from __future__ import annotations

import argparse
import brotli
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from dataclasses import dataclass, field
from pathlib import Path
import re
import struct
import time

import numpy as np
from PIL import Image

from split_sprite_sheet import resize_rgba_premultiplied


DISPLAY_WIDTH = 399
DISPLAY_HEIGHT = 509
TRANSPARENT_GUTTER = 2
REUSABLE_PAGE_MAX_WIDTH = 1540
MAX_DECODED_PAGE_BYTES = 24 * 1024 * 1024
ACTION_NAMES = ("yawn", "cry", "cute", "like", "eat", "wave", "think")
ACTION_LOOP_FRAME_COUNT = 48
EDGE_PEEK_FRAME_COUNT = 24
WAKE_PAGE_FRAME_LIMIT = 32
WAKE_PAGE_MIN_PREFETCH_FRAMES = 8
ACTION_PAGE_FRAME_LIMIT = 32
ACTION_PAGE_MIN_PREFETCH_FRAMES = 8
DELTA_SUB_HEADER = struct.Struct("<4H")
DELTA_SUB_ENCODING = "pbgra32-delta-sub-v1"
DIRECT_ENCODING = "pbgra32"
DELTA_MIN_SAVING_BYTES = 256 * 1024
DELTA_MIN_SAVING_PERCENT = 10
MAX_DELTA_PAYLOAD_BYTES = 32 * 1024 * 1024


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


def image_to_pbgra32(image: Image.Image) -> np.ndarray:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint16)
    alpha = rgba[:, :, 3:4]
    premultiplied = ((rgba[:, :, :3] * alpha + 127) // 255).astype(np.uint8)
    pbgra = np.empty(rgba.shape, dtype=np.uint8)
    pbgra[:, :, 0] = premultiplied[:, :, 2]
    pbgra[:, :, 1] = premultiplied[:, :, 1]
    pbgra[:, :, 2] = premultiplied[:, :, 0]
    pbgra[:, :, 3] = alpha[:, :, 0].astype(np.uint8)
    return pbgra


def encode_brotli(payload: bytes) -> bytes:
    return brotli.compress(
        payload,
        # Quality 11 is intentionally an offline-build cost. It reconstructs
        # the exact Pbgra32 bytes while keeping the much denser 60 fps atlas
        # below GitHub's single-object limit. Runtime decoding stays on the
        # background page loader and never runs in CompositionTarget.Rendering.
        mode=brotli.MODE_GENERIC,
        quality=11,
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


@dataclass
class BuiltPage:
    manifest: dict[str, object]
    direct_compressed_byte_count: int
    delta_compressed_byte_count: int
    selected_saving_byte_count: int


def build_delta_sub_payload(root: Path, paths: list[str]) -> bytes:
    """Encode full-canvas frame deltas in page path order."""

    previous = np.zeros(
        (DISPLAY_HEIGHT, DISPLAY_WIDTH, 4),
        dtype=np.uint8,
    )
    payload = bytearray()
    for resource_path in paths:
        frame = make_display_frame(root / resource_path, resource_path)
        try:
            current = image_to_pbgra32(frame)
        finally:
            frame.close()

        changed = np.any(current != previous, axis=2)
        changed_rows = np.flatnonzero(np.any(changed, axis=1))
        changed_columns = np.flatnonzero(np.any(changed, axis=0))
        if changed_rows.size == 0:
            payload.extend(DELTA_SUB_HEADER.pack(0, 0, 0, 0))
            previous = current
            continue

        x = int(changed_columns[0])
        y = int(changed_rows[0])
        width = int(changed_columns[-1]) - x + 1
        height = int(changed_rows[-1]) - y + 1
        payload.extend(DELTA_SUB_HEADER.pack(x, y, width, height))
        delta = np.subtract(
            current[y : y + height, x : x + width],
            previous[y : y + height, x : x + width],
            dtype=np.uint8,
        )
        payload.extend(delta.tobytes())
        previous = current
    return bytes(payload)


def reconstruct_delta_sub_atlas(
    payload: bytes,
    paths: list[str],
    frames: dict[str, dict[str, int]],
    atlas_width: int,
    atlas_height: int,
) -> bytes:
    """Rebuild final atlas bytes and fail closed on malformed delta data."""

    previous = np.zeros(
        (DISPLAY_HEIGHT, DISPLAY_WIDTH, 4),
        dtype=np.uint8,
    )
    atlas = np.zeros((atlas_height, atlas_width, 4), dtype=np.uint8)
    offset = 0
    for resource_path in paths:
        if offset + DELTA_SUB_HEADER.size > len(payload):
            raise RuntimeError("Delta-sub payload ends before a frame header")
        x, y, width, height = DELTA_SUB_HEADER.unpack_from(payload, offset)
        offset += DELTA_SUB_HEADER.size
        if width == 0 or height == 0:
            if (x, y, width, height) != (0, 0, 0, 0):
                raise RuntimeError(
                    "An unchanged delta-sub frame must use a zero header"
                )
        if x + width > DISPLAY_WIDTH or y + height > DISPLAY_HEIGHT:
            raise RuntimeError("Delta-sub rectangle exceeds the display canvas")
        block_byte_count = width * height * 4
        if offset + block_byte_count > len(payload):
            raise RuntimeError("Delta-sub payload ends inside a frame block")
        if block_byte_count:
            delta = np.frombuffer(
                payload,
                dtype=np.uint8,
                count=block_byte_count,
                offset=offset,
            ).reshape((height, width, 4))
            region = previous[y : y + height, x : x + width]
            np.add(region, delta, out=region)
            offset += block_byte_count

        descriptor = frames[resource_path]
        sprite_x = descriptor["x"]
        sprite_y = descriptor["y"]
        sprite_width = descriptor["width"]
        sprite_height = descriptor["height"]
        destination_x = descriptor["destinationX"]
        destination_y = descriptor["destinationY"]
        crop = np.zeros((sprite_height, sprite_width, 4), dtype=np.uint8)
        source_x0 = max(0, destination_x)
        source_y0 = max(0, destination_y)
        source_x1 = min(DISPLAY_WIDTH, destination_x + sprite_width)
        source_y1 = min(DISPLAY_HEIGHT, destination_y + sprite_height)
        if source_x1 > source_x0 and source_y1 > source_y0:
            target_x0 = source_x0 - destination_x
            target_y0 = source_y0 - destination_y
            crop[
                target_y0 : target_y0 + source_y1 - source_y0,
                target_x0 : target_x0 + source_x1 - source_x0,
            ] = previous[source_y0:source_y1, source_x0:source_x1]
        atlas[
            sprite_y : sprite_y + sprite_height,
            sprite_x : sprite_x + sprite_width,
        ] = crop

    if offset != len(payload):
        raise RuntimeError(
            f"Delta-sub payload has {len(payload) - offset} trailing bytes"
        )
    return atlas.tobytes()


def numbered_resource_paths(
    root: Path,
    prefix: str,
    *,
    expected_count: int | None = None,
) -> list[str]:
    """Return a fail-closed, numerically sorted three-digit PNG sequence."""

    assets_directory = root / "Assets"
    expression = re.compile(rf"^{re.escape(prefix)}-(\d{{3}})\.png$")
    numbered: list[tuple[int, str]] = []
    for path in assets_directory.glob(f"{prefix}-*.png"):
        match = expression.fullmatch(path.name)
        if match is None:
            raise RuntimeError(
                f"Malformed dense sprite name for {prefix}: {path.name}"
            )
        numbered.append((int(match.group(1)), f"Assets/{path.name}"))

    numbered.sort(key=lambda item: item[0])
    numbers = [number for number, _ in numbered]
    if not numbered or numbers != list(range(1, len(numbered) + 1)):
        raise RuntimeError(
            f"Dense sprite sequence {prefix} must be contiguous from 001; "
            f"found {numbers[:8]}{'...' if len(numbers) > 8 else ''}"
        )
    if expected_count is not None and len(numbered) != expected_count:
        raise RuntimeError(
            f"Dense sprite sequence {prefix} must contain exactly "
            f"{expected_count} frames, found {len(numbered)}"
        )
    return [resource_path for _, resource_path in numbered]


def wake_resource_paths(root: Path) -> list[str]:
    return numbered_resource_paths(root, "luban-wake-smooth")


def action_resource_paths(root: Path, action: str) -> list[str]:
    return numbered_resource_paths(root, f"luban-{action}-smooth")


def action_loop_resource_paths(root: Path, action: str) -> list[str]:
    return numbered_resource_paths(
        root,
        f"luban-{action}-loop",
        expected_count=ACTION_LOOP_FRAME_COUNT,
    )


def edge_peek_resource_paths(root: Path, direction: str) -> list[str]:
    return numbered_resource_paths(
        root,
        f"luban-edge-{direction}-smooth",
        expected_count=EDGE_PEEK_FRAME_COUNT,
    )


def partition_wake_resource_paths(
    wake_paths: list[str],
    edge_paths: list[str],
) -> list[tuple[str, list[str]]]:
    """Keep idle/edge hot while splitting an arbitrary positive wake sequence."""

    if not wake_paths:
        raise RuntimeError("Dense wake sequence must contain at least one frame")

    chunks = [
        list(wake_paths[offset : offset + WAKE_PAGE_FRAME_LIMIT])
        for offset in range(0, len(wake_paths), WAKE_PAGE_FRAME_LIMIT)
    ]
    if (
        len(chunks) > 1
        and len(chunks[-1]) < WAKE_PAGE_MIN_PREFETCH_FRAMES
    ):
        transfer_count = WAKE_PAGE_MIN_PREFETCH_FRAMES - len(chunks[-1])
        transfer_start = len(chunks[-2]) - transfer_count
        if transfer_start <= 0:
            raise RuntimeError(
                "Dense wake sequence cannot reserve a final "
                f"{WAKE_PAGE_MIN_PREFETCH_FRAMES}-frame prefetch window"
            )
        chunks[-1] = chunks[-2][transfer_start:] + chunks[-1]
        chunks[-2] = chunks[-2][:transfer_start]

    partitions: list[tuple[str, list[str]]] = []
    for part_number, chunk in enumerate(chunks, start=1):
        page_name = (
            "idle"
            if part_number == 1
            else f"idle-part-{part_number:02d}"
        )
        page_paths = (
            ["Assets/luban-idle.png", *chunk, *edge_paths]
            if part_number == 1
            else chunk
        )
        partitions.append((page_name, page_paths))

    expected_page_names = [
        "idle"
        if part_number == 1
        else f"idle-part-{part_number:02d}"
        for part_number in range(1, len(partitions) + 1)
    ]
    actual_page_names = [page_name for page_name, _ in partitions]
    invalid_page_names = [
        page_name
        for page_name in actual_page_names
        if page_name != "idle"
        and re.fullmatch(r"idle-part-\d{2}", page_name) is None
    ]
    expected_first_page = [
        "Assets/luban-idle.png",
        *chunks[0],
        *edge_paths,
    ]
    flattened_wake_paths = [path for chunk in chunks for path in chunk]
    trailing_pages_contain_only_wake = all(
        partition_paths == chunks[index]
        for index, (_, partition_paths) in enumerate(partitions[1:], start=1)
    )
    if (
        actual_page_names != expected_page_names
        or invalid_page_names
        or partitions[0][1] != expected_first_page
        or not trailing_pages_contain_only_wake
        or flattened_wake_paths != wake_paths
        or any(
            not chunk or len(chunk) > WAKE_PAGE_FRAME_LIMIT
            for chunk in chunks
        )
        or (
            len(chunks) > 1
            and len(chunks[-1]) < WAKE_PAGE_MIN_PREFETCH_FRAMES
        )
    ):
        raise RuntimeError(
            "Invalid continuous idle/wake page partition: "
            f"pages={actual_page_names}, wakeSizes="
            f"{[len(chunk) for chunk in chunks]}"
        )

    return partitions


def partition_action_resource_paths(
    action: str,
    paths: list[str],
) -> list[tuple[str, list[str]]]:
    """Split one action into ordered pages with a usable final prefetch window."""

    if len(paths) < ACTION_PAGE_MIN_PREFETCH_FRAMES:
        raise RuntimeError(
            f"Dense action {action} must contain at least "
            f"{ACTION_PAGE_MIN_PREFETCH_FRAMES} frames so the loop page can be "
            f"prefetched; found {len(paths)}"
        )

    chunks = [
        list(paths[offset : offset + ACTION_PAGE_FRAME_LIMIT])
        for offset in range(0, len(paths), ACTION_PAGE_FRAME_LIMIT)
    ]
    if (
        len(chunks) > 1
        and len(chunks[-1]) < ACTION_PAGE_MIN_PREFETCH_FRAMES
    ):
        transfer_count = ACTION_PAGE_MIN_PREFETCH_FRAMES - len(chunks[-1])
        transfer_start = len(chunks[-2]) - transfer_count
        if transfer_start <= 0:
            raise RuntimeError(
                f"Dense action {action} cannot reserve a final "
                f"{ACTION_PAGE_MIN_PREFETCH_FRAMES}-frame prefetch window"
            )
        chunks[-1] = chunks[-2][transfer_start:] + chunks[-1]
        chunks[-2] = chunks[-2][:transfer_start]

    base_page_name = f"action-{action}"
    partitions = [
        (
            base_page_name
            if part_number == 1
            else f"{base_page_name}-part-{part_number:02d}",
            chunk,
        )
        for part_number, chunk in enumerate(chunks, start=1)
    ]
    expected_page_names = [
        base_page_name
        if part_number == 1
        else f"{base_page_name}-part-{part_number:02d}"
        for part_number in range(1, len(partitions) + 1)
    ]
    actual_page_names = [page_name for page_name, _ in partitions]
    flattened_paths = [
        path
        for _, partition_paths in partitions
        for path in partition_paths
    ]
    invalid_page_names = [
        page_name
        for page_name in actual_page_names
        if page_name != base_page_name
        and re.fullmatch(
            rf"{re.escape(base_page_name)}-part-\d{{2}}",
            page_name,
        )
        is None
    ]
    if (
        actual_page_names != expected_page_names
        or invalid_page_names
        or flattened_paths != paths
        or any(
            not partition_paths
            or len(partition_paths) > ACTION_PAGE_FRAME_LIMIT
            for _, partition_paths in partitions
        )
        or len(partitions[-1][1]) < ACTION_PAGE_MIN_PREFETCH_FRAMES
    ):
        raise RuntimeError(
            f"Invalid continuous action page partition for {action}: "
            f"pages={actual_page_names}, sizes="
            f"{[len(partition_paths) for _, partition_paths in partitions]}"
        )

    return partitions


def resource_paths(root: Path) -> list[str]:
    wake = wake_resource_paths(root)
    paths = [
        path
        for _, partition_paths in partition_wake_resource_paths(wake, [])
        for path in partition_paths
    ]
    for direction in ("left", "top", "bottom"):
        paths.extend(edge_peek_resource_paths(root, direction))
    for action in ACTION_NAMES:
        paths.extend(action_resource_paths(root, action))
        paths.extend(action_loop_resource_paths(root, action))
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
    wake = wake_resource_paths(root)
    # Wake, dense actions, and natural loops use bounded sequential pages so
    # every decoded page can remain below the fixed 24 MiB runtime limit.
    # Dense edge-peek loops live on three small direction pages. Keeping all
    # 72 frames on the idle page would violate the fixed 24 MiB decoded-page
    # budget; the runtime can prefetch the one direction selected by dragging.
    pages: dict[str, list[str]] = {}
    wake_partitions = partition_wake_resource_paths(wake, [])
    first_page_name, first_page_paths = wake_partitions[0]
    pages[first_page_name] = first_page_paths
    # Manifest order is also the background warm-up priority. Put all three
    # edge directions immediately after the primary idle page, before trailing
    # wake/action pages, so entering edge mode does not flash a cold fallback.
    for direction in ("left", "top", "bottom"):
        pages[f"edge-{direction}"] = edge_peek_resource_paths(root, direction)
    for page_name, partition_paths in wake_partitions[1:]:
        if page_name in pages:
            raise RuntimeError(f"Duplicate sprite page name: {page_name}")
        pages[page_name] = partition_paths
    for action in ACTION_NAMES:
        action_paths = action_resource_paths(root, action)
        for page_name, partition_paths in partition_action_resource_paths(
            action,
            action_paths,
        ):
            if page_name in pages:
                raise RuntimeError(f"Duplicate sprite page name: {page_name}")
            pages[page_name] = partition_paths
        pages[f"loop-{action}"] = action_loop_resource_paths(root, action)
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
    resource_page_owners: dict[str, list[str]] = {}
    for page_name, paths in pages.items():
        for path in paths:
            resource_page_owners.setdefault(path, []).append(page_name)
    cross_page_duplicates = {
        path: owner_pages
        for path, owner_pages in resource_page_owners.items()
        if len(set(owner_pages)) > 1
    }
    if cross_page_duplicates:
        raise RuntimeError(
            f"Sprite resources appear on multiple pages: {cross_page_duplicates}"
        )
    if sum(len(paths) for paths in pages.values()) != len(actual):
        raise RuntimeError(
            "Page frame count must equal the unique source union; duplicated "
            "page-local resources are not allowed"
        )
    return pages


def build_page(
    root: Path,
    page_name: str,
    paths: list[str],
    atlas_path: Path,
) -> BuiltPage:
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
    runtime_path = atlas_path.with_suffix(".pbgra.br")
    decoded_atlas = image_to_pbgra32(atlas).tobytes()
    if len(decoded_atlas) != uncompressed_byte_count:
        raise RuntimeError(
            f"Atlas page {page_name} Pbgra32 length changed unexpectedly: "
            f"{len(decoded_atlas)} != {uncompressed_byte_count}"
        )
    delta_payload = build_delta_sub_payload(root, paths)
    reconstructed_atlas = reconstruct_delta_sub_atlas(
        delta_payload,
        paths,
        ordered_frames,
        atlas_width,
        atlas_height,
    )
    if reconstructed_atlas != decoded_atlas:
        mismatch_count = sum(
            left != right
            for left, right in zip(reconstructed_atlas, decoded_atlas)
        )
        raise RuntimeError(
            f"Atlas page {page_name} delta-sub round trip changed "
            f"{mismatch_count} decoded bytes"
        )

    direct_runtime_bytes = encode_brotli(decoded_atlas)
    delta_runtime_bytes = encode_brotli(delta_payload)
    delta_saving = len(direct_runtime_bytes) - len(delta_runtime_bytes)
    use_delta = (
        len(delta_payload) <= MAX_DELTA_PAYLOAD_BYTES
        and delta_saving >= DELTA_MIN_SAVING_BYTES
        and delta_saving * 100
        >= len(direct_runtime_bytes) * DELTA_MIN_SAVING_PERCENT
    )
    encoding = DELTA_SUB_ENCODING if use_delta else DIRECT_ENCODING
    runtime_payload = delta_payload if use_delta else decoded_atlas
    runtime_bytes = delta_runtime_bytes if use_delta else direct_runtime_bytes
    if len(runtime_bytes) > len(runtime_payload):
        raise RuntimeError(
            f"Atlas page {page_name} Brotli output is larger than its payload: "
            f"{len(runtime_bytes)} > {len(runtime_payload)} bytes"
        )
    write_bytes_atomically(runtime_path, runtime_bytes)
    source_fingerprint = source_set_fingerprint(root, paths)
    content_sha256 = hashlib.sha256(runtime_bytes).hexdigest()
    decoded_sha256 = hashlib.sha256(decoded_atlas).hexdigest()
    preview_sha256 = file_sha256(atlas_path)
    return BuiltPage(
        manifest={
            "resource": runtime_path.relative_to(root).as_posix(),
            "previewResource": atlas_path.relative_to(root).as_posix(),
            "width": atlas_width,
            "height": atlas_height,
            "encoding": encoding,
            "uncompressedByteCount": uncompressed_byte_count,
            "payloadByteCount": len(runtime_payload),
            "compressedByteCount": len(runtime_bytes),
            "sourceFingerprint": source_fingerprint,
            "contentSha256": content_sha256,
            "decodedSha256": decoded_sha256,
            "previewSha256": preview_sha256,
            "logicalFrameCount": len(ordered_frames),
            "uniqueSpriteCount": len(sprites),
            "frames": ordered_frames,
        },
        direct_compressed_byte_count=len(direct_runtime_bytes),
        delta_compressed_byte_count=len(delta_runtime_bytes),
        selected_saving_byte_count=delta_saving if use_delta else 0,
    )


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
            f"luban-{page_name}.pbgra.br",
        )
    }
    for pattern in (
        "luban-*.png",
        "luban-*.pbgra.br",
        # Remove stale legacy encodings so the project wildcard cannot
        # accidentally carry two compression formats into single-file publish.
        "luban-*.pbgra.*",
    ):
        for stale_page in output_directory.glob(pattern):
            if stale_page.name not in expected_page_files:
                stale_page.unlink()
    requested_workers = int(os.environ.get("XLB_ATLAS_WORKERS", "3"))
    worker_count = max(1, min(requested_workers, 4, len(pages)))
    print(f"Building {len(pages)} sprite pages with {worker_count} workers:")
    # Each job owns distinct output paths and writes both files atomically.
    # Brotli's native encoder releases the GIL, so bounded page parallelism
    # shortens the offline quality-11 build without changing any page bytes.
    # Results are collected in manifest order to keep JSON deterministic.
    with ThreadPoolExecutor(
        max_workers=worker_count,
        thread_name_prefix="xlb-atlas",
    ) as executor:
        page_futures = {
            page_name: executor.submit(
                build_page,
                root,
                page_name,
                paths,
                output_directory / f"luban-{page_name}.png",
            )
            for page_name, paths in pages.items()
        }
        built_pages = {
            page_name: page_futures[page_name].result()
            for page_name in pages
        }

    manifest_pages = {
        page_name: built_pages[page_name].manifest
        for page_name in pages
    }
    for page_name in pages:
        result = built_pages[page_name]
        page = result.manifest
        direct_bytes = result.direct_compressed_byte_count
        selected_bytes = int(page["compressedByteCount"])
        print(
            f"  {page_name}: {page['width']}x{page['height']}, "
            f"{page['logicalFrameCount']} frames, "
            f"{page['uniqueSpriteCount']} unique, {page['encoding']}, "
            f"{selected_bytes / 1024 / 1024:.2f} MiB Brotli "
            f"(direct {direct_bytes / 1024 / 1024:.2f} MiB)"
        )

    selected_delta_pages = [
        page_name
        for page_name in pages
        if built_pages[page_name].manifest["encoding"] == DELTA_SUB_ENCODING
    ]
    total_direct_bytes = sum(
        result.direct_compressed_byte_count
        for result in built_pages.values()
    )
    total_selected_bytes = sum(
        int(result.manifest["compressedByteCount"])
        for result in built_pages.values()
    )
    total_saving_bytes = total_direct_bytes - total_selected_bytes
    saving_percent = (
        total_saving_bytes * 100 / total_direct_bytes
        if total_direct_bytes
        else 0.0
    )
    print(
        f"Selective delta-sub: {len(selected_delta_pages)}/{len(pages)} pages; "
        f"saved {total_saving_bytes / 1024 / 1024:.2f} MiB "
        f"({saving_percent:.2f}%) vs all-direct Brotli."
    )
    if selected_delta_pages:
        print("  selected: " + ", ".join(selected_delta_pages))

    source_paths = resource_paths(root)
    source_frame_count = len(source_paths)
    page_frame_count = sum(len(paths) for paths in pages.values())
    if source_frame_count != len({path for paths in pages.values() for path in paths}):
        raise RuntimeError("Manifest source frame count does not match page union")
    if page_frame_count != source_frame_count:
        raise RuntimeError(
            "Manifest page frame count must equal source frame count; "
            "cross-page resource duplication is not allowed"
        )
    if page_frame_count != sum(
        int(page["logicalFrameCount"])
        for page in manifest_pages.values()
    ):
        raise RuntimeError("Manifest page frame count does not match built pages")

    manifest = {
        "version": 4,
        "compression": "brotli",
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
