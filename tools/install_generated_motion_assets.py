from __future__ import annotations

import argparse
from collections import deque
import os
from pathlib import Path
from statistics import median
import time

from PIL import Image

from normalize_sprite import normalize
from split_sprite_sheet import (
    load_cells,
    remove_border_fragments,
    resize_cells_to_width,
    resize_rgba_premultiplied,
    save_registered_groups,
)


ACTIONS = ("yawn", "cry", "cute", "like", "eat", "wave", "think")
RUNTIME_CANVAS_SIZE = (450, 550)
V3_DOWN_SPEED_LINE_BOXES = {
    4: (
        (241, 7, 251, 52),
        (261, 7, 270, 44),
        (301, 10, 309, 43),
        (334, 20, 341, 56),
        (369, 68, 374, 100),
        (293, 240, 302, 282),
    ),
    5: (
        (122, 244, 139, 263),
        (110, 271, 130, 285),
        (122, 297, 138, 313),
    ),
}
V6_WAKE_SOURCE_NAME = "wake-v5-12-sheet-alpha.png"
V6_WAKE_MIDDLE_SOURCE_NAME = "wake-v5-middle-4-sheet-alpha.png"
V6_WAKE_CRAWL_SOURCE_NAME = "wake-v5-crawl-to-kneel-4-sheet-alpha.png"
V6_WAKE_GAP_SOURCE_NAME = "wake-v6-gap-2-sheet-alpha.png"
V6_EDGE_SOURCE_NAME = "edge-v2-12-sheet-alpha.png"
V8_WRIGGLE_HORIZONTAL_SOURCE_NAME = "wriggle-horizontal-v8-24-sheet-alpha.png"
V11_WRIGGLE_HORIZONTAL_SOURCE_NAME = (
    "wriggle-horizontal-v11-48-flow-sheet-alpha.png"
)
V15_WRIGGLE_VERTICAL_SOURCE_NAME = (
    "wriggle-vertical-v15-48-flow-sheet-alpha.png"
)
V13_WRIGGLE_CORNER_SOURCE_NAMES = (
    "wriggle-corner-v13-a-16-sheet-alpha.png",
    "wriggle-corner-v13-b-16-sheet-alpha.png",
    "wriggle-corner-v13-c-16-sheet-alpha.png",
)
V16_WRIGGLE_CORNER_SOURCE_NAME = "wriggle-corner-v16-48-flow-sheet-alpha.png"
V6_SCALE_REGISTERED_ACTIONS = ACTIONS
V6_ACTION_SOURCE_NAMES = {
    "yawn": "yawn-v7-24-sheet-alpha.png",
    "cry": "cry-v7-24-sheet-alpha.png",
    "cute": "cute-v7-24-sheet-alpha.png",
    "wave": "wave-v7-24-sheet-alpha.png",
}
V6_RUNTIME_INSET = 5
V6_RUNTIME_BOTTOM = RUNTIME_CANVAS_SIZE[1] - 10
V6_WAKE_TARGET_BRIM_WIDTH = 172
V7_EDGE_TARGET_BRIM_WIDTH = 180
# The older action sheets use several different head/body proportions.  A
# single brim target either enlarges the head or makes the standing body pop
# shorter.  These per-group targets keep the first action pose within about
# 11% of wake-14's full height while remaining within 10 raw pixels of idle's
# perceived cap scale.
V6_ACTION_TARGET_BRIM_WIDTHS = {
    "yawn": 180,
    "cry": 180,
    "cute": 180,
    "like": 181,
    "eat": 180,
    "wave": 180,
    "think": 180,
}
V6_ACTION_HEAD_CENTER_X = RUNTIME_CANVAS_SIZE[0] // 2
V6_WAKE_SEQUENCE = (
    ("base", 0),
    ("base", 1),
    ("base", 2),
    ("gap", 0),
    ("base", 3),
    ("gap", 1),
    ("crawl", 1),
    ("crawl", 2),
    ("crawl", 3),
    ("middle", 2),
    ("middle", 3),
    ("base", 7),
    ("base", 9),
    ("base", 11),
)
V6_WAKE_TARGET_HEAD_CENTERS = (
    206, 206, 207, 204, 202, 198, 200, 200, 202, 205, 210, 215, 221, 225,
)
WRIGGLE_FRAME_COUNT = 48
WRIGGLE_CORNER_FRAME_COUNT = 48
WRIGGLE_TARGET_BRIM_WIDTH = 146
WRIGGLE_HORIZONTAL_HEAD_CENTER_X = 344
WRIGGLE_VERTICAL_HEAD_CENTER_X = RUNTIME_CANVAS_SIZE[0] // 2


def resolve_generated_source(source_directory: Path, name: str) -> Path:
    source = source_directory / name
    if source.is_file():
        return source

    tracked_source = Path(__file__).resolve().parent / "generated_sources" / name
    if tracked_source.is_file():
        return tracked_source
    raise FileNotFoundError(
        f"Generated source was not found in {source_directory} or "
        f"{tracked_source.parent}: {name}"
    )


def try_resolve_generated_source(source_directory: Path, name: str) -> Path | None:
    source = source_directory / name
    if source.is_file():
        return source

    tracked_source = Path(__file__).resolve().parent / "generated_sources" / name
    return tracked_source if tracked_source.is_file() else None


def load_trimmed_grid_cells(
    source: Path,
    *,
    columns: int,
    rows: int,
) -> tuple[list[Image.Image], float]:
    """Split a generated grid after removing its uneven outer whitespace.

    Image generation keeps the requested rows and columns but may compress the
    artwork into only part of the canvas.  Splitting against the full canvas
    can therefore cut a sprite through the middle.  The opaque artwork bounds
    are stable, so trim those bounds, add a small transparent safety gutter,
    and only then divide the strict grid.
    """

    if columns <= 0 or rows <= 0:
        raise ValueError("Sprite-sheet columns and rows must be positive")
    with Image.open(source) as opened:
        sheet = opened.convert("RGBA")

    visible_mask = sheet.getchannel("A").point(
        lambda alpha: 255 if alpha > 64 else 0
    )
    visible_box = visible_mask.getbbox()
    if visible_box is None:
        raise ValueError(f"Generated sprite sheet is empty: {source}")
    trimmed = sheet.crop(visible_box)
    gutter = 4
    padded = Image.new(
        "RGBA",
        (trimmed.width + gutter * 2, trimmed.height + gutter * 2),
        (0, 0, 0, 0),
    )
    padded.paste(trimmed, (gutter, gutter))

    x_boundaries = [
        round(index * padded.width / columns)
        for index in range(columns + 1)
    ]
    y_boundaries = [
        round(index * padded.height / rows)
        for index in range(rows + 1)
    ]
    cells: list[Image.Image] = []
    for row in range(rows):
        for column in range(columns):
            cell = padded.crop((
                x_boundaries[column],
                y_boundaries[row],
                x_boundaries[column + 1],
                y_boundaries[row + 1],
            ))
            cells.append(remove_border_fragments(cell))
    return cells, padded.width / columns


def get_mask_components(
    image: Image.Image,
    predicate,
) -> list[tuple[int, tuple[int, int, int, int]]]:
    """Return 8-connected mask components as (pixel count, bounding box)."""

    frame = image.convert("RGBA")
    width, height = frame.size
    pixels = frame.load()
    mask = bytearray(width * height)
    for y in range(height):
        for x in range(width):
            red, green, blue, alpha = pixels[x, y]
            if alpha > 24 and predicate(red, green, blue):
                mask[y * width + x] = 1

    visited = bytearray(width * height)
    components: list[tuple[int, tuple[int, int, int, int]]] = []
    for start_y in range(height):
        for start_x in range(width):
            start_index = start_y * width + start_x
            if not mask[start_index] or visited[start_index]:
                continue

            queue: deque[tuple[int, int]] = deque([(start_x, start_y)])
            visited[start_index] = 1
            count = 0
            left = right = start_x
            top = bottom = start_y
            while queue:
                x, y = queue.popleft()
                count += 1
                left = min(left, x)
                right = max(right, x)
                top = min(top, y)
                bottom = max(bottom, y)
                for next_y in range(max(0, y - 1), min(height, y + 2)):
                    for next_x in range(max(0, x - 1), min(width, x + 2)):
                        next_index = next_y * width + next_x
                        if not mask[next_index] or visited[next_index]:
                            continue
                        visited[next_index] = 1
                        queue.append((next_x, next_y))
            components.append((count, (left, top, right + 1, bottom + 1)))
    return components


def get_blue_brim_box(image: Image.Image) -> tuple[int, int, int, int]:
    """Locate Luban's connected cyan-blue cap brim, excluding purple clothes."""

    components = get_mask_components(
        image,
        lambda red, green, blue: (
            blue >= 95 and green >= 55 and
            blue >= red * 1.22 and blue >= green * 1.08
        ),
    )
    useful = [
        (count, box)
        for count, box in components
        if (count >= 18 and box[2] - box[0] >= 12 and
            box[1] < image.height * 0.58)
    ]
    if not useful:
        raise ValueError("Generated frame does not contain a detectable blue cap brim")

    # The brim is both the widest and one of the largest cyan components.  The
    # width-weighted score stays stable even when black ink divides its fill.
    return max(
        useful,
        key=lambda item: item[0] * max(1, item[1][2] - item[1][0]),
    )[1]


def get_topmost_blue_brim_box(image: Image.Image) -> tuple[int, int, int, int]:
    """Locate the cap brim without confusing the prone sprite's blue clothes."""

    visible_box = image.convert("RGBA").getchannel("A").getbbox()
    if visible_box is None:
        raise ValueError("Wriggle frame is empty")
    visible_top = visible_box[1]
    visible_height = visible_box[3] - visible_box[1]
    components = get_mask_components(
        image,
        lambda red, green, blue: (
            blue >= 95 and green >= 55 and
            blue >= red * 1.22 and blue >= green * 1.08
        ),
    )
    useful = [
        (count, box)
        for count, box in components
        if (count >= 18 and box[2] - box[0] >= 12 and
            box[1] < visible_top + visible_height * 0.45)
    ]
    if not useful:
        raise ValueError("Wriggle frame does not contain a detectable cap brim")

    top = min(box[1] for _, box in useful)
    top_band = [item for item in useful if item[1][1] <= top + 4]
    return max(
        top_band,
        key=lambda item: item[0] * max(1, item[1][2] - item[1][0]),
    )[1]


def get_red_cap_box(image: Image.Image) -> tuple[int, int, int, int]:
    """Locate the red cap crown above the already-registered blue brim."""

    brim = get_topmost_blue_brim_box(image)
    components = get_mask_components(
        image,
        lambda red, green, blue: (
            red >= 100 and red >= blue * 1.25 and red >= green * 1.25
        ),
    )
    useful = [
        (count, box)
        for count, box in components
        if count >= 24 and box[2] - box[0] >= 12 and
        box[1] < brim[1] + 4
    ]
    if not useful:
        raise ValueError("Wriggle frame does not contain a detectable red cap")
    return max(
        useful,
        key=lambda item: item[0] * max(1, item[1][2] - item[1][0]),
    )[1]


def get_red_cap_center(image: Image.Image) -> tuple[float, float]:
    box = get_red_cap_box(image)
    pixels = image.convert("RGBA").load()
    count = 0
    sum_x = 0
    sum_y = 0
    for y in range(box[1], box[3]):
        for x in range(box[0], box[2]):
            red, green, blue, alpha = pixels[x, y]
            if (alpha <= 24 or red < 100 or
                    red < blue * 1.25 or red < green * 1.25):
                continue
            count += 1
            sum_x += x
            sum_y += y
    if count == 0:
        raise ValueError("Wriggle red cap component is empty")
    return sum_x / count, sum_y / count


def crop_visible(image: Image.Image) -> Image.Image:
    frame = image.convert("RGBA")
    box = frame.getchannel("A").getbbox()
    if box is None:
        raise ValueError("Generated frame must contain visible pixels")
    return frame.crop(box)


def remove_white_grid_gutter(image: Image.Image) -> Image.Image:
    """Remove image-generator grid lines without erasing white costume details."""

    frame = image.convert("RGBA")
    pixels = frame.load()
    width, height = frame.size
    visited = bytearray(width * height)
    queue: deque[tuple[int, int]] = deque()

    def is_grid_pixel(x: int, y: int) -> bool:
        red, green, blue, alpha = pixels[x, y]
        return alpha > 16 and red >= 235 and green >= 235 and blue >= 235

    for x in range(width):
        for y in (0, height - 1):
            if is_grid_pixel(x, y):
                queue.append((x, y))
    for y in range(height):
        for x in (0, width - 1):
            if is_grid_pixel(x, y):
                queue.append((x, y))

    while queue:
        x, y = queue.popleft()
        index = y * width + x
        if visited[index] or not is_grid_pixel(x, y):
            continue
        visited[index] = 1
        pixels[x, y] = (0, 0, 0, 0)
        for next_y in range(max(0, y - 1), min(height, y + 2)):
            for next_x in range(max(0, x - 1), min(width, x + 2)):
                if not visited[next_y * width + next_x]:
                    queue.append((next_x, next_y))
    # Image generation may leave a broken one-pixel grid remnant just inside
    # a rounded cell boundary.  The authored sheets reserve much more than
    # this safety gutter around every sprite, so clearing it cannot touch art.
    safety_gutter = min(12, width // 8, height // 8)
    for y in range(height):
        for x in range(width):
            if (x < safety_gutter or y < safety_gutter or
                    x >= width - safety_gutter or
                    y >= height - safety_gutter):
                pixels[x, y] = (0, 0, 0, 0)
    return remove_border_fragments(frame)


def save_png_atomically(image: Image.Image, destination: Path) -> None:
    """Replace a PNG without exposing half-written frames to scanners/builds."""

    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(
        f".{destination.stem}.{os.getpid()}.tmp.png"
    )
    for attempt in range(5):
        try:
            image.save(temporary, format="PNG", optimize=True)
            temporary.replace(destination)
            return
        except OSError:
            temporary.unlink(missing_ok=True)
            if attempt == 4:
                raise
            time.sleep(0.05 * (attempt + 1))


def fit_scale(
    sprite: Image.Image,
    desired_scale: float,
    maximum_size: tuple[int, int] = (
        RUNTIME_CANVAS_SIZE[0] - V6_RUNTIME_INSET * 2,
        RUNTIME_CANVAS_SIZE[1] - V6_RUNTIME_INSET * 2,
    ),
) -> float:
    return min(
        desired_scale,
        maximum_size[0] / sprite.width,
        maximum_size[1] / sprite.height,
    )


def place_runtime_sprite(
    sprite: Image.Image,
    *,
    anchor: str = "bottom-center",
    head_center_x: int | None = None,
    allow_horizontal_crop: bool = False,
) -> Image.Image:
    sprite = neutralize_green_fringe(crop_visible(sprite))
    canvas = Image.new("RGBA", RUNTIME_CANVAS_SIZE, (0, 0, 0, 0))
    if head_center_x is not None:
        brim_box = get_blue_brim_box(sprite)
        brim_center_x = (brim_box[0] + brim_box[2]) / 2
        x = round(head_center_x - brim_center_x)
        if allow_horizontal_crop:
            x = max(0, x)
        else:
            x = min(
                RUNTIME_CANVAS_SIZE[0] - V6_RUNTIME_INSET - sprite.width,
                max(V6_RUNTIME_INSET, x),
            )
        y = V6_RUNTIME_BOTTOM - sprite.height
    elif anchor == "left":
        x = 0
        y = (RUNTIME_CANVAS_SIZE[1] - sprite.height) // 2
    elif anchor == "top":
        x = (RUNTIME_CANVAS_SIZE[0] - sprite.width) // 2
        y = 0
    elif anchor == "bottom":
        x = (RUNTIME_CANVAS_SIZE[0] - sprite.width) // 2
        y = RUNTIME_CANVAS_SIZE[1] - sprite.height
    elif anchor == "bottom-center":
        x = (RUNTIME_CANVAS_SIZE[0] - sprite.width) // 2
        y = V6_RUNTIME_BOTTOM - sprite.height
    else:
        raise ValueError(f"Unknown runtime anchor: {anchor}")

    if not allow_horizontal_crop:
        x = min(RUNTIME_CANVAS_SIZE[0] - sprite.width, max(0, x))
    y = min(RUNTIME_CANVAS_SIZE[1] - sprite.height, max(0, y))
    canvas.alpha_composite(sprite, (x, y))
    return canvas


def place_wriggle_sprite(
    sprite: Image.Image,
    *,
    head_center_x: int,
) -> Image.Image:
    """Bottom-register a wriggle pose using the real, topmost cap brim."""

    sprite = neutralize_green_fringe(crop_visible(sprite))
    brim_box = get_topmost_blue_brim_box(sprite)
    brim_center_x = (brim_box[0] + brim_box[2]) / 2
    x = round(head_center_x - brim_center_x)
    x = min(
        RUNTIME_CANVAS_SIZE[0] - V6_RUNTIME_INSET - sprite.width,
        max(V6_RUNTIME_INSET, x),
    )
    x = min(RUNTIME_CANVAS_SIZE[0] - sprite.width, max(0, x))
    y = min(
        RUNTIME_CANVAS_SIZE[1] - sprite.height,
        max(0, V6_RUNTIME_BOTTOM - sprite.height),
    )
    canvas = Image.new("RGBA", RUNTIME_CANVAS_SIZE, (0, 0, 0, 0))
    canvas.alpha_composite(sprite, (x, y))
    return canvas


def place_corner_sprite(
    sprite: Image.Image,
    *,
    target_cap_center_x: float,
) -> Image.Image:
    """Bottom-register a turn pose along the endpoints' red-cap trajectory."""

    sprite = neutralize_green_fringe(crop_visible(sprite))
    cap_center_x, _ = get_red_cap_center(sprite)
    x = round(target_cap_center_x - cap_center_x)
    x = min(
        RUNTIME_CANVAS_SIZE[0] - V6_RUNTIME_INSET - sprite.width,
        max(V6_RUNTIME_INSET, x),
    )
    x = min(RUNTIME_CANVAS_SIZE[0] - sprite.width, max(0, x))
    y = min(
        RUNTIME_CANVAS_SIZE[1] - sprite.height,
        max(0, V6_RUNTIME_BOTTOM - sprite.height),
    )
    canvas = Image.new("RGBA", RUNTIME_CANVAS_SIZE, (0, 0, 0, 0))
    canvas.alpha_composite(sprite, (x, y))
    return canvas


def resize_sprite(image: Image.Image, scale: float) -> Image.Image:
    sprite = crop_visible(image)
    scale = fit_scale(sprite, scale)
    return resize_rgba_premultiplied(
        sprite,
        (
            max(1, round(sprite.width * scale)),
            max(1, round(sprite.height * scale)),
        ),
    )


def register_by_brim(
    image: Image.Image,
    target_brim_width: int,
    *,
    head_center_x: int,
    allow_horizontal_crop: bool = False,
) -> Image.Image:
    sprite = crop_visible(image)
    brim_box = get_blue_brim_box(sprite)
    brim_width = brim_box[2] - brim_box[0]
    sprite = resize_sprite(sprite, target_brim_width / brim_width)
    return place_runtime_sprite(
        sprite,
        head_center_x=head_center_x,
        allow_horizontal_crop=allow_horizontal_crop,
    )


def resize_runtime_paths(paths: list[Path]) -> None:
    for path in paths:
        with Image.open(path) as opened:
            frame = opened.convert("RGBA")
        if frame.size != RUNTIME_CANVAS_SIZE:
            frame = resize_rgba_premultiplied(frame, RUNTIME_CANVAS_SIZE)
        frame.save(path, optimize=True)


def neutralize_green_fringe(image: Image.Image) -> Image.Image:
    """Despill residual green-dominant edge RGB without changing alpha."""

    frame = image.convert("RGBA")
    pixels = bytearray(frame.tobytes())
    for offset in range(0, len(pixels), 4):
        red = pixels[offset]
        green = pixels[offset + 1]
        blue = pixels[offset + 2]
        alpha = pixels[offset + 3]
        if alpha > 0 and green > red and green > blue:
            pixels[offset + 1] = max(red, blue)
    return Image.frombytes("RGBA", frame.size, bytes(pixels))


def remove_green_screen(image: Image.Image) -> Image.Image:
    """Convert the generated bright-green backdrop to smooth alpha."""

    frame = image.convert("RGBA")
    pixels = bytearray(frame.tobytes())
    for offset in range(0, len(pixels), 4):
        red = pixels[offset]
        green = pixels[offset + 1]
        blue = pixels[offset + 2]
        original_alpha = pixels[offset + 3]
        green_excess = green - max(red, blue)
        if green >= 70 and green_excess > 16:
            # A black antialiased contour over the key color moves linearly
            # from about 200 green-excess (background) toward zero (ink).
            key_alpha = round(
                255 * max(0, min(1, (188 - green_excess) / (188 - 16)))
            )
            alpha = original_alpha * key_alpha // 255
            # Generated backgrounds contain a slight green luminance gradient;
            # its residual key alpha clusters below 48.  Remove that cluster,
            # then expand the real antialiased contour back to the full range.
            if alpha <= 48:
                pixels[offset:offset + 4] = b"\x00\x00\x00\x00"
                continue
            alpha = round((alpha - 48) * 255 / (255 - 48))
            if alpha >= 252:
                alpha = 255
            pixels[offset + 1] = min(green, max(red, blue) + 4)
            pixels[offset + 3] = alpha
    return Image.frombytes("RGBA", frame.size, bytes(pixels))


def resize_runtime_frames(assets_directory: Path) -> None:
    prefixes = ["luban-wake"]
    prefixes.extend(f"luban-{action}-frame" for action in ACTIONS)
    prefixes.extend((
        "luban-edge-left",
        "luban-edge-top",
        "luban-edge-bottom",
    ))
    prefixes.extend(
        f"luban-roam-wriggle-{direction}"
        for direction in (
            "horizontal",
            "vertical",
            "vertical-up",
            "vertical-down",
            "corner",
        )
    )

    paths = [
        path
        for prefix in prefixes
        for path in sorted(assets_directory.glob(f"{prefix}-*.png"))
    ]
    resize_runtime_paths(paths)


def reanchor_edge_frames(assets_directory: Path, prefix: str, anchor: str) -> None:
    for frame_number in range(1, 5):
        path = assets_directory / f"{prefix}-{frame_number:02d}.png"
        with Image.open(path) as opened:
            frame = opened.convert("RGBA")
        box = frame.getchannel("A").getbbox()
        if box is None:
            raise ValueError(f"Edge frame is empty: {path}")

        sprite = frame.crop(box)
        canvas = Image.new("RGBA", frame.size, (0, 0, 0, 0))
        if anchor == "left":
            position = (0, (frame.height - sprite.height) // 2)
        elif anchor == "top":
            position = ((frame.width - sprite.width) // 2, 0)
        elif anchor == "bottom":
            position = ((frame.width - sprite.width) // 2, frame.height - sprite.height)
        else:
            raise ValueError(f"Unknown edge anchor: {anchor}")
        canvas.alpha_composite(sprite, position)
        canvas.save(path, optimize=True)


def remove_v3_down_speed_lines(cells: list[Image.Image]) -> list[Image.Image]:
    """Remove only the detached white speed marks in down frames 5 and 6."""

    cleaned_cells = [cell.copy() for cell in cells]
    for frame_index, boxes in V3_DOWN_SPEED_LINE_BOXES.items():
        if frame_index >= len(cleaned_cells):
            raise ValueError("V3 vertical-down sheet must contain eight cells")
        cell = cleaned_cells[frame_index]
        found_visible_mark = False
        for box in boxes:
            left, top, right, bottom = box
            if not (0 <= left < right <= cell.width and
                    0 <= top < bottom <= cell.height):
                raise ValueError(
                    f"V3 speed-line cleanup box {box} is outside frame "
                    f"{frame_index + 1} size {cell.size}"
                )
            found_visible_mark |= (
                cell.getchannel("A").crop(box).getbbox() is not None
            )
            cell.paste((0, 0, 0, 0), box)
        # Edge contraction can already erase every thin mark in frame 5.  The
        # three slightly wider frame-6 dashes must still be found and removed.
        if frame_index == 5 and not found_visible_mark:
            raise ValueError("Expected V3 speed lines are missing from frame 6")
    return cleaned_cells


def install(source_directory: Path, assets_directory: Path) -> None:
    loaded: list[tuple[list, int, Path, str]] = []

    wake_source = source_directory / "wake-12-sheet-alpha.png"
    wake_cells, wake_cell_width = load_cells(wake_source, columns=6, rows=2)
    if len(wake_cells) != 12:
        raise ValueError("Wake sheet must contain exactly 12 cells")
    loaded.append((wake_cells, wake_cell_width, assets_directory, "luban-wake"))

    for action in ACTIONS:
        source = source_directory / f"{action}-24-sheet-alpha.png"
        cells, cell_width = load_cells(source, columns=6, rows=4)
        if len(cells) != 24:
            raise ValueError(f"{action} sheet must contain exactly 24 cells")
        loaded.append((
            cells,
            cell_width,
            assets_directory,
            f"luban-{action}-frame",
        ))

    reference_cell_width = max(group[1] for group in loaded)
    groups = [
        (
            resize_cells_to_width(cells, cell_width, reference_cell_width),
            destination,
            prefix,
        )
        for cells, cell_width, destination, prefix in loaded
    ]
    save_registered_groups(groups)

    edge_source = source_directory / "edge-peek-12-sheet-alpha.png"
    edge_cells, _ = load_cells(edge_source, columns=4, rows=3)
    if len(edge_cells) != 12:
        raise ValueError("Edge peek sheet must contain exactly 12 cells")
    save_registered_groups([
        (edge_cells[0:4], assets_directory, "luban-edge-left"),
        (edge_cells[4:8], assets_directory, "luban-edge-top"),
        (edge_cells[8:12], assets_directory, "luban-edge-bottom"),
    ])
    reanchor_edge_frames(assets_directory, "luban-edge-left", "left")
    reanchor_edge_frames(assets_directory, "luban-edge-top", "top")
    reanchor_edge_frames(assets_directory, "luban-edge-bottom", "bottom")

    roam_source = source_directory / "roam-moves-24-sheet-alpha.png"
    roam_cells, _ = load_cells(roam_source, columns=4, rows=6)
    if len(roam_cells) != 24:
        raise ValueError("Roam movement sheet must contain exactly 24 cells")
    # The historical sheet stores three horizontal groups followed by three
    # vertical groups.  Only its first movement is still supported.
    roam_groups = [
        (
            roam_cells[0:4],
            assets_directory,
            "luban-roam-wriggle-horizontal",
        ),
        (
            roam_cells[12:16],
            assets_directory,
            "luban-roam-wriggle-vertical",
        ),
    ]
    save_registered_groups(roam_groups)
    resize_runtime_frames(assets_directory)


def install_v2_subset(source_directory: Path, assets_directory: Path) -> None:
    """Install the approved v2 idle, wake, and edge assets only.

    The v2 roam sheet still contains only the legacy 24-cell layout, so this
    path deliberately leaves every existing roam asset untouched.
    """

    idle_source = source_directory / "idle-v2-alpha.png"
    idle_destination = assets_directory / "luban-idle.png"
    normalize(idle_source, idle_destination)

    wake_cells, wake_cell_width = load_cells(
        source_directory / "wake-v2-12-sheet-alpha.png",
        columns=6,
        rows=2,
        snap_to_transparent_gaps=True,
    )
    if len(wake_cells) != 12:
        raise ValueError("V2 wake sheet must contain exactly 12 cells")
    registered_wake = resize_cells_to_width(
        wake_cells,
        wake_cell_width,
        wake_cell_width,
    )
    save_registered_groups([
        (registered_wake, assets_directory, "luban-wake"),
    ])

    # The two hands in edge-v2 intentionally touch the cell boundary and can
    # be independent from the head.  Preserve them instead of applying the
    # generic generated-border fragment cleanup.
    edge_cells, _ = load_cells(
        source_directory / "edge-v2-12-sheet-alpha.png",
        columns=4,
        rows=3,
        preserve_border_components=True,
        snap_to_transparent_gaps=True,
    )
    if len(edge_cells) != 12:
        raise ValueError("V2 edge sheet must contain exactly 12 cells")
    save_registered_groups([
        (edge_cells[0:4], assets_directory, "luban-edge-left"),
        (edge_cells[4:8], assets_directory, "luban-edge-top"),
        (edge_cells[8:12], assets_directory, "luban-edge-bottom"),
    ])
    reanchor_edge_frames(assets_directory, "luban-edge-left", "left")
    reanchor_edge_frames(assets_directory, "luban-edge-top", "top")
    reanchor_edge_frames(assets_directory, "luban-edge-bottom", "bottom")

    updated_paths = [idle_destination]
    updated_paths.extend(sorted(assets_directory.glob("luban-wake-*.png")))
    updated_paths.extend(sorted(assets_directory.glob("luban-edge-left-*.png")))
    updated_paths.extend(sorted(assets_directory.glob("luban-edge-top-*.png")))
    updated_paths.extend(sorted(assets_directory.glob("luban-edge-bottom-*.png")))
    resize_runtime_paths(updated_paths)


def install_v3_roam(source_directory: Path, assets_directory: Path) -> None:
    """Install 8-frame horizontal, vertical-up, and vertical-down roam sets."""

    horizontal_cells, horizontal_cell_width = load_cells(
        source_directory / "roam-horizontal-v3-24-sheet-alpha.png",
        columns=4,
        rows=6,
        snap_to_transparent_gaps=True,
    )
    vertical_up_cells, vertical_up_cell_width = load_cells(
        source_directory / "roam-vertical-up-v3-25-sheet-alpha.png",
        columns=5,
        rows=5,
        snap_to_transparent_gaps=True,
    )
    vertical_down_cells, vertical_down_cell_width = load_cells(
        source_directory / "roam-vertical-down-v3-8-sheet-alpha.png",
        columns=4,
        rows=2,
        preserve_border_components=True,
        snap_to_transparent_gaps=True,
    )
    if len(horizontal_cells) != 24:
        raise ValueError("V3 horizontal roam sheet must contain 24 cells")
    if len(vertical_up_cells) != 25:
        raise ValueError("V3 vertical-up roam sheet must contain 25 cells")
    if len(vertical_down_cells) != 8:
        raise ValueError("V3 vertical-down roam sheet must contain 8 cells")

    # The 5x5 up sheet contributes its first 24 cells in row-major order; the
    # final generated cell is intentionally unused.  Down frames are cleaned
    # after removing the two frames' white speed marks.
    vertical_up_cells = vertical_up_cells[:24]
    vertical_down_cells = remove_v3_down_speed_lines(vertical_down_cells)

    reference_cell_width = max(
        horizontal_cell_width,
        vertical_up_cell_width,
        vertical_down_cell_width,
    )
    registered_horizontal = resize_cells_to_width(
        horizontal_cells,
        horizontal_cell_width,
        reference_cell_width,
    )
    registered_vertical_up = resize_cells_to_width(
        vertical_up_cells,
        vertical_up_cell_width,
        reference_cell_width,
    )
    registered_vertical_down = resize_cells_to_width(
        vertical_down_cells,
        vertical_down_cell_width,
        reference_cell_width,
    )

    groups: list[tuple[list[Image.Image], Path, str]] = [
        (
            registered_horizontal[0:8],
            assets_directory,
            "luban-roam-wriggle-horizontal",
        ),
        (
            registered_vertical_up[0:8],
            assets_directory,
            "luban-roam-wriggle-vertical-up",
        ),
        (
            registered_vertical_down,
            assets_directory,
            "luban-roam-wriggle-vertical-down",
        ),
    ]
    save_registered_groups(groups)

    updated_paths = [
        assets_directory / f"{prefix}-{frame_number:02d}.png"
        for _, _, prefix in groups
        for frame_number in range(1, 9)
    ]
    resize_runtime_paths(updated_paths)

    # Very dark key-colored edge pixels can become visible only after the two
    # premultiplied-alpha resizes.  A final RGB-only despill keeps the alpha
    # silhouette intact while guaranteeing that no green fringe survives.
    for path in updated_paths:
        with Image.open(path) as opened:
            frame = neutralize_green_fringe(opened)
        frame.save(path, optimize=True)

    # MainWindow now consumes directional up/down assets.  Remove the obsolete
    # four-frame `vertical` names so only directional assets remain.
    for frame_number in range(1, 5):
        legacy_path = assets_directory / (
            f"luban-roam-wriggle-vertical-{frame_number:02d}.png"
        )
        legacy_path.unlink(missing_ok=True)


def get_direct_runtime_registration_scale(cells: list[Image.Image]) -> float:
    boxes = [cell.getchannel("A").getbbox() for cell in cells]
    if any(box is None for box in boxes):
        raise ValueError("Every generated cell must contain a visible sprite")
    visible_boxes = [box for box in boxes if box is not None]
    widest = max(box[2] - box[0] for box in visible_boxes)
    tallest = max(box[3] - box[1] for box in visible_boxes)
    # `save_registered_groups` fits to 860x1060 on a 900x1100 canvas and the
    # runtime pass halves that canvas to 450x550.  Compose both operations into
    # one premultiplied-alpha resize so the regenerated edge never shimmers.
    return min(860 / widest, 1060 / tallest) / 2


def install_v6_wake(source_directory: Path, assets_directory: Path) -> None:
    """Install fourteen gradual, brim-registered wake poses without scale pops."""

    base_cells, _ = load_cells(
        resolve_generated_source(source_directory, V6_WAKE_SOURCE_NAME),
        columns=4,
        rows=3,
        snap_to_transparent_gaps=True,
    )
    middle_cells, _ = load_cells(
        resolve_generated_source(source_directory, V6_WAKE_MIDDLE_SOURCE_NAME),
        columns=2,
        rows=2,
        snap_to_transparent_gaps=True,
    )
    crawl_cells, _ = load_cells(
        resolve_generated_source(source_directory, V6_WAKE_CRAWL_SOURCE_NAME),
        columns=2,
        rows=2,
        snap_to_transparent_gaps=True,
    )
    gap_cells, _ = load_cells(
        resolve_generated_source(source_directory, V6_WAKE_GAP_SOURCE_NAME),
        columns=2,
        rows=1,
        snap_to_transparent_gaps=True,
    )
    if (len(base_cells) != 12 or len(middle_cells) != 4 or
            len(crawl_cells) != 4 or len(gap_cells) != 2):
        raise ValueError("V6 wake sources must contain 12 + 4 + 4 + 2 cells")

    sources = {
        "base": base_cells,
        "middle": middle_cells,
        "crawl": crawl_cells,
        "gap": gap_cells,
    }
    for frame_number, ((source_name, source_index), target_head_center) in enumerate(
        zip(
            V6_WAKE_SEQUENCE,
            V6_WAKE_TARGET_HEAD_CENTERS,
            strict=True,
        ),
        start=1,
    ):
        frame = register_by_brim(
            sources[source_name][source_index],
            V6_WAKE_TARGET_BRIM_WIDTH,
            head_center_x=target_head_center,
            allow_horizontal_crop=True,
        )
        save_png_atomically(
            frame,
            assets_directory / f"luban-wake-{frame_number:02d}.png",
        )


def install_v6_actions(
    source_directory: Path,
    assets_directory: Path,
    actions: tuple[str, ...] = V6_SCALE_REGISTERED_ACTIONS,
) -> None:
    """Match every standing action to the wake character scale offline."""

    for action in actions:
        cells, _ = load_cells(
            resolve_generated_source(
                source_directory,
                V6_ACTION_SOURCE_NAMES.get(
                    action,
                    f"{action}-24-sheet-alpha.png",
                ),
            ),
            columns=6,
            rows=4,
        )
        if len(cells) != 24:
            raise ValueError(f"V6 {action} source must contain twenty-four cells")

        for frame_number, cell in enumerate(cells, start=1):
            frame = register_by_brim(
                cell,
                V6_ACTION_TARGET_BRIM_WIDTHS[action],
                head_center_x=V6_ACTION_HEAD_CENTER_X,
            )
            save_png_atomically(
                frame,
                assets_directory / f"luban-{action}-frame-{frame_number:02d}.png",
            )


def install_v6_edge(source_directory: Path, assets_directory: Path) -> None:
    """Register every edge-peek frame to the same cap scale as idle/actions."""

    cells, _ = load_cells(
        resolve_generated_source(source_directory, V6_EDGE_SOURCE_NAME),
        columns=4,
        rows=3,
        preserve_border_components=True,
        snap_to_transparent_gaps=True,
    )
    if len(cells) != 12:
        raise ValueError("V6 edge source must contain twelve cells")
    groups = (
        ("left", cells[0:4], "left"),
        ("top", cells[4:8], "top"),
        ("bottom", cells[8:12], "bottom"),
    )
    for edge_name, edge_cells, anchor in groups:
        for frame_number, cell in enumerate(edge_cells, start=1):
            sprite = crop_visible(cell)
            brim_box = get_blue_brim_box(sprite)
            brim_width = brim_box[2] - brim_box[0]
            sprite = resize_sprite(
                sprite,
                V7_EDGE_TARGET_BRIM_WIDTH / brim_width,
            )
            frame = place_runtime_sprite(sprite, anchor=anchor)
            save_png_atomically(
                frame,
                assets_directory / f"luban-edge-{edge_name}-{frame_number:02d}.png",
            )


def normalize_wriggle_brim(
    sprites: list[Image.Image],
    target_brim_width: float,
) -> list[Image.Image]:
    normalized = []
    for sprite in sprites:
        brim = get_topmost_blue_brim_box(sprite)
        brim_width = brim[2] - brim[0]
        normalized.append(resize_sprite(sprite, target_brim_width / brim_width))
    return normalized


def render_wriggle_quality_mask(frame: Image.Image) -> tuple[bytes, int]:
    width = 190
    height = 242
    target_height = round(frame.height * width / frame.width)
    resized = resize_rgba_premultiplied(frame, (width, target_height))
    visual = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    visual.alpha_composite(resized, (0, height - target_height))
    mask = bytes(
        1 if alpha > 16 else 0
        for alpha in visual.getchannel("A").get_flattened_data()
    )
    return mask, sum(mask)


def alpha_iou(first: bytes, second: bytes) -> float:
    intersection = sum(
        1 for left, right in zip(first, second, strict=True)
        if left and right
    )
    union = sum(
        1 for left, right in zip(first, second, strict=True)
        if left or right
    )
    return 1 if union == 0 else intersection / union


def validate_wriggle_loop(frames: list[Image.Image], name: str) -> None:
    if len(frames) != WRIGGLE_FRAME_COUNT:
        raise ValueError(
            f"{name} wriggle loop must contain exactly "
            f"{WRIGGLE_FRAME_COUNT} frames"
        )
    if len({frame.tobytes() for frame in frames}) != len(frames):
        raise ValueError(
            f"{name} wriggle loop must contain "
            f"{WRIGGLE_FRAME_COUNT} unique frames"
        )

    rendered = [render_wriggle_quality_mask(frame) for frame in frames]
    pairs = [
        (rendered[index], rendered[(index + 1) % len(rendered)])
        for index in range(len(rendered))
    ]
    ious = [alpha_iou(first[0], second[0]) for first, second in pairs]
    scale_steps = [
        abs(first[1] ** 0.5 - second[1] ** 0.5) /
        max(1, (first[1] ** 0.5 + second[1] ** 0.5) / 2)
        for first, second in pairs
    ]
    if min(ious) < 0.92 or sum(ious) / len(ious) < 0.95:
        raise ValueError(
            f"{name} wriggle continuity failed: "
            f"min IoU={min(ious):.3f}, mean IoU={sum(ious) / len(ious):.3f}"
        )
    if max(scale_steps) > 0.025:
        raise ValueError(
            f"{name} wriggle scale step exceeds 2.5%: "
            f"{max(scale_steps):.2%}"
        )

    brims = [get_topmost_blue_brim_box(frame) for frame in frames]
    brim_widths = [box[2] - box[0] for box in brims]
    brim_spread = (max(brim_widths) - min(brim_widths)) / (
        sum(brim_widths) / len(brim_widths)
    )
    cap_centers = [get_red_cap_center(frame) for frame in frames]
    maximum_cap_shift = max(
        (
            (cap_centers[(index + 1) % len(cap_centers)][0] - center[0]) ** 2 +
            (cap_centers[(index + 1) % len(cap_centers)][1] - center[1]) ** 2
        ) ** 0.5 * 190 / RUNTIME_CANVAS_SIZE[0]
        for index, center in enumerate(cap_centers)
    )
    if brim_spread > 0.03:
        raise ValueError(f"{name} wriggle cap scale spread exceeds 3%")
    if maximum_cap_shift > 2:
        raise ValueError(
            f"{name} wriggle adjacent cap movement exceeds 2px: "
            f"{maximum_cap_shift:.2f}px"
        )


def save_wriggle_runtime_loop(
    assets_directory: Path,
    horizontal_frames: list[Image.Image],
    vertical_up_frames: list[Image.Image],
) -> None:
    validate_wriggle_loop(horizontal_frames, "horizontal")
    validate_wriggle_loop(vertical_up_frames, "vertical")
    vertical_down_frames = list(reversed(vertical_up_frames))
    for direction, frames in (
        ("horizontal", horizontal_frames),
        ("vertical-up", vertical_up_frames),
        ("vertical-down", vertical_down_frames),
    ):
        for frame_number, frame in enumerate(frames, start=1):
            save_png_atomically(
                frame,
                assets_directory /
                f"luban-roam-wriggle-{direction}-{frame_number:02d}.png",
            )


def install_v6_roam(source_directory: Path, assets_directory: Path) -> None:
    """Install the 48-frame wriggle and corner sequences."""

    horizontal_wriggle, _ = load_cells(
        resolve_generated_source(
            source_directory,
            V11_WRIGGLE_HORIZONTAL_SOURCE_NAME,
        ),
        columns=8,
        rows=6,
        preserve_border_components=True,
    )
    vertical_wriggle, _ = load_cells(
        resolve_generated_source(
            source_directory,
            V15_WRIGGLE_VERTICAL_SOURCE_NAME,
        ),
        columns=8,
        rows=6,
        preserve_border_components=True,
    )
    if (len(horizontal_wriggle) != WRIGGLE_FRAME_COUNT or
            len(vertical_wriggle) != WRIGGLE_FRAME_COUNT):
        raise ValueError("V11 wriggle sources must each contain 48 cells")
    validate_wriggle_loop(horizontal_wriggle, "horizontal")
    validate_wriggle_loop(vertical_wriggle, "vertical")
    corner_wriggle, _ = load_cells(
        resolve_generated_source(
            source_directory,
            V16_WRIGGLE_CORNER_SOURCE_NAME,
        ),
        columns=8,
        rows=6,
        preserve_border_components=True,
    )
    if len(corner_wriggle) != WRIGGLE_CORNER_FRAME_COUNT:
        raise ValueError("V16 wriggle corner source must contain 48 cells")
    if corner_wriggle[0].tobytes() != horizontal_wriggle[0].tobytes():
        raise ValueError("V16 wriggle corner must start at horizontal frame 1")
    if corner_wriggle[-1].tobytes() != vertical_wriggle[0].tobytes():
        raise ValueError("V16 wriggle corner must end at vertical frame 1")
    save_wriggle_runtime_loop(
        assets_directory,
        horizontal_wriggle,
        vertical_wriggle,
    )
    for frame_number, frame in enumerate(corner_wriggle, start=1):
        save_png_atomically(
            frame,
            assets_directory /
            f"luban-roam-wriggle-corner-{frame_number:02d}.png",
        )

    for frame_number in range(1, 5):
        legacy_path = assets_directory / (
            f"luban-roam-wriggle-vertical-{frame_number:02d}.png"
        )
        legacy_path.unlink(missing_ok=True)


def install_v6_motion(source_directory: Path, assets_directory: Path) -> None:
    assets_directory.mkdir(parents=True, exist_ok=True)
    install_v6_wake(source_directory, assets_directory)
    install_v6_actions(source_directory, assets_directory)
    install_v6_edge(source_directory, assets_directory)
    install_v6_roam(source_directory, assets_directory)


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Install the generated wake and 24-frame action sheets plus the "
            "48-frame wriggle and corner sequences with shared registration, "
            "then resize the runtime canvases to 450x550."
        )
    )
    parser.add_argument(
        "--source-directory",
        type=Path,
        default=Path("tmp/imagegen"),
    )
    parser.add_argument(
        "--assets-directory",
        type=Path,
        default=Path("Assets"),
    )
    selection = parser.add_mutually_exclusive_group()
    selection.add_argument(
        "--v2-subset",
        action="store_true",
        help=(
            "Install idle-v2, wake-v2, and edge-v2 without replacing roam "
            "assets."
        ),
    )
    selection.add_argument(
        "--v3-roam",
        action="store_true",
        help=(
            "Install the legacy wriggle assets and remove obsolete "
            "four-frame vertical assets."
        ),
    )
    selection.add_argument(
        "--v6-motion",
        action="store_true",
        help=(
            "Install the tracked scale-registered wake, standing actions, "
            "edge-peek, and 48-frame wriggle/corner sets."
        ),
    )
    args = parser.parse_args()
    if args.v2_subset:
        install_v2_subset(args.source_directory, args.assets_directory)
    elif args.v3_roam:
        install_v3_roam(args.source_directory, args.assets_directory)
    elif args.v6_motion:
        install_v6_motion(args.source_directory, args.assets_directory)
    else:
        install(args.source_directory, args.assets_directory)


if __name__ == "__main__":
    main()
