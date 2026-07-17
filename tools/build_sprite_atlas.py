from __future__ import annotations

import argparse
import hashlib
import json
import os
from dataclasses import dataclass, field
from pathlib import Path
import time

from PIL import Image

from split_sprite_sheet import resize_rgba_premultiplied


DISPLAY_WIDTH = 145
DISPLAY_HEIGHT = 185
TRANSPARENT_GUTTER = 2
REUSABLE_PAGE_MAX_WIDTH = 1024


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


def resource_paths() -> list[str]:
    paths = ["Assets/luban-idle.png"]
    paths.extend(f"Assets/luban-wake-{number:02}.png" for number in range(1, 15))
    for edge in ("left", "top", "bottom"):
        paths.extend(
            f"Assets/luban-edge-{edge}-{number:02}.png"
            for number in range(1, 5)
        )
    for mode in ("wriggle", "crawl", "hop"):
        for direction in ("horizontal", "vertical-up", "vertical-down"):
            paths.extend(
                f"Assets/luban-roam-{mode}-{direction}-{number:02}.png"
                for number in range(1, 9)
            )
    for action in ("yawn", "cry", "run", "cute", "like", "eat", "wave", "think"):
        paths.extend(
            f"Assets/luban-{action}-frame-{number:02}.png"
            for number in range(1, 25)
        )
    if len(paths) != 291 or len(set(paths)) != len(paths):
        raise RuntimeError(f"Expected 291 unique resources, got {len(paths)}")
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
    width, height, placements = simulate_shelf_pack(
        sprites,
        REUSABLE_PAGE_MAX_WIDTH,
    )
    if width > 4096 or height > 4096:
        raise RuntimeError("Could not pack sprite atlas within 4096x4096")
    for sprite, atlas_x, atlas_y in placements:
        sprite.atlas_x = atlas_x
        sprite.atlas_y = atlas_y
    return width, height


def page_resource_paths() -> dict[str, list[str]]:
    idle = "Assets/luban-idle.png"
    wake = [f"Assets/luban-wake-{number:02}.png" for number in range(1, 15)]
    pages: dict[str, list[str]] = {"idle": [idle]}
    for action in ("yawn", "cry", "run", "cute", "like", "eat", "wave", "think"):
        pages[f"action-{action}"] = [idle, *wake, *[
            f"Assets/luban-{action}-frame-{number:02}.png"
            for number in range(1, 25)
        ]]
    pages["edge"] = [idle, *[
        f"Assets/luban-edge-{edge}-{number:02}.png"
        for edge in ("left", "top", "bottom")
        for number in range(1, 5)
    ]]
    for mode in ("wriggle", "crawl", "hop"):
        pages[f"roam-{mode}"] = [idle, *[
            f"Assets/luban-roam-{mode}-{direction}-{number:02}.png"
            for direction in ("horizontal", "vertical-up", "vertical-down")
            for number in range(1, 9)
        ]]

    expected = set(resource_paths())
    actual = {path for paths in pages.values() for path in paths}
    if actual != expected:
        raise RuntimeError(
            f"Page source union mismatch: missing={sorted(expected - actual)}, "
            f"extra={sorted(actual - expected)}"
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
    print(
        f"  {page_name}: {atlas_width}x{atlas_height}, "
        f"{len(ordered_frames)} frames, {len(sprites)} unique"
    )
    return {
        "resource": atlas_path.relative_to(root).as_posix(),
        "width": atlas_width,
        "height": atlas_height,
        "logicalFrameCount": len(ordered_frames),
        "uniqueSpriteCount": len(sprites),
        "frames": ordered_frames,
    }


def write_outputs(
    root: Path,
    output_directory: Path,
    manifest_path: Path,
) -> None:
    pages = page_resource_paths()
    output_directory.mkdir(parents=True, exist_ok=True)
    manifest_pages: dict[str, dict[str, object]] = {}
    print(f"Building {len(pages)} sprite pages:")
    for page_name, paths in pages.items():
        manifest_pages[page_name] = build_page(
            root,
            page_name,
            paths,
            output_directory / f"luban-{page_name}.png",
        )

    manifest = {
        "version": 2,
        "displayWidth": DISPLAY_WIDTH,
        "displayHeight": DISPLAY_HEIGHT,
        "sourceFrameCount": len(resource_paths()),
        "pageFrameCount": sum(len(paths) for paths in pages.values()),
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
