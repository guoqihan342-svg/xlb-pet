from __future__ import annotations

import argparse
from pathlib import Path

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
    args = parser.parse_args()
    if args.v2_subset:
        install_v2_subset(args.source_directory, args.assets_directory)
    elif args.v3_roam:
        install_v3_roam(args.source_directory, args.assets_directory)
    else:
        install(args.source_directory, args.assets_directory)


if __name__ == "__main__":
    main()
