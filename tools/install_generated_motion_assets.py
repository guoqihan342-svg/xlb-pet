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
    resize_cells_to_width,
    resize_rgba_premultiplied,
    save_registered_groups,
)


ACTIONS = ("yawn", "cry", "run", "cute", "like", "eat", "wave", "think")
ROAM_MODES = ("wriggle", "crawl", "hop")
RUNTIME_CANVAS_SIZE = (450, 550)
V2_RUN_SCALE = 1.42
V2_RUN_LOOP_SCALE = 1.12
V5_RUN_SOURCE_NAME = "run-loop-v11-16-sheet-alpha.png"
V5_RUN_TARGET_HEAD_WIDTH = 248
V5_RUN_HEAD_REGION_FRACTION = 0.5
V5_RUN_TARGET_HEAD_TOPS = (126,) * 16
V5_RUN_SEQUENCE_INDICES = (
    6, 1, 13, 14, 4, 2, 11, 10,
    7, 3, 5, 9, 0, 8, 12, 15,
)
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
V6_ROAM_HORIZONTAL_SOURCE_NAME = "roam-horizontal-v5-24-sheet-alpha.png"
V7_ROAM_VERTICAL_SOURCE_NAME = "roam-vertical-v7-24-sheet-alpha.png"
V6_SCALE_REGISTERED_ACTIONS = tuple(
    action for action in ACTIONS if action != "run"
)
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
V7_ROAM_SEQUENCE_INDICES = tuple(range(8))


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


def crop_visible(image: Image.Image) -> Image.Image:
    frame = image.convert("RGBA")
    box = frame.getchannel("A").getbbox()
    if box is None:
        raise ValueError("Generated frame must contain visible pixels")
    return frame.crop(box)


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
        f"luban-roam-{mode}-{direction}"
        for mode in ROAM_MODES
        for direction in (
            "horizontal",
            "vertical",
            "vertical-up",
            "vertical-down",
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
    roam_groups = []
    for mode_index, mode in enumerate(ROAM_MODES):
        horizontal_start = mode_index * 4
        vertical_start = (mode_index + len(ROAM_MODES)) * 4
        roam_groups.append((
            roam_cells[horizontal_start:horizontal_start + 4],
            assets_directory,
            f"luban-roam-{mode}-horizontal",
        ))
        roam_groups.append((
            roam_cells[vertical_start:vertical_start + 4],
            assets_directory,
            f"luban-roam-{mode}-vertical",
        ))
    save_registered_groups(roam_groups)
    resize_runtime_frames(assets_directory)


def install_v2_subset(source_directory: Path, assets_directory: Path) -> None:
    """Install the approved v2 idle, wake, run, and edge assets only.

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
    run_entry_cells, run_entry_cell_width = load_cells(
        source_directory / "run-entry-v2-16-sheet-alpha.png",
        columns=4,
        rows=4,
        snap_to_transparent_gaps=True,
    )
    run_loop_cells, run_loop_cell_width = load_cells(
        source_directory / "run-loop-v2-8-sheet-alpha.png",
        columns=4,
        rows=2,
        snap_to_transparent_gaps=True,
    )
    if len(wake_cells) != 12:
        raise ValueError("V2 wake sheet must contain exactly 12 cells")
    if len(run_entry_cells) != 16 or len(run_loop_cells) != 8:
        raise ValueError("V2 run sheets must contain 16 entry and 8 loop cells")

    reference_cell_width = max(
        wake_cell_width,
        run_entry_cell_width,
        run_loop_cell_width,
    )
    registered_wake = resize_cells_to_width(
        wake_cells,
        wake_cell_width,
        reference_cell_width,
    )
    registered_run_entry = resize_cells_to_width(
        run_entry_cells,
        run_entry_cell_width,
        reference_cell_width,
    )
    registered_run_loop = resize_cells_to_width(
        run_loop_cells,
        run_loop_cell_width,
        reference_cell_width,
    )
    # The loop sheet draws the same character about 11% smaller than the entry
    # sheet.  Correct that source-scale mismatch before shared registration so
    # frame 16 -> 17 does not visibly shrink when the loop begins.
    registered_run_loop = [
        resize_rgba_premultiplied(
            cell,
            (
                round(cell.width * V2_RUN_LOOP_SCALE),
                round(cell.height * V2_RUN_LOOP_SCALE),
            ),
        )
        for cell in registered_run_loop
    ]
    registered_run = registered_run_entry + registered_run_loop
    registered_run = [
        resize_rgba_premultiplied(
            cell,
            (
                round(cell.width * V2_RUN_SCALE),
                round(cell.height * V2_RUN_SCALE),
            ),
        )
        for cell in registered_run
    ]
    save_registered_groups([
        (registered_wake, assets_directory, "luban-wake"),
        (registered_run, assets_directory, "luban-run-frame"),
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
    updated_paths.extend(sorted(assets_directory.glob("luban-run-frame-*.png")))
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
    # final generated cell is intentionally unused.  Down frames are shared by
    # all three movement modes after removing the two frames' white speed marks.
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

    groups: list[tuple[list[Image.Image], Path, str]] = []
    for mode_index, mode in enumerate(ROAM_MODES):
        start = mode_index * 8
        groups.append((
            registered_horizontal[start:start + 8],
            assets_directory,
            f"luban-roam-{mode}-horizontal",
        ))
        groups.append((
            registered_vertical_up[start:start + 8],
            assets_directory,
            f"luban-roam-{mode}-vertical-up",
        ))
        groups.append((
            registered_vertical_down,
            assets_directory,
            f"luban-roam-{mode}-vertical-down",
        ))
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
    # four-frame `vertical` names so the packaged roam set contains exactly 72
    # current resources instead of retaining unused legacy copies.
    for mode in ROAM_MODES:
        for frame_number in range(1, 5):
            legacy_path = assets_directory / (
                f"luban-roam-{mode}-vertical-{frame_number:02d}.png"
            )
            legacy_path.unlink(missing_ok=True)


def get_visible_and_head_boxes(
    image: Image.Image,
) -> tuple[tuple[int, int, int, int], tuple[int, int, int, int]]:
    alpha = image.getchannel("A")
    visible_box = alpha.getbbox()
    if visible_box is None:
        raise ValueError("Run frame must contain a visible character")

    head_bottom = visible_box[1] + max(
        1,
        round(
            (visible_box[3] - visible_box[1])
            * V5_RUN_HEAD_REGION_FRACTION
        ),
    )
    local_head_box = alpha.crop((
        0,
        visible_box[1],
        image.width,
        head_bottom,
    )).getbbox()
    if local_head_box is None:
        raise ValueError("Run frame must contain a visible head region")
    head_box = (
        local_head_box[0],
        visible_box[1] + local_head_box[1],
        local_head_box[2],
        visible_box[1] + local_head_box[3],
    )
    return visible_box, head_box


def install_v5_run(source_directory: Path, assets_directory: Path) -> None:
    """Install a head-registered sixteen-phase crossed-leg run loop as frames 9-24."""

    assets_directory.mkdir(parents=True, exist_ok=True)
    source = source_directory / V5_RUN_SOURCE_NAME
    if not source.is_file():
        source = (
            Path(__file__).resolve().parent
            / "generated_sources"
            / V5_RUN_SOURCE_NAME
        )
    cells, _ = load_cells(
        source,
        columns=4,
        rows=4,
        snap_to_transparent_gaps=True,
    )
    if len(cells) != 16:
        raise ValueError("V11 run sheet must contain exactly sixteen cells")
    cells = [cells[index] for index in V5_RUN_SEQUENCE_INDICES]

    for frame_number, target_head_top, cell in zip(
        range(9, 25),
        V5_RUN_TARGET_HEAD_TOPS,
        cells,
        strict=True,
    ):
        visible_box, head_box = get_visible_and_head_boxes(cell)
        sprite = cell.crop(visible_box)
        head_width = head_box[2] - head_box[0]
        scale = V5_RUN_TARGET_HEAD_WIDTH / head_width
        sprite = resize_rgba_premultiplied(
            sprite,
            (
                max(1, round(sprite.width * scale)),
                max(1, round(sprite.height * scale)),
            ),
        )
        sprite = neutralize_green_fringe(sprite)

        visible_box, _ = get_visible_and_head_boxes(sprite)
        sprite = sprite.crop(visible_box)
        _, head_box = get_visible_and_head_boxes(sprite)
        head_center_x = (head_box[0] + head_box[2]) / 2
        destination = (
            round(RUNTIME_CANVAS_SIZE[0] / 2 - head_center_x),
            target_head_top - head_box[1],
        )
        if (destination[0] < 0 or destination[1] < 0 or
                destination[0] + sprite.width > RUNTIME_CANVAS_SIZE[0] or
                destination[1] + sprite.height > RUNTIME_CANVAS_SIZE[1]):
            raise ValueError(
                f"V5 run frame {frame_number} exceeds the runtime canvas: "
                f"sprite={sprite.size}, destination={destination}"
            )

        canvas = Image.new("RGBA", RUNTIME_CANVAS_SIZE, (0, 0, 0, 0))
        canvas.alpha_composite(sprite, destination)
        save_png_atomically(
            canvas,
            assets_directory / f"luban-run-frame-{frame_number:02d}.png",
        )


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
    """Match every standing action to the wake/run character scale offline."""

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


def install_v6_roam(source_directory: Path, assets_directory: Path) -> None:
    """Install three distinct, scale-registered 8-frame loops in every direction."""

    horizontal_cells, _ = load_cells(
        resolve_generated_source(
            source_directory,
            V6_ROAM_HORIZONTAL_SOURCE_NAME,
        ),
        columns=4,
        rows=6,
        snap_to_transparent_gaps=True,
    )
    vertical_cells, _ = load_cells(
        resolve_generated_source(
            source_directory,
            V7_ROAM_VERTICAL_SOURCE_NAME,
        ),
        columns=4,
        rows=6,
        snap_to_transparent_gaps=True,
    )
    if len(horizontal_cells) != 24 or len(vertical_cells) != 24:
        raise ValueError("V7 roam horizontal/vertical sources must each contain 24 cells")

    # Keep the approved horizontal V5 size as the reference.  Every generated
    # vertical loop uses one median cap registration per movement mode, avoiding
    # both cross-direction scale pops and per-frame rescaling.  The generated
    # source has inconsistent orientation across modes, so normalize every mode
    # to an upright, viewer-readable cap first.  Moving down then plays the same
    # climbing cycle in reverse (feet first) instead of turning the whole mascot
    # and its cap badge upside down.
    base_scale = get_direct_runtime_registration_scale(horizontal_cells)
    for mode_index, mode in enumerate(ROAM_MODES):
        start = mode_index * 8
        mode_horizontal_cells = horizontal_cells[start:start + 8]
        mode_vertical_cells = vertical_cells[start:start + 8]
        horizontal_sprites = [
            resize_sprite(cell, base_scale)
            for cell in mode_horizontal_cells
        ]
        horizontal_brim_width = median(
            get_blue_brim_box(sprite)[2] - get_blue_brim_box(sprite)[0]
            for sprite in horizontal_sprites
        )
        vertical_brim_width = median(
            get_blue_brim_box(crop_visible(cell))[2] -
            get_blue_brim_box(crop_visible(cell))[0]
            for cell in mode_vertical_cells
        )
        vertical_group_scale = horizontal_brim_width / vertical_brim_width
        vertical_head_centers = []
        for cell in mode_vertical_cells:
            visible = crop_visible(cell)
            brim = get_blue_brim_box(visible)
            vertical_head_centers.append(
                ((brim[1] + brim[3]) / 2) / max(1, visible.height)
            )
        rotate_to_upright = median(vertical_head_centers) > 0.5
        upright_vertical_sprites = []
        for cell in mode_vertical_cells:
            vertical_source = crop_visible(cell)
            if rotate_to_upright:
                vertical_source = vertical_source.transpose(
                    Image.Transpose.ROTATE_180
                )
            upright_vertical_sprites.append(
                resize_sprite(vertical_source, vertical_group_scale)
            )

        for frame_number, source_index in enumerate(
            V7_ROAM_SEQUENCE_INDICES,
            start=1,
        ):
            horizontal_sprite = horizontal_sprites[source_index]
            vertical_up_sprite = upright_vertical_sprites[source_index]
            reverse_index = (-source_index) % len(upright_vertical_sprites)
            vertical_down_sprite = upright_vertical_sprites[reverse_index]

            direction_sprites = {
                "horizontal": horizontal_sprite,
                "vertical-up": vertical_up_sprite,
                "vertical-down": vertical_down_sprite,
            }
            for direction, sprite in direction_sprites.items():
                frame = place_runtime_sprite(sprite)
                save_png_atomically(
                    frame,
                    assets_directory /
                    f"luban-roam-{mode}-{direction}-{frame_number:02d}.png",
                )

    for mode in ROAM_MODES:
        for frame_number in range(1, 5):
            legacy_path = assets_directory / (
                f"luban-roam-{mode}-vertical-{frame_number:02d}.png"
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
            "Install the generated wake and 24-frame action sheets with one "
            "shared scale and bottom-center registration, then resize the "
            "runtime canvases to 450x550."
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
            "Install idle-v2, wake-v2, run-entry-v2 + run-loop-v2, and "
            "edge-v2 without replacing roam assets."
        ),
    )
    selection.add_argument(
        "--v3-roam",
        action="store_true",
        help=(
            "Install the 72 approved v3 roam assets and remove obsolete "
            "four-frame vertical assets."
        ),
    )
    selection.add_argument(
        "--v5-run",
        action="store_true",
        help=(
            "Install the tracked sixteen-cell V11 run sheet as a normalized "
            "crossed-leg loop in frames 9 through 24."
        ),
    )
    selection.add_argument(
        "--v6-motion",
        action="store_true",
        help=(
            "Install the tracked scale-registered wake, standing actions, "
            "edge-peek, and 72-frame roam sets."
        ),
    )
    args = parser.parse_args()
    if args.v2_subset:
        install_v2_subset(args.source_directory, args.assets_directory)
    elif args.v3_roam:
        install_v3_roam(args.source_directory, args.assets_directory)
    elif args.v5_run:
        install_v5_run(args.source_directory, args.assets_directory)
    elif args.v6_motion:
        install_v6_motion(args.source_directory, args.assets_directory)
    else:
        install(args.source_directory, args.assets_directory)


if __name__ == "__main__":
    main()
