from __future__ import annotations

import argparse
from collections import deque
import os
from pathlib import Path
from statistics import median
import time

import cv2
import numpy as np
from PIL import Image, ImageDraw

from normalize_sprite import normalize
from split_sprite_sheet import (
    load_cells,
    resize_cells_to_width,
    resize_rgba_premultiplied,
    save_registered_groups,
)


ACTION_NAMES = ("yawn", "cry", "cute", "like", "eat")
TODO_POSE_NAME = "think"
SMOOTH_ACTION_NAMES = (*ACTION_NAMES, TODO_POSE_NAME)
RUNTIME_CANVAS_SIZE = (450, 550)
V9_WAKE_CHARACTER_SOURCE_NAME = "wake-v9-character-24-sheet-alpha.png"
V9_IDLE_PILLOW_SOURCE_NAME = "idle-pillow-v3-alpha.png"
V10_WAKE_BRIDGE_SOURCE_NAMES = {
    3: "wake-v10-bridge-03-04-rife-alpha.png",
    18: "wake-v10-bridge-18-19-rife-alpha.png",
    20: "wake-v10-bridge-20-21-composite-runtime-alpha.png",
}
V10_PRE_REGISTERED_WAKE_BRIDGES = frozenset({20})
EDGE_PEEK_SOURCE_NAME = "edge-v4-12-sheet-alpha.png"
REMINDER_SOURCE_NAME = "reminder-megaphone-v1-8-key-sheet-alpha.png"
REMINDER_BRIDGE_SOURCE_NAME = "reminder-megaphone-v2-8-bridge-sheet-alpha.png"
SNORE_BUBBLE_CLEAN_REFERENCE_NAME = "luban-idle-no-snore-patch-source.png"
SNORE_BUBBLE_PATCH_BOX = (143, 405, 179, 438)
SNORE_REGISTRATION_BOX = (40, 210, 445, 490)
V6_SCALE_REGISTERED_ACTIONS = SMOOTH_ACTION_NAMES
V6_ACTION_SOURCE_NAMES = {
    "yawn": "yawn-v7-24-sheet-alpha.png",
    "cry": "cry-v7-24-sheet-alpha.png",
    "cute": "cute-v7-24-sheet-alpha.png",
}
V10_ACTION_ENTRY_SOURCE_NAMES = {
    "yawn": "action-v10-yawn-entry-rife-alpha.png",
    "cry": "action-v10-cry-entry-rife-alpha.png",
    "cute": "action-v10-cute-entry-rife-alpha.png",
    "like": "action-v10-like-entry-hybrid-alpha.png",
    "eat": "action-v10-eat-entry-hybrid-alpha.png",
    "think": "action-v10-think-entry-hybrid-alpha.png",
}
V10_ACTION_INTERNAL_SOURCE_NAMES = {
    "yawn": {6: "action-v10-yawn-06-07-rife-alpha.png"},
    "cry": {3: "action-v10-cry-03-04-rife-alpha.png"},
    "think": {6: "action-v10-think-06-07-rife-alpha.png"},
}
V6_RUNTIME_INSET = 5
V6_RUNTIME_BOTTOM = RUNTIME_CANVAS_SIZE[1] - 10
V6_WAKE_TARGET_BRIM_WIDTH = 180
V9_PILLOW_SIZE = (430, 150)
V7_EDGE_TARGET_BRIM_WIDTH = 180
EDGE_PEEK_REVEAL_OFFSETS = {
    # K1 rests at the boundary, K2 leans out curiously, K3 is the full cute
    # reveal, and K4 makes a shy partial retreat before returning to K1.
    # Direction-specific offsets preserve both gripping hands at the contact
    # line while making the K1 -> K3 reveal visibly deeper.
    "left": (25, 15, 0, 15),
    "top": (18, 10, 0, 10),
    "bottom": (24, 12, 0, 12),
}
# The side-peek artwork ends with a short curved sleeve/body tail.  During the
# deepest reveal that curve starts a few pixels inside the runtime canvas,
# exposing a transparent wedge directly below the lower gripping hand.  The
# opposite edge mirrors this same left-facing sequence, so repairing this one
# contact strip fixes both sides without changing the authored source sheet.
EDGE_LEFT_SLEEVE_TAIL_ROWS = 50
EDGE_LEFT_SLEEVE_BOTTOM_GUTTER_ROWS = 2
EDGE_LEFT_SLEEVE_MAX_GAP = 24
EDGE_LEFT_SLEEVE_ALPHA_THRESHOLD = 24
# The older action sheets use several different head/body proportions. A
# single brim target either enlarges the head or makes the standing body pop
# shorter. These per-group targets keep every action visually aligned.
V6_ACTION_TARGET_BRIM_WIDTHS = {
    "yawn": 180,
    "cry": 180,
    "cute": 180,
    "like": 181,
    "eat": 180,
    "think": 180,
}
V6_ACTION_HEAD_CENTER_X = RUNTIME_CANVAS_SIZE[0] // 2
V9_WAKE_TARGET_HEAD_CENTERS = (
    190, 192, 193, 195, 196, 198, 199, 201,
    202, 204, 205, 207, 208, 210, 211, 213,
    214, 216, 217, 219, 220, 222, 223, 225,
)
V9_WAKE_TARGET_BRIM_CENTER_Y = (
    328, 324, 319, 314, 309, 304, 299, 294,
    289, 284, 279, 274, 269, 264, 259, 254,
    249, 244, 239, 234, 230, 226, 223, 220,
)
DISPLAY_CANVAS_SIZE = (399, 509)


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
    """Replace a PNG without exposing half-written frames to builds."""

    # Chroma spill can reappear after premultiplied resize converts back to
    # straight RGBA. Sanitize only after every transform/composite is final,
    # and canonicalize fully transparent pixels so the atlas never exposes a
    # green or colored fringe during scaling.
    image = neutralize_green_fringe(image)
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


def remove_baked_snore_bubble(
    frame: Image.Image,
    clean_reference: Image.Image,
) -> Image.Image:
    """Replace the old opaque nose bubble with registered clean face pixels."""

    target = np.asarray(frame.convert("RGBA"), dtype=np.uint8)
    reference = np.asarray(clean_reference.convert("RGBA"), dtype=np.uint8)
    if (
        target.shape != reference.shape
        or (target.shape[1], target.shape[0]) != RUNTIME_CANVAS_SIZE
    ):
        raise ValueError(
            "Snore cleanup requires matching 450x550 RGBA frames"
        )

    def registration_channel(rgba: np.ndarray) -> np.ndarray:
        gray = cv2.cvtColor(rgba[..., :3], cv2.COLOR_RGB2GRAY)
        alpha = rgba[..., 3].astype(np.float32) / 255
        return gray.astype(np.float32) * alpha / 255

    registration_mask = np.zeros(target.shape[:2], dtype=np.uint8)
    left, top, right, bottom = SNORE_REGISTRATION_BOX
    registration_mask[top:bottom, left:right] = 255
    patch_left, patch_top, patch_right, patch_bottom = (
        SNORE_BUBBLE_PATCH_BOX
    )
    registration_mask[
        patch_top - 12 : patch_bottom + 12,
        patch_left - 12 : patch_right + 12,
    ] = 0

    warp = np.eye(2, 3, dtype=np.float32)
    correlation, warp = cv2.findTransformECC(
        registration_channel(target),
        registration_channel(reference),
        warp,
        cv2.MOTION_AFFINE,
        (
            cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_COUNT,
            200,
            1e-7,
        ),
        registration_mask,
        5,
    )
    if not np.isfinite(correlation) or correlation < 0.95:
        raise ValueError(
            "Clean-face registration is too weak for snore cleanup: "
            f"{correlation:.6f}"
        )

    width, height = frame.size
    warped_reference = cv2.warpAffine(
        reference,
        warp,
        (width, height),
        flags=cv2.INTER_LANCZOS4 | cv2.WARP_INVERSE_MAP,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0, 0),
    )

    source_mask_image = Image.new("L", frame.size, 0)
    ImageDraw.Draw(source_mask_image).ellipse(
        (
            patch_left,
            patch_top,
            patch_right - 1,
            patch_bottom - 1,
        ),
        fill=255,
    )
    source_mask = np.asarray(source_mask_image, dtype=np.uint8)
    warped_mask = cv2.warpAffine(
        source_mask,
        warp,
        (width, height),
        flags=cv2.INTER_LINEAR | cv2.WARP_INVERSE_MAP,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=0,
    )
    select = warped_mask >= 128
    cleaned = target.copy()
    cleaned[select] = warped_reference[select]
    return Image.fromarray(cleaned, "RGBA")


def clean_existing_snore_keyframes(assets_directory: Path) -> None:
    clean_reference_path = (
        assets_directory / SNORE_BUBBLE_CLEAN_REFERENCE_NAME
    )
    first_wake_path = assets_directory / "luban-wake-01.png"
    second_wake_path = assets_directory / "luban-wake-02.png"
    with Image.open(clean_reference_path) as opened:
        clean_reference = opened.convert("RGBA").copy()
    with Image.open(first_wake_path) as opened:
        first_wake = opened.convert("RGBA").copy()
    with Image.open(second_wake_path) as opened:
        second_wake = opened.convert("RGBA").copy()

    cleaned_first = remove_baked_snore_bubble(
        first_wake,
        clean_reference,
    )
    cleaned_second = remove_baked_snore_bubble(
        second_wake,
        clean_reference,
    )
    save_png_atomically(cleaned_first, assets_directory / "luban-idle.png")
    save_png_atomically(cleaned_first, first_wake_path)
    save_png_atomically(cleaned_second, second_wake_path)


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


def neutralize_green_fringe(image: Image.Image) -> Image.Image:
    """Despill residual green-dominant edge RGB without changing alpha."""

    frame = image.convert("RGBA")
    pixels = bytearray(frame.tobytes())
    for offset in range(0, len(pixels), 4):
        red = pixels[offset]
        green = pixels[offset + 1]
        blue = pixels[offset + 2]
        alpha = pixels[offset + 3]
        if alpha == 0:
            pixels[offset] = 0
            pixels[offset + 1] = 0
            pixels[offset + 2] = 0
        elif green > red and green > blue:
            pixels[offset + 1] = max(red, blue)
    return Image.frombytes("RGBA", frame.size, bytes(pixels))


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
        if not allow_horizontal_crop:
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


def translate_rgba_without_wrap(
    image: Image.Image,
    *,
    x: int = 0,
    y: int = 0,
) -> Image.Image:
    """Translate a runtime sprite while clipping pixels outside its canvas."""

    frame = image.convert("RGBA")
    width, height = frame.size
    source_left = max(0, -x)
    source_top = max(0, -y)
    destination_left = max(0, x)
    destination_top = max(0, y)
    copy_width = min(width - source_left, width - destination_left)
    copy_height = min(height - source_top, height - destination_top)
    canvas = Image.new("RGBA", frame.size, (0, 0, 0, 0))
    if copy_width <= 0 or copy_height <= 0:
        return canvas

    visible = frame.crop(
        (
            source_left,
            source_top,
            source_left + copy_width,
            source_top + copy_height,
        )
    )
    canvas.alpha_composite(visible, (destination_left, destination_top))
    return canvas


def repair_left_edge_sleeve_continuation(frame: Image.Image) -> Image.Image:
    """Continue the lower purple sleeve through the left screen boundary.

    The added pixels are limited to the narrow transparent wedge beneath the
    lower gripping hand.  They are copied from purple sleeve pixels on the same
    scanline, so no character detail, scale, pose, or source artwork changes.
    The last two antialiased rows remain untouched to preserve the curved lower
    outline.
    """

    repaired = frame.convert("RGBA")
    alpha = repaired.getchannel("A")
    solid = alpha.point(
        lambda value: 255
        if value >= EDGE_LEFT_SLEEVE_ALPHA_THRESHOLD
        else 0
    )
    bounds = solid.getbbox()
    if bounds is None:
        return repaired

    pixels = repaired.load()
    start_y = max(bounds[1], bounds[3] - EDGE_LEFT_SLEEVE_TAIL_ROWS)
    end_y = max(start_y, bounds[3] - EDGE_LEFT_SLEEVE_BOTTOM_GUTTER_ROWS)
    for y in range(start_y, end_y):
        visible_x = [
            x
            for x in range(min(EDGE_LEFT_SLEEVE_MAX_GAP + 1, repaired.width))
            if pixels[x, y][3] >= EDGE_LEFT_SLEEVE_ALPHA_THRESHOLD
        ]
        if not visible_x:
            continue
        gap_width = visible_x[0]
        if gap_width <= 0 or gap_width > EDGE_LEFT_SLEEVE_MAX_GAP:
            continue

        # Pick only the existing purple clothing/body pixels as the extension
        # source.  This keeps skin from being stretched underneath the hand.
        purple_x = []
        for x in range(gap_width, min(repaired.width, gap_width + 72)):
            red, green, blue, pixel_alpha = pixels[x, y]
            if (
                pixel_alpha >= EDGE_LEFT_SLEEVE_ALPHA_THRESHOLD
                and blue * 100 >= red * 85
                and blue * 100 >= green * 115
                and red < 190
            ):
                purple_x.append(x)
        if not purple_x:
            continue

        for x in range(gap_width):
            source_index = min(gap_width - 1 - x, len(purple_x) - 1)
            pixels[x, y] = pixels[purple_x[source_index], y]
    return repaired


def resize_sprite(
    image: Image.Image,
    scale: float,
    *,
    allow_horizontal_crop: bool = False,
) -> Image.Image:
    sprite = crop_visible(image)
    if allow_horizontal_crop:
        # Pillow poses can be slightly wider than the runtime canvas. Limiting
        # their scale by the pillow width used to shrink the character's hat by
        # up to 8%, then the first standing pose suddenly grew again. Preserve
        # the common character scale and clip only the far pillow edges.
        scale = min(
            scale,
            (RUNTIME_CANVAS_SIZE[1] - V6_RUNTIME_INSET * 2) / sprite.height,
        )
    else:
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
    sprite = resize_sprite(
        sprite,
        target_brim_width / brim_width,
        allow_horizontal_crop=allow_horizontal_crop,
    )
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


def resize_runtime_frames(assets_directory: Path) -> None:
    prefixes = ["luban-wake"]
    prefixes.extend(f"luban-{action}-frame" for action in SMOOTH_ACTION_NAMES)
    prefixes.extend((
        "luban-edge-left",
        "luban-edge-top",
        "luban-edge-bottom",
    ))
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


def install(source_directory: Path, assets_directory: Path) -> None:
    """Install legacy wake, six smooth-pose, and edge-peek sheets."""

    loaded: list[tuple[list[Image.Image], int, Path, str]] = []
    wake_source = source_directory / "wake-12-sheet-alpha.png"
    wake_cells, wake_cell_width = load_cells(wake_source, columns=6, rows=2)
    if len(wake_cells) != 12:
        raise ValueError("Wake sheet must contain exactly 12 cells")
    loaded.append((wake_cells, wake_cell_width, assets_directory, "luban-wake"))

    for action in SMOOTH_ACTION_NAMES:
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
    resize_runtime_frames(assets_directory)


def install_v2_subset(source_directory: Path, assets_directory: Path) -> None:
    """Install the approved v2 idle, wake, and edge assets."""

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


def place_wake_character(
    image: Image.Image,
    *,
    head_center_x: int,
    brim_center_y: int,
) -> Image.Image:
    """Register a pillow-free wake pose without changing the cap scale."""

    sprite = neutralize_green_fringe(crop_visible(image))
    brim_box = get_blue_brim_box(sprite)
    brim_width = brim_box[2] - brim_box[0]
    sprite = resize_rgba_premultiplied(
        sprite,
        (
            max(1, round(sprite.width * V6_WAKE_TARGET_BRIM_WIDTH / brim_width)),
            max(1, round(sprite.height * V6_WAKE_TARGET_BRIM_WIDTH / brim_width)),
        ),
    )
    brim_box = get_blue_brim_box(sprite)
    brim_center_x = (brim_box[0] + brim_box[2]) / 2
    current_brim_center_y = (brim_box[1] + brim_box[3]) / 2
    y = round(brim_center_y - current_brim_center_y)

    # The generated standing keys keep the requested cap size but have a
    # slightly longer lower body than the six smooth-pose sheets. Compress only
    # the torso/leg tail when needed; the cap, face, and ear pieces remain
    # byte-for-byte at the common 180 px cap scale.
    overflow = y + sprite.height - V6_RUNTIME_BOTTOM
    if overflow > 0:
        split_y = min(
            sprite.height - 1,
            brim_box[3] + round(V6_WAKE_TARGET_BRIM_WIDTH * 0.60),
        )
        lower_height = sprite.height - split_y
        resized_lower_height = max(1, lower_height - overflow)
        if lower_height > 1 and resized_lower_height < lower_height:
            upper = sprite.crop((0, 0, sprite.width, split_y))
            lower = resize_rgba_premultiplied(
                sprite.crop((0, split_y, sprite.width, sprite.height)),
                (sprite.width, resized_lower_height),
            )
            compressed = Image.new(
                "RGBA",
                (sprite.width, split_y + resized_lower_height),
                (0, 0, 0, 0),
            )
            compressed.alpha_composite(upper, (0, 0))
            compressed.alpha_composite(lower, (0, split_y))
            sprite = compressed

    x = round(head_center_x - brim_center_x)
    x = min(RUNTIME_CANVAS_SIZE[0] - sprite.width, max(0, x))
    y = min(RUNTIME_CANVAS_SIZE[1] - sprite.height, max(0, y))
    canvas = Image.new("RGBA", RUNTIME_CANVAS_SIZE, (0, 0, 0, 0))
    canvas.alpha_composite(sprite, (x, y))
    return canvas


def install_v6_wake(source_directory: Path, assets_directory: Path) -> None:
    """Install a 27-frame pillow-free wake plus one static pillow layer.

    The three bridge poses are deliberately curated instead of applying a
    blanket whole-image dissolve. Two are RIFE-anime character-only
    intermediates and the kneel-to-stand bridge is a generated anatomical
    pose. The pillow is emitted once at the final display resolution and is
    rendered by WPF beneath every normal character pose. Keeping those pixels
    out of the changing atlas frames prevents alpha pulses and resampling
    shimmer while the character wakes or changes action pages.
    """

    cells, _ = load_cells(
        resolve_generated_source(source_directory, V9_WAKE_CHARACTER_SOURCE_NAME),
        columns=6,
        rows=4,
        snap_to_transparent_gaps=True,
    )
    if len(cells) != 24:
        raise ValueError("V9 wake character source must contain twenty-four cells")

    with Image.open(resolve_generated_source(
            source_directory,
            V9_IDLE_PILLOW_SOURCE_NAME)) as opened:
        pillow = neutralize_green_fringe(crop_visible(opened.convert("RGBA")))
    pillow = resize_rgba_premultiplied(pillow, V9_PILLOW_SIZE)
    pillow_layer = Image.new("RGBA", RUNTIME_CANVAS_SIZE, (0, 0, 0, 0))
    pillow_layer.alpha_composite(
        pillow,
        (
            (RUNTIME_CANVAS_SIZE[0] - pillow.width) // 2,
            V6_RUNTIME_BOTTOM - pillow.height,
        ),
    )

    display_pillow_height = round(
        RUNTIME_CANVAS_SIZE[1] * DISPLAY_CANVAS_SIZE[0] /
        RUNTIME_CANVAS_SIZE[0]
    )
    display_pillow = resize_rgba_premultiplied(
        pillow_layer,
        (DISPLAY_CANVAS_SIZE[0], display_pillow_height),
    )
    pillow_display_canvas = Image.new(
        "RGBA", DISPLAY_CANVAS_SIZE, (0, 0, 0, 0)
    )
    pillow_display_canvas.alpha_composite(
        display_pillow,
        (0, DISPLAY_CANVAS_SIZE[1] - display_pillow_height),
    )
    save_png_atomically(
        pillow_display_canvas,
        assets_directory / "luban-pillow-layer.png",
    )

    source_poses: list[tuple[Image.Image, int, int, bool]] = []
    for source_number, values in enumerate(zip(
            cells,
            V9_WAKE_TARGET_HEAD_CENTERS,
            V9_WAKE_TARGET_BRIM_CENTER_Y,
            strict=True), start=1):
        cell, target_head_center, target_brim_center_y = values
        source_poses.append((
            cell,
            target_head_center,
            target_brim_center_y,
            False,
        ))
        bridge_name = V10_WAKE_BRIDGE_SOURCE_NAMES.get(source_number)
        if bridge_name is None:
            continue

        with Image.open(resolve_generated_source(
                source_directory, bridge_name)) as opened:
            bridge = opened.convert("RGBA").copy()
        next_index = source_number
        source_poses.append((
            bridge,
            round((target_head_center +
                   V9_WAKE_TARGET_HEAD_CENTERS[next_index]) / 2),
            round((target_brim_center_y +
                   V9_WAKE_TARGET_BRIM_CENTER_Y[next_index]) / 2),
            source_number in V10_PRE_REGISTERED_WAKE_BRIDGES,
        ))

    if len(source_poses) != 27:
        raise ValueError("V10 wake path must contain twenty-seven poses")

    registered_frames: list[Image.Image] = []
    for (cell, target_head_center, target_brim_center_y,
         is_pre_registered) in source_poses:
        if is_pre_registered:
            character = neutralize_green_fringe(cell)
            if character.size != RUNTIME_CANVAS_SIZE:
                raise ValueError(
                    "Pre-registered wake bridge must match the runtime canvas"
                )
        else:
            character = place_wake_character(
                cell,
                head_center_x=target_head_center,
                brim_center_y=target_brim_center_y,
            )
        registered_frames.append(character)

    with Image.open(
        assets_directory / SNORE_BUBBLE_CLEAN_REFERENCE_NAME
    ) as opened:
        clean_reference = opened.convert("RGBA").copy()
    registered_frames[0] = remove_baked_snore_bubble(
        registered_frames[0],
        clean_reference,
    )
    registered_frames[1] = remove_baked_snore_bubble(
        registered_frames[1],
        clean_reference,
    )

    # Use the same character pixels for sleeping idle and the first wake sample;
    # the invariant pillow is rendered by its own visual layer. The user's
    # original pic assets stay untouched.
    save_png_atomically(registered_frames[0], assets_directory / "luban-idle.png")
    for frame_number, frame in enumerate(registered_frames, start=1):
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

        entry_source_name = V10_ACTION_ENTRY_SOURCE_NAMES.get(action)
        if entry_source_name is not None:
            with Image.open(resolve_generated_source(
                    source_directory, entry_source_name)) as opened:
                entry_bridge = neutralize_green_fringe(opened.convert("RGBA"))
            if entry_bridge.size != RUNTIME_CANVAS_SIZE:
                raise ValueError(
                    f"V10 {action} entry bridge must match the runtime canvas"
                )
            save_png_atomically(
                entry_bridge,
                assets_directory / f"luban-{action}-entry-bridge.png",
            )

        for after_frame, bridge_source_name in (
                V10_ACTION_INTERNAL_SOURCE_NAMES.get(action, {}).items()):
            with Image.open(resolve_generated_source(
                    source_directory, bridge_source_name)) as opened:
                internal_bridge = neutralize_green_fringe(opened.convert("RGBA"))
            if internal_bridge.size != RUNTIME_CANVAS_SIZE:
                raise ValueError(
                    f"V10 {action} internal bridge must match the runtime canvas"
                )
            save_png_atomically(
                internal_bridge,
                assets_directory /
                f"luban-{action}-bridge-{after_frame:02d}-{after_frame + 1:02d}.png",
            )


def build_edge_peek_unshifted_frames(
    source_directory: Path,
) -> dict[str, list[Image.Image]]:
    """Register edge poses before the Windows-boundary reveal translation."""

    cells, _ = load_cells(
        resolve_generated_source(source_directory, EDGE_PEEK_SOURCE_NAME),
        columns=4,
        rows=3,
        preserve_border_components=True,
        snap_to_transparent_gaps=True,
    )
    if len(cells) != 12:
        raise ValueError("Edge-peek source must contain twelve cells")
    groups = (
        ("left", cells[0:4], "left"),
        ("top", cells[4:8], "top"),
        ("bottom", cells[8:12], "bottom"),
    )
    registered: dict[str, list[Image.Image]] = {}
    for edge_name, edge_cells, anchor in groups:
        sprites = [crop_visible(cell) for cell in edge_cells]
        brim_widths = []
        for sprite in sprites:
            brim_box = get_blue_brim_box(sprite)
            brim_widths.append(brim_box[2] - brim_box[0])
        group_scale = V7_EDGE_TARGET_BRIM_WIDTH / median(brim_widths)
        registered[edge_name] = []
        for sprite in sprites:
            sprite = resize_sprite(sprite, group_scale)
            registered[edge_name].append(
                place_runtime_sprite(sprite, anchor=anchor)
            )
    return registered


def translate_edge_peek_frame(
    frame: Image.Image,
    edge_name: str,
    reveal_offset: int,
) -> Image.Image:
    """Move a registered pose out through one Windows boundary without wrap."""

    if edge_name not in EDGE_PEEK_REVEAL_OFFSETS:
        raise ValueError(f"Unsupported edge direction: {edge_name}")
    translated = translate_rgba_without_wrap(
        frame,
        x=-reveal_offset if edge_name == "left" else 0,
        y=(
            -reveal_offset
            if edge_name == "top"
            else reveal_offset if edge_name == "bottom" else 0
        ),
    )
    if edge_name == "left":
        translated = repair_left_edge_sleeve_continuation(translated)
    return translated


def install_edge_peek(source_directory: Path, assets_directory: Path) -> None:
    """Register each edge-peek group with one stable cap scale."""

    registered = build_edge_peek_unshifted_frames(source_directory)
    for edge_name, frames in registered.items():
        for frame_number, frame in enumerate(frames, start=1):
            reveal_offset = EDGE_PEEK_REVEAL_OFFSETS[edge_name][frame_number - 1]
            frame = translate_edge_peek_frame(
                frame, edge_name, reveal_offset
            )
            save_png_atomically(
                frame,
                assets_directory / f"luban-edge-{edge_name}-{frame_number:02d}.png",
            )


def install_v6_motion(source_directory: Path, assets_directory: Path) -> None:
    """Install the current wake, six smooth poses, and manual edge-peek assets."""

    assets_directory.mkdir(parents=True, exist_ok=True)
    install_v6_wake(source_directory, assets_directory)
    install_v6_actions(source_directory, assets_directory)
    install_edge_peek(source_directory, assets_directory)


def install_reminder(source_directory: Path, assets_directory: Path) -> None:
    """Install authored reminder poses and bridges on the shared runtime grid."""

    assets_directory.mkdir(parents=True, exist_ok=True)
    for source_name, destination_prefix in (
        (REMINDER_SOURCE_NAME, "luban-reminder-key"),
        (REMINDER_BRIDGE_SOURCE_NAME, "luban-reminder-bridge"),
    ):
        cells, _ = load_cells(
            resolve_generated_source(source_directory, source_name),
            columns=4,
            rows=2,
        )
        if len(cells) != 8:
            raise ValueError(
                f"Reminder source {source_name} must contain exactly eight cells"
            )

        for frame_number, cell in enumerate(cells, start=1):
            frame = register_by_brim(
                cell,
                target_brim_width=180,
                head_center_x=225,
            )
            if frame.size != RUNTIME_CANVAS_SIZE:
                raise AssertionError(
                    f"Reminder {destination_prefix} {frame_number:02d} is "
                    f"{frame.size}; expected {RUNTIME_CANVAS_SIZE}"
                )
            save_png_atomically(
                frame,
                assets_directory / f"{destination_prefix}-{frame_number:02d}.png",
            )


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Install generated wake, six smooth-pose, and manual edge-peek sheets "
            "with shared registration on 450x550 runtime canvases."
        )
    )
    parser.add_argument(
        "--source-directory",
        type=Path,
        default=Path("tools/generated_sources"),
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
        help="Install idle-v2, wake-v2, and edge-v2 assets.",
    )
    selection.add_argument(
        "--v6-motion",
        action="store_true",
        help=(
            "Install the tracked scale-registered wake, seven standing "
            "actions, and manual edge-peek sets."
        ),
    )
    selection.add_argument(
        "--edge-peek",
        action="store_true",
        help="Install only the current twelve authored edge-peek key poses.",
    )
    selection.add_argument(
        "--reminder",
        action="store_true",
        help=(
            "Install the tracked reminder/megaphone poses and bridge poses "
            "with a 180px brim and a 225px head center on 450x550 canvases."
        ),
    )
    selection.add_argument(
        "--clean-snore-bubble",
        action="store_true",
        help=(
            "Remove the legacy opaque nose bubble from idle and the first "
            "two wake keyframes before rebuilding the dense wake sequence."
        ),
    )
    args = parser.parse_args()
    if args.v2_subset:
        install_v2_subset(args.source_directory, args.assets_directory)
    elif args.edge_peek:
        install_edge_peek(args.source_directory, args.assets_directory)
    elif args.v6_motion:
        install_v6_motion(args.source_directory, args.assets_directory)
    elif args.reminder:
        install_reminder(args.source_directory, args.assets_directory)
    elif args.clean_snore_bubble:
        clean_existing_snore_keyframes(args.assets_directory)
    else:
        install(args.source_directory, args.assets_directory)


if __name__ == "__main__":
    main()
