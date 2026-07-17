from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

from PIL import Image


CANVAS_SIZE = (900, 1100)
MAX_REGISTERED_SIZE = (860, 1060)
BOTTOM_MARGIN = 20


def resize_rgba_premultiplied(
    image: Image.Image,
    size: tuple[int, int],
) -> Image.Image:
    """Resize RGBA without bleeding hidden chroma-key RGB into soft edges."""

    return (
        image.convert("RGBA")
        .convert("RGBa")
        .resize(size, Image.Resampling.LANCZOS)
        .convert("RGBA")
    )


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


def _find_nearest_transparent_boundary(
    projection: list[int],
    nominal: int,
    radius: int,
) -> int:
    start = max(1, nominal - radius)
    end = min(len(projection) - 1, nominal + radius + 1)
    zero_runs: list[tuple[int, int]] = []
    run_start: int | None = None
    for index in range(start, end):
        if projection[index] == 0 and run_start is None:
            run_start = index
        elif projection[index] != 0 and run_start is not None:
            zero_runs.append((run_start, index))
            run_start = None
    if run_start is not None:
        zero_runs.append((run_start, end))

    if zero_runs:
        nearest = min(
            zero_runs,
            key=lambda run: abs(((run[0] + run[1] - 1) / 2) - nominal),
        )
        return round((nearest[0] + nearest[1]) / 2)

    minimum = min(projection[start:end])
    candidates = [
        index
        for index in range(start, end)
        if projection[index] == minimum
    ]
    return min(candidates, key=lambda index: abs(index - nominal))


def _alpha_projection(alpha: Image.Image, horizontal: bool) -> list[int]:
    binary = alpha.point(lambda value: 255 if value > 16 else 0)
    size = (binary.width, 1) if horizontal else (1, binary.height)
    return list(binary.resize(size, Image.Resampling.BOX).getdata())


def load_cells(
    source: Path,
    columns: int,
    rows: int,
    *,
    preserve_border_components: bool = False,
    snap_to_transparent_gaps: bool = False,
) -> tuple[list[Image.Image], float]:
    with Image.open(source) as opened:
        sheet = opened.convert("RGBA")

    if columns <= 0 or rows <= 0:
        raise ValueError("Sprite-sheet columns and rows must be positive")

    nominal_cell_width = sheet.width / columns
    nominal_cell_height = sheet.height / rows
    y_boundaries = [0]
    if snap_to_transparent_gaps:
        row_projection = _alpha_projection(sheet.getchannel("A"), horizontal=False)
        for index in range(1, rows):
            y_boundaries.append(_find_nearest_transparent_boundary(
                row_projection,
                round(index * nominal_cell_height),
                round(nominal_cell_height * 0.4),
            ))
    else:
        y_boundaries.extend(
            round(index * sheet.height / rows)
            for index in range(1, rows)
        )
    y_boundaries.append(sheet.height)

    cells: list[Image.Image] = []
    for row in range(rows):
        x_boundaries = [0]
        if snap_to_transparent_gaps:
            row_alpha = sheet.getchannel("A").crop((
                0,
                y_boundaries[row],
                sheet.width,
                y_boundaries[row + 1],
            ))
            column_projection = _alpha_projection(row_alpha, horizontal=True)
            for index in range(1, columns):
                x_boundaries.append(_find_nearest_transparent_boundary(
                    column_projection,
                    round(index * nominal_cell_width),
                    round(nominal_cell_width * 0.35),
                ))
        else:
            x_boundaries.extend(
                round(index * sheet.width / columns)
                for index in range(1, columns)
            )
        x_boundaries.append(sheet.width)

        for column in range(columns):
            cell = sheet.crop((
                x_boundaries[column],
                y_boundaries[row],
                x_boundaries[column + 1],
                y_boundaries[row + 1],
            ))
            if not preserve_border_components:
                cell = remove_border_fragments(cell)
            cells.append(cell)
    return cells, nominal_cell_width


def resize_cells_to_width(
    cells: list[Image.Image],
    cell_width: float,
    target_width: float,
) -> list[Image.Image]:
    scale = target_width / cell_width
    if abs(scale - 1) < 1e-9:
        return cells

    resized: list[Image.Image] = []
    for cell in cells:
        resized.append(resize_rgba_premultiplied(
            cell,
            (
                max(1, round(cell.width * scale)),
                max(1, round(cell.height * scale)),
            ),
        ))
    return resized


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
            registered = resize_rgba_premultiplied(
                cell.crop(box),
                (
                    max(1, round((box[2] - box[0]) * scale)),
                    max(1, round((box[3] - box[1]) * scale)),
                ),
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
    preserve_border_components: bool = False,
    snap_to_transparent_gaps: bool = False,
) -> None:
    cells, cell_width = load_cells(
        source,
        columns,
        rows,
        preserve_border_components=preserve_border_components,
        snap_to_transparent_gaps=snap_to_transparent_gaps,
    )
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
            preserve_border_components=preserve_border_components,
            snap_to_transparent_gaps=snap_to_transparent_gaps,
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
    parser.add_argument(
        "--preserve-border-components",
        action="store_true",
        help=(
            "Do not discard small alpha components touching cell borders. "
            "Use this for intentional edge-anchored sprites such as hands."
        ),
    )
    parser.add_argument(
        "--snap-to-transparent-gaps",
        action="store_true",
        help=(
            "Move nominal grid cuts to nearby transparent gaps so generated "
            "sprites crossing an equal cell boundary are not clipped."
        ),
    )
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
        args.preserve_border_components,
        args.snap_to_transparent_gaps,
    )


if __name__ == "__main__":
    main()
