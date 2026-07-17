from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

from PIL import Image


CANVAS_SIZE = (900, 1100)
MAX_REGISTERED_SIZE = (860, 1060)
BOTTOM_MARGIN = 20


def remove_border_fragments(cell: Image.Image) -> Image.Image:
    cleaned = cell.copy()
    alpha = cleaned.getchannel("A")
    width, height = cleaned.size
    visible = alpha.load()
    visited = bytearray(width * height)
    components: list[tuple[list[tuple[int, int]], bool]] = []

    for start_y in range(height):
        for start_x in range(width):
            start_index = start_y * width + start_x
            if visited[start_index] or visible[start_x, start_y] == 0:
                continue

            queue: deque[tuple[int, int]] = deque([(start_x, start_y)])
            visited[start_index] = 1
            component: list[tuple[int, int]] = []
            touches_border = False
            while queue:
                x, y = queue.popleft()
                component.append((x, y))
                touches_border |= x == 0 or y == 0 or x == width - 1 or y == height - 1
                for next_y in range(max(0, y - 1), min(height, y + 2)):
                    for next_x in range(max(0, x - 1), min(width, x + 2)):
                        next_index = next_y * width + next_x
                        if visited[next_index] or visible[next_x, next_y] == 0:
                            continue
                        visited[next_index] = 1
                        queue.append((next_x, next_y))

            components.append((component, touches_border))

    if not components:
        return cleaned

    largest_size = max(len(component) for component, _ in components)
    for component, touches_border in components:
        if touches_border and len(component) < largest_size * 0.25:
            for x, y in component:
                cleaned.putpixel((x, y), (0, 0, 0, 0))

    return cleaned


def load_cells(
    source: Path,
    columns: int,
    rows: int,
) -> tuple[list[Image.Image], int]:
    with Image.open(source) as opened:
        sheet = opened.convert("RGBA")

    if sheet.width % columns != 0 or sheet.height % rows != 0:
        raise ValueError(
            f"Sheet size {sheet.size} is not divisible by {columns}x{rows}"
        )

    cell_width = sheet.width // columns
    cell_height = sheet.height // rows
    cells: list[Image.Image] = []
    for row in range(rows):
        for column in range(columns):
            cell = remove_border_fragments(sheet.crop(
                (
                    column * cell_width,
                    row * cell_height,
                    (column + 1) * cell_width,
                    (row + 1) * cell_height,
                )
            ))
            cells.append(cell)
    return cells, cell_width


def resize_cells_to_width(
    cells: list[Image.Image],
    cell_width: int,
    target_width: int,
) -> list[Image.Image]:
    if cell_width == target_width:
        return cells

    scale = target_width / cell_width
    return [
        cell.resize(
            (
                target_width,
                max(1, round(cell.height * scale)),
            ),
            Image.Resampling.LANCZOS,
        )
        for cell in cells
    ]


def save_registered_groups(
    groups: list[tuple[list[Image.Image], Path, str]],
) -> None:
    all_cells = [cell for cells, _, _ in groups for cell in cells]
    visible_boxes = [cell.getchannel("A").getbbox() for cell in all_cells]
    if any(box is None for box in visible_boxes):
        raise ValueError("Every sprite-sheet cell must contain a visible frame")

    boxes = [box for box in visible_boxes if box is not None]
    widest_frame = max(box[2] - box[0] for box in boxes)
    tallest_frame = max(box[3] - box[1] for box in boxes)
    scale = min(
        MAX_REGISTERED_SIZE[0] / widest_frame,
        MAX_REGISTERED_SIZE[1] / tallest_frame,
    )
    box_index = 0
    for cells, destination, prefix in groups:
        destination.mkdir(parents=True, exist_ok=True)
        for frame_index, cell in enumerate(cells, start=1):
            box = boxes[box_index]
            box_index += 1
            registered = cell.crop(box).resize(
                (
                    max(1, round((box[2] - box[0]) * scale)),
                    max(1, round((box[3] - box[1]) * scale)),
                ),
                Image.Resampling.LANCZOS,
            )
            canvas = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
            x = (CANVAS_SIZE[0] - registered.width) // 2
            y = CANVAS_SIZE[1] - BOTTOM_MARGIN - registered.height
            canvas.alpha_composite(registered, (x, y))

            final_path = destination / f"{prefix}-{frame_index:02d}.png"
            canvas.save(final_path, optimize=True)


def split_sheet(
    source: Path,
    destination: Path,
    prefix: str,
    columns: int,
    rows: int,
    paired_source: Path | None = None,
    paired_destination: Path | None = None,
    paired_prefix: str | None = None,
) -> None:
    cells, cell_width = load_cells(source, columns, rows)
    loaded_groups = [(cells, cell_width, destination, prefix)]
    if paired_source is not None:
        if paired_destination is None or paired_prefix is None:
            raise ValueError(
                "Paired destination and prefix are required with paired source"
            )
        paired_cells, paired_cell_width = load_cells(
            paired_source,
            columns,
            rows,
        )
        loaded_groups.append((
            paired_cells,
            paired_cell_width,
            paired_destination,
            paired_prefix,
        ))

    reference_cell_width = max(group[1] for group in loaded_groups)
    groups = [
        (
            resize_cells_to_width(group_cells, group_cell_width, reference_cell_width),
            group_destination,
            group_prefix,
        )
        for group_cells, group_cell_width, group_destination, group_prefix in loaded_groups
    ]
    save_registered_groups(groups)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Split a transparent sprite sheet and normalize every cell."
    )
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("prefix")
    parser.add_argument("--columns", type=int, default=4)
    parser.add_argument("--rows", type=int, default=3)
    parser.add_argument("--paired-source", type=Path)
    parser.add_argument("--paired-destination", type=Path)
    parser.add_argument("--paired-prefix")
    args = parser.parse_args()
    split_sheet(
        args.source,
        args.destination,
        args.prefix,
        args.columns,
        args.rows,
        args.paired_source,
        args.paired_destination,
        args.paired_prefix,
    )


if __name__ == "__main__":
    main()
