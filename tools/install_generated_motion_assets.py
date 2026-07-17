from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image

from split_sprite_sheet import (
    load_cells,
    resize_cells_to_width,
    save_registered_groups,
)


ACTIONS = ("yawn", "cry", "run", "cute", "like", "eat", "wave", "think")
ROAM_MODES = ("wriggle", "crawl", "hop")
RUNTIME_CANVAS_SIZE = (450, 550)


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
        for direction in ("horizontal", "vertical")
    )

    for prefix in prefixes:
        for path in sorted(assets_directory.glob(f"{prefix}-*.png")):
            with Image.open(path) as opened:
                frame = opened.convert("RGBA")
            if frame.size != RUNTIME_CANVAS_SIZE:
                frame = frame.resize(
                    RUNTIME_CANVAS_SIZE,
                    Image.Resampling.LANCZOS,
                )
            frame.save(path, optimize=True)


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
    args = parser.parse_args()
    install(args.source_directory, args.assets_directory)


if __name__ == "__main__":
    main()
