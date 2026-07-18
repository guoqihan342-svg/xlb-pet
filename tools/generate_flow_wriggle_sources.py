from __future__ import annotations

import argparse
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from install_generated_motion_assets import (
    RUNTIME_CANVAS_SIZE,
    V13_WRIGGLE_CORNER_SOURCE_NAMES,
    V8_WRIGGLE_HORIZONTAL_SOURCE_NAME,
    WRIGGLE_HORIZONTAL_HEAD_CENTER_X,
    WRIGGLE_TARGET_BRIM_WIDTH,
    WRIGGLE_VERTICAL_HEAD_CENTER_X,
    crop_visible,
    get_direct_runtime_registration_scale,
    get_red_cap_center,
    get_topmost_blue_brim_box,
    load_cells,
    load_trimmed_grid_cells,
    normalize_wriggle_brim,
    place_corner_sprite,
    place_wriggle_sprite,
    remove_white_grid_gutter,
    resize_sprite,
    resolve_generated_source,
    save_png_atomically,
)


HORIZONTAL_PHASE_ORDER = (
    4, 3, 6, 17, 23, 22, 2, 8, 1, 7, 14, 11,
    19, 20, 12, 5, 15, 16, 24, 9, 10, 21, 13,
)
VERTICAL_V14_KEYFRAME_INDICES = (1, 2, 4, 6, 8, 10, 12, 14)
VERTICAL_V14_SOURCE_NAME = "wriggle-vertical-v14-16-keys-sheet-alpha.png"
OUTPUT_FRAME_COUNT = 48
SHEET_COLUMNS = 8
SHEET_ROWS = 6


def make_flow_gray(frame: Image.Image) -> np.ndarray:
    rgba = np.asarray(frame.convert("RGBA"), dtype=np.float32)
    alpha = rgba[:, :, 3:4] / 255.0
    composited = rgba[:, :, :3] * alpha + 255.0 * (1.0 - alpha)
    return cv2.cvtColor(composited.astype(np.uint8), cv2.COLOR_RGB2GRAY)


def premultiplied_array(frame: Image.Image) -> np.ndarray:
    rgba = np.asarray(frame.convert("RGBA"), dtype=np.float32) / 255.0
    rgba[:, :, :3] *= rgba[:, :, 3:4]
    return rgba


def calculate_flow(first: Image.Image, second: Image.Image) -> tuple[np.ndarray, np.ndarray]:
    forward_solver = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
    backward_solver = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
    first_gray = make_flow_gray(first)
    second_gray = make_flow_gray(second)
    return (
        forward_solver.calc(first_gray, second_gray, None),
        backward_solver.calc(second_gray, first_gray, None),
    )


def warp_with_flow(
    pixels: np.ndarray,
    flow: np.ndarray,
    amount: float,
) -> np.ndarray:
    height, width = pixels.shape[:2]
    x, y = np.meshgrid(
        np.arange(width, dtype=np.float32),
        np.arange(height, dtype=np.float32),
    )
    map_x = x - flow[:, :, 0] * amount
    map_y = y - flow[:, :, 1] * amount
    return cv2.remap(
        pixels,
        map_x,
        map_y,
        cv2.INTER_CUBIC,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=0,
    )


def flow_inbetween(
    first: Image.Image,
    second: Image.Image,
    amount: float,
    forward_flow: np.ndarray,
    backward_flow: np.ndarray,
) -> Image.Image:
    first_warped = warp_with_flow(
        premultiplied_array(first),
        forward_flow,
        amount,
    )
    second_warped = warp_with_flow(
        premultiplied_array(second),
        backward_flow,
        1.0 - amount,
    )
    blended = first_warped * (1.0 - amount) + second_warped * amount
    alpha = np.clip(blended[:, :, 3:4], 0.0, 1.0)
    rgb = np.zeros_like(blended[:, :, :3])
    np.divide(
        blended[:, :, :3],
        alpha,
        out=rgb,
        where=alpha > 1.0 / 255.0,
    )
    rgba = np.concatenate((np.clip(rgb, 0.0, 1.0), alpha), axis=2)
    return Image.fromarray(np.rint(rgba * 255.0).astype(np.uint8), "RGBA")


def resample_closed_loop(
    keyframes: list[Image.Image],
    output_count: int = OUTPUT_FRAME_COUNT,
) -> list[Image.Image]:
    if len(keyframes) < 2:
        raise ValueError("A flow loop requires at least two keyframes")

    flows = [
        calculate_flow(keyframes[index], keyframes[(index + 1) % len(keyframes)])
        for index in range(len(keyframes))
    ]
    output: list[Image.Image] = []
    for output_index in range(output_count):
        phase = output_index * len(keyframes) / output_count
        key_index = int(phase) % len(keyframes)
        amount = phase - int(phase)
        if amount <= 1e-9:
            output.append(keyframes[key_index].copy())
            continue

        next_index = (key_index + 1) % len(keyframes)
        forward, backward = flows[key_index]
        output.append(flow_inbetween(
            keyframes[key_index],
            keyframes[next_index],
            amount,
            forward,
            backward,
        ))
    return output


def resample_open_path(
    keyframes: list[Image.Image],
    output_count: int = OUTPUT_FRAME_COUNT,
) -> list[Image.Image]:
    if len(keyframes) < 2 or output_count < len(keyframes):
        raise ValueError("An open flow path requires enough output frames")
    flows = [
        calculate_flow(keyframes[index], keyframes[index + 1])
        for index in range(len(keyframes) - 1)
    ]
    output: list[Image.Image] = []
    for output_index in range(output_count):
        phase = output_index * (len(keyframes) - 1) / (output_count - 1)
        key_index = min(int(phase), len(keyframes) - 2)
        amount = phase - key_index
        if output_index == output_count - 1:
            output.append(keyframes[-1].copy())
        elif amount <= 1e-9:
            output.append(keyframes[key_index].copy())
        else:
            output.append(flow_inbetween(
                keyframes[key_index],
                keyframes[key_index + 1],
                amount,
                *flows[key_index],
            ))
    return output


def bottom_register_corner_frames(frames: list[Image.Image]) -> list[Image.Image]:
    """Remove flow residue and keep the screen-contact baseline pixel-stable."""

    registered = [frames[0].copy()]
    for frame in frames[1:-1]:
        rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8).copy()
        # A weak subpixel foot can disappear after the final 190×242 Fant
        # downsample and make the contact line jump.  Register against a solid
        # alpha edge, then let the final scaler recreate the soft boundary.
        rgba[rgba[:, :, 3] <= 64] = 0
        cleaned = Image.fromarray(rgba, "RGBA")
        cap_center_x, _ = get_red_cap_center(cleaned)
        registered.append(place_corner_sprite(
            crop_visible(cleaned),
            target_cap_center_x=cap_center_x,
        ))
    registered.append(frames[-1].copy())
    return registered


def load_horizontal_keys(source_directory: Path) -> list[Image.Image]:
    cells, _ = load_trimmed_grid_cells(
        resolve_generated_source(
            source_directory,
            V8_WRIGGLE_HORIZONTAL_SOURCE_NAME,
        ),
        columns=6,
        rows=4,
    )
    scale = get_direct_runtime_registration_scale(cells)
    sprites = [resize_sprite(cell, scale) for cell in cells]
    normalized = []
    for sprite in sprites:
        brim = get_topmost_blue_brim_box(sprite)
        brim_width = brim[2] - brim[0]
        normalized.append(resize_sprite(
            sprite,
            WRIGGLE_TARGET_BRIM_WIDTH / brim_width,
        ))
    placed = [
        place_wriggle_sprite(
            sprite,
            head_center_x=WRIGGLE_HORIZONTAL_HEAD_CENTER_X,
        )
        for sprite in normalized
    ]
    return [placed[number - 1] for number in HORIZONTAL_PHASE_ORDER]


def load_vertical_keys(source_directory: Path) -> list[Image.Image]:
    cells, _ = load_cells(
        resolve_generated_source(
            source_directory,
            VERTICAL_V14_SOURCE_NAME,
        ),
        columns=4,
        rows=4,
    )
    sprites = normalize_wriggle_brim(
        [crop_visible(remove_white_grid_gutter(cell)) for cell in cells],
        WRIGGLE_TARGET_BRIM_WIDTH,
    )
    placed = [
        place_wriggle_sprite(
            sprite,
            head_center_x=WRIGGLE_VERTICAL_HEAD_CENTER_X,
        )
        for sprite in sprites
    ]
    return [placed[index] for index in VERTICAL_V14_KEYFRAME_INDICES]


def load_corner_keys(
    source_directory: Path,
    horizontal_endpoint: Image.Image,
    vertical_endpoint: Image.Image,
) -> list[Image.Image]:
    groups: list[list[Image.Image]] = []
    for source_name in V13_WRIGGLE_CORNER_SOURCE_NAMES:
        cells, _ = load_cells(
            resolve_generated_source(source_directory, source_name),
            columns=4,
            rows=4,
        )
        groups.append([remove_white_grid_gutter(cell) for cell in cells])

    # Avoid the two generated segment starts that repeat a shorter pose, and
    # stop the middle segment before it over-extends into a tall standing bob.
    selected = [*groups[0], *groups[1][2:10], *groups[2][2:]]
    sprites = normalize_wriggle_brim(
        [crop_visible(cell) for cell in selected],
        WRIGGLE_TARGET_BRIM_WIDTH,
    )
    start_cap_x, _ = get_red_cap_center(horizontal_endpoint)
    end_cap_x, _ = get_red_cap_center(vertical_endpoint)
    keys = [horizontal_endpoint.copy()]
    for index, sprite in enumerate(sprites, start=1):
        progress = index / (len(sprites) + 1)
        keys.append(place_corner_sprite(
            sprite,
            target_cap_center_x=(
                start_cap_x + (end_cap_x - start_cap_x) * progress
            ),
        ))
    keys.append(vertical_endpoint.copy())
    return keys


def save_sheet(frames: list[Image.Image], destination: Path) -> None:
    if len(frames) != SHEET_COLUMNS * SHEET_ROWS:
        raise ValueError("Flow sheet must contain exactly 48 frames")
    sheet = Image.new(
        "RGBA",
        (
            RUNTIME_CANVAS_SIZE[0] * SHEET_COLUMNS,
            RUNTIME_CANVAS_SIZE[1] * SHEET_ROWS,
        ),
        (0, 0, 0, 0),
    )
    for index, frame in enumerate(frames):
        sheet.alpha_composite(
            frame,
            (
                (index % SHEET_COLUMNS) * RUNTIME_CANVAS_SIZE[0],
                (index // SHEET_COLUMNS) * RUNTIME_CANVAS_SIZE[1],
            ),
        )
    save_png_atomically(sheet, destination)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Generate 48-frame motion-compensated wriggle source sheets."
    )
    parser.add_argument("--source-directory", type=Path, default=Path("pic"))
    parser.add_argument(
        "--output-directory",
        type=Path,
        default=Path("tools/generated_sources"),
    )
    args = parser.parse_args()
    args.output_directory.mkdir(parents=True, exist_ok=True)

    horizontal_frames = resample_closed_loop(
        load_horizontal_keys(args.source_directory)
    )
    vertical_frames = resample_closed_loop(load_vertical_keys(args.source_directory))
    corner_frames = bottom_register_corner_frames(resample_open_path(
        load_corner_keys(
            args.source_directory,
            horizontal_frames[0],
            vertical_frames[0],
        )
    ))
    save_sheet(
        horizontal_frames,
        args.output_directory / "wriggle-horizontal-v11-48-flow-sheet-alpha.png",
    )
    save_sheet(
        vertical_frames,
        args.output_directory / "wriggle-vertical-v15-48-flow-sheet-alpha.png",
    )
    save_sheet(
        corner_frames,
        args.output_directory / "wriggle-corner-v16-48-flow-sheet-alpha.png",
    )


if __name__ == "__main__":
    main()
