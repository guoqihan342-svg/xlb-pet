from __future__ import annotations

import argparse
import hashlib
import json
import os
from dataclasses import dataclass, field
from pathlib import Path
import time

import lz4.block
import numpy as np
from PIL import Image

from split_sprite_sheet import resize_rgba_premultiplied


DISPLAY_WIDTH = 399
DISPLAY_HEIGHT = 509
TRANSPARENT_GUTTER = 2
REUSABLE_PAGE_MAX_WIDTH = 1540
MAX_DECODED_PAGE_BYTES = 24 * 1024 * 1024
ACTION_NAMES = ("yawn", "cry", "cute", "like", "eat", "wave", "think")
WAKE_FRAME_COUNT = 27
ACTION_ENTRY_BRIDGES = frozenset(ACTION_NAMES)
ACTION_INTERNAL_BRIDGES = {
    "yawn": (6,),
    "cry": (3,),
    "think": (6,),
}


def replace_with_retry(temporary: Path, destination: Path) -> None:
    for attempt in range(5):
        try:
            temporary.replace(destination)
            return
        except OSError:
            if attempt == 4:
                raise
            time.sleep(0.05 * (attempt + 1))


def save_png_atomically(image: Image.Image, destination: Path) -> None:
    temporary = destination.with_name(
        f".{destination.stem}.{os.getpid()}.tmp.png"
    )
    temporary.unlink(missing_ok=True)
    try:
        image.save(
            temporary,
            format="PNG",
            optimize=True,
            compress_level=9,
        )
        replace_with_retry(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def write_text_atomically(destination: Path, content: str) -> None:
    temporary = destination.with_name(
        f".{destination.stem}.{os.getpid()}.tmp{destination.suffix}"
    )
    temporary.unlink(missing_ok=True)
    try:
        temporary.write_text(content, encoding="utf-8")
        replace_with_retry(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def write_bytes_atomically(destination: Path, content: bytes) -> None:
    temporary = destination.with_name(
        f".{destination.stem}.{os.getpid()}.tmp{destination.suffix}"
    )
    temporary.unlink(missing_ok=True)
    try:
        temporary.write_bytes(content)
        replace_with_retry(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def encode_pbgra32_lz4(image: Image.Image) -> bytes:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint16)
    alpha = rgba[:, :, 3:4]
    premultiplied = ((rgba[:, :, :3] * alpha + 127) // 255).astype(np.uint8)
    pbgra = np.empty(rgba.shape, dtype=np.uint8)
    pbgra[:, :, 0] = premultiplied[:, :, 2]
    pbgra[:, :, 1] = premultiplied[:, :, 1]
    pbgra[:, :, 2] = premultiplied[:, :, 0]
    pbgra[:, :, 3] = alpha[:, :, 0].astype(np.uint8)
    return lz4.block.compress(
        pbgra.tobytes(),
        # High-compression mode changes only the on-disk LZ4 representation;
        # the decoder still reconstructs the exact same Pbgra32 bytes.  Level
        # 9 keeps the single-file publish safely below GitHub's 100 MB object
        # limit without trading runtime memory or sprite quality for size.
        mode="high_compression",
        compression=9,
        store_size=False,
    )


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_set_fingerprint(root: Path, paths: list[str]) -> str:
    digest = hashlib.sha256()
    for resource_path in paths:
        digest.update(resource_path.encode("utf-8"))
        digest.update(b"\0")
        digest.update(bytes.fromhex(file_sha256(root / resource_path)))
    return digest.hexdigest()


@dataclass
class PackedSprite:
    pixels: bytes
    width: int
    height: int
    destination_x: int
    destination_y: int
    resource_paths: list[str] = field(default_factory=list)
    atlas_x: int = 0
    atlas_y: int = 0


def action_resource_paths(action: str) -> list[str]:
    paths: list[str] = []
    if action in ACTION_ENTRY_BRIDGES:
        paths.append(f"Assets/luban-{action}-entry-bridge.png")
    bridge_after_frames = ACTION_INTERNAL_BRIDGES.get(action, ())
    for number in range(1, 25):
        paths.append(f"Assets/luban-{action}-frame-{number:02}.png")
        if number in bridge_after_frames:
            paths.append(
                f"Assets/luban-{action}-bridge-{number:02}-{number + 1:02}.png"
            )
    return paths


def resource_paths(root: Path) -> list[str]:
    paths = ["Assets/luban-idle.png"]
    paths.extend(
        f"Assets/luban-wake-{number:02}.png"
        for number in range(1, WAKE_FRAME_COUNT + 1)
    )
    for edge in ("left", "top", "bottom"):
        paths.extend(
            f"Assets/luban-edge-{edge}-{number:02}.png"
            for number in range(1, 5)
    )
    for action in ACTION_NAMES:
        paths.extend(action_resource_paths(action))
    if len(set(paths)) != len(paths):
        raise RuntimeError(
            f"Sprite resource list contains duplicates: {len(paths)} paths"
        )
    return paths


def make_display_frame(path: Path, resource_path: str) -> Image.Image:
    with Image.open(path) as source:
        source = source.convert("RGBA")
        target_height = round(source.height * DISPLAY_WIDTH / source.width)
        resized = resize_rgba_premultiplied(
            source,
            (DISPLAY_WIDTH, target_height),
        )

    frame = Image.new("RGBA", (DISPLAY_WIDTH, DISPLAY_HEIGHT), (0, 0, 0, 0))
    destination_x = max(0, (DISPLAY_WIDTH - resized.width) // 2)
    destination_y = (
        0
        if "luban-edge-top-" in resource_path.lower()
        else DISPLAY_HEIGHT - resized.height
    )
    frame.alpha_composite(resized, (destination_x, destination_y))
    return frame


def crop_with_transparent_gutter(frame: Image.Image) -> tuple[Image.Image, int, int]:
    bounds = frame.getchannel("A").getbbox()
    if bounds is None:
        return Image.new("RGBA", (1, 1), (0, 0, 0, 0)), 0, 0

    content = frame.crop(bounds)
    cropped = Image.new(
        "RGBA",
        (
            content.width + TRANSPARENT_GUTTER * 2,
            content.height + TRANSPARENT_GUTTER * 2,
        ),
        (0, 0, 0, 0),
    )
    cropped.alpha_composite(content, (TRANSPARENT_GUTTER, TRANSPARENT_GUTTER))
    return (
        cropped,
        bounds[0] - TRANSPARENT_GUTTER,
        bounds[1] - TRANSPARENT_GUTTER,
    )


def sprite_fingerprint(
    pixels: bytes,
    width: int,
    height: int,
    destination_x: int,
    destination_y: int,
) -> str:
    digest = hashlib.sha256()
    digest.update(
        f"{width},{height},{destination_x},{destination_y}:".encode("ascii")
    )
    digest.update(pixels)
    return digest.hexdigest()


def build_unique_sprites(root: Path, paths: list[str]) -> list[PackedSprite]:
    unique: dict[str, PackedSprite] = {}
    for resource_path in paths:
        frame = make_display_frame(root / resource_path, resource_path)
        cropped, destination_x, destination_y = crop_with_transparent_gutter(frame)
        pixels = cropped.tobytes()
        fingerprint = sprite_fingerprint(
            pixels,
            cropped.width,
            cropped.height,
            destination_x,
            destination_y,
        )
        sprite = unique.get(fingerprint)
        if sprite is None:
            sprite = PackedSprite(
                pixels=pixels,
                width=cropped.width,
                height=cropped.height,
                destination_x=destination_x,
                destination_y=destination_y,
            )
            unique[fingerprint] = sprite
        elif (
            sprite.pixels != pixels
            or sprite.width != cropped.width
            or sprite.height != cropped.height
            or sprite.destination_x != destination_x
            or sprite.destination_y != destination_y
        ):
            raise RuntimeError("SHA-256 collision while building sprite atlas")
        sprite.resource_paths.append(resource_path)
    return list(unique.values())


def simulate_shelf_pack(
    sprites: list[PackedSprite],
    maximum_width: int,
) -> tuple[int, int, list[tuple[PackedSprite, int, int]]]:
    ordered = sorted(
        sprites,
        key=lambda sprite: (-sprite.height, -sprite.width, sprite.resource_paths[0]),
    )
    x = 0
    y = 0
    row_height = 0
    used_width = 0
    placements: list[tuple[PackedSprite, int, int]] = []
    for sprite in ordered:
        if sprite.width > maximum_width:
            raise RuntimeError(
                f"Sprite {sprite.resource_paths[0]} is wider than atlas candidate"
            )
        if x > 0 and x + sprite.width > maximum_width:
            x = 0
            y += row_height
            row_height = 0
        placements.append((sprite, x, y))
        x += sprite.width
        used_width = max(used_width, x)
        row_height = max(row_height, sprite.height)
    return used_width, y + row_height, placements


def pack_sprites(sprites: list[PackedSprite]) -> tuple[int, int]:
    # Search deterministic shelf widths and retain the smallest-area valid
    # layout while keeping every decoded page under the runtime memory limit.
    minimum_width = max(
        REUSABLE_PAGE_MAX_WIDTH,
        max(sprite.width for sprite in sprites),
    )
    candidates: list[
        tuple[int, int, int, list[tuple[PackedSprite, int, int]]]
    ] = []
    candidate_widths = [
        *range(minimum_width, 4097, 32),
        4096,
    ]
    for maximum_width in dict.fromkeys(candidate_widths):
        width, height, placements = simulate_shelf_pack(
            sprites,
            maximum_width,
        )
        if (
            width <= 4096
            and height <= 4096
            and width * height * 4 <= MAX_DECODED_PAGE_BYTES
        ):
            candidates.append((
                width * height,
                max(width, height),
                width,
                placements,
            ))
    if not candidates:
        raise RuntimeError("Could not pack sprite atlas within 4096x4096")
    _, _, width, placements = min(candidates, key=lambda item: item[:3])
    height = max(
        atlas_y + sprite.height
        for sprite, _, atlas_y in placements
    )
    for sprite, atlas_x, atlas_y in placements:
        sprite.atlas_x = atlas_x
        sprite.atlas_y = atlas_y
    return width, height


def page_resource_paths(root: Path) -> dict[str, list[str]]:
    idle = "Assets/luban-idle.png"
    wake = [
        f"Assets/luban-wake-{number:02}.png"
        for number in range(1, WAKE_FRAME_COUNT + 1)
    ]
    # The shared idle/wake page is always hot. Each action page contains its
    # own poses and approved transition bridges, so wake pixels are not
    # duplicated seven times and a cold action page can decode in the
    # background while the wake path plays.
    edge = [
        f"Assets/luban-edge-{edge_name}-{number:02}.png"
        for edge_name in ("left", "top", "bottom")
        for number in range(1, 5)
    ]
    # Idle, wake, and manual edge-peek frames share one always-hot page. A
    # drag release can therefore switch to the first edge pose atomically,
    # without a cold page decode advancing the edge clock before it is shown.
    pages: dict[str, list[str]] = {"idle": [idle, *wake, *edge]}
    for action in ACTION_NAMES:
        pages[f"action-{action}"] = action_resource_paths(action)
    expected = set(resource_paths(root))
    actual = {path for paths in pages.values() for path in paths}
    if actual != expected:
        raise RuntimeError(
            f"Page source union mismatch: missing={sorted(expected - actual)}, "
            f"extra={sorted(actual - expected)}"
        )
    duplicate_page_paths = {
        page_name: len(paths) - len(set(paths))
        for page_name, paths in pages.items()
        if len(paths) != len(set(paths))
    }
    if duplicate_page_paths:
        raise RuntimeError(
            f"Page resource lists contain duplicates: {duplicate_page_paths}"
        )
    return pages


def build_page(
    root: Path,
    page_name: str,
    paths: list[str],
    atlas_path: Path,
) -> dict[str, object]:
    sprites = build_unique_sprites(root, paths)
    atlas_width, atlas_height = pack_sprites(sprites)
    uncompressed_byte_count = atlas_width * atlas_height * 4
    if uncompressed_byte_count > MAX_DECODED_PAGE_BYTES:
        raise RuntimeError(
            f"Atlas page {page_name} decodes to {uncompressed_byte_count} bytes, "
            f"above the {MAX_DECODED_PAGE_BYTES}-byte reusable-buffer limit"
        )
    atlas = Image.new("RGBA", (atlas_width, atlas_height), (0, 0, 0, 0))
    frames: dict[str, dict[str, int]] = {}
    for sprite in sprites:
        image = Image.frombytes("RGBA", (sprite.width, sprite.height), sprite.pixels)
        atlas.alpha_composite(image, (sprite.atlas_x, sprite.atlas_y))
        descriptor = {
            "x": sprite.atlas_x,
            "y": sprite.atlas_y,
            "width": sprite.width,
            "height": sprite.height,
            "destinationX": sprite.destination_x,
            "destinationY": sprite.destination_y,
        }
        for resource_path in sprite.resource_paths:
            frames[resource_path] = descriptor

    ordered_frames = {path: frames[path] for path in paths}
    atlas_path.parent.mkdir(parents=True, exist_ok=True)
    save_png_atomically(atlas, atlas_path)
    runtime_path = atlas_path.with_suffix(".pbgra.lz4")
    runtime_bytes = encode_pbgra32_lz4(atlas)
    write_bytes_atomically(runtime_path, runtime_bytes)
    source_fingerprint = source_set_fingerprint(root, paths)
    content_sha256 = hashlib.sha256(runtime_bytes).hexdigest()
    preview_sha256 = file_sha256(atlas_path)
    print(
        f"  {page_name}: {atlas_width}x{atlas_height}, "
        f"{len(ordered_frames)} frames, {len(sprites)} unique, "
        f"{runtime_path.stat().st_size / 1024 / 1024:.2f} MiB LZ4"
    )
    return {
        "resource": runtime_path.relative_to(root).as_posix(),
        "previewResource": atlas_path.relative_to(root).as_posix(),
        "width": atlas_width,
        "height": atlas_height,
        "uncompressedByteCount": uncompressed_byte_count,
        "compressedByteCount": len(runtime_bytes),
        "sourceFingerprint": source_fingerprint,
        "contentSha256": content_sha256,
        "previewSha256": preview_sha256,
        "logicalFrameCount": len(ordered_frames),
        "uniqueSpriteCount": len(sprites),
        "frames": ordered_frames,
    }


def write_outputs(
    root: Path,
    output_directory: Path,
    manifest_path: Path,
) -> None:
    pages = page_resource_paths(root)
    output_directory.mkdir(parents=True, exist_ok=True)
    expected_page_files = {
        name
        for page_name in pages
        for name in (
            f"luban-{page_name}.png",
            f"luban-{page_name}.pbgra.lz4",
        )
    }
    for pattern in ("luban-*.png", "luban-*.pbgra.lz4"):
        for stale_page in output_directory.glob(pattern):
            if stale_page.name not in expected_page_files:
                stale_page.unlink()
    manifest_pages: dict[str, dict[str, object]] = {}
    print(f"Building {len(pages)} sprite pages:")
    for page_name, paths in pages.items():
        manifest_pages[page_name] = build_page(
            root,
            page_name,
            paths,
            output_directory / f"luban-{page_name}.png",
        )

    source_paths = resource_paths(root)
    source_frame_count = len(source_paths)
    page_frame_count = sum(len(paths) for paths in pages.values())
    if source_frame_count != len({path for paths in pages.values() for path in paths}):
        raise RuntimeError("Manifest source frame count does not match page union")
    if page_frame_count != sum(
        int(page["logicalFrameCount"])
        for page in manifest_pages.values()
    ):
        raise RuntimeError("Manifest page frame count does not match built pages")

    manifest = {
        "version": 3,
        "displayWidth": DISPLAY_WIDTH,
        "displayHeight": DISPLAY_HEIGHT,
        "sourceFrameCount": source_frame_count,
        "pageFrameCount": page_frame_count,
        "sourceSetFingerprint": source_set_fingerprint(root, source_paths),
        "maxDecodedPageBytes": MAX_DECODED_PAGE_BYTES,
        "pages": manifest_pages,
    }
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    write_text_atomically(
        manifest_path,
        json.dumps(manifest, ensure_ascii=False, separators=(",", ":")) + "\n",
    )
    print(
        f"Wrote {manifest_path}; {manifest['sourceFrameCount']} source frames, "
        f"{manifest['pageFrameCount']} page-local frames."
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build compact, transparent-guttered desktop-pet sprite pages."
    )
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path("Assets/sprite-pages"),
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path("Assets/luban-sprite-pages.json"),
    )
    args = parser.parse_args()
    root = args.root.resolve()
    output_directory = (
        args.output_dir
        if args.output_dir.is_absolute()
        else root / args.output_dir
    )
    manifest_path = (
        args.manifest if args.manifest.is_absolute() else root / args.manifest
    )
    write_outputs(root, output_directory, manifest_path)


if __name__ == "__main__":
    main()
