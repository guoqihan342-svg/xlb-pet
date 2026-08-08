from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path
from typing import Iterable

import cv2
import numpy as np
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
GENERATED_SOURCES = ROOT / "tools" / "generated_sources"

CANVAS_SIZE = (450, 550)
WORK_BOTTOM = 540
WORK_MAX_SIZE = (420, 480)

ENTER_FRAME_COUNT = 48
LOOP_FRAME_COUNT = 96
SERIOUS_LOOP_FRAME_COUNT = 96
SERIOUS_EXIT_FRAME_COUNT = 24
SERIOUS_ENTER_SOURCE_FRAME_INDICES = (23, 20, 16, 13, 10, 7, 3, 0)

WORK_ANCHOR_PATH = GENERATED_SOURCES / "luban-work-home-row-v5-alpha.png"
KEYBOARD_UNDERLAY_PATH = GENERATED_SOURCES / "luban-work-keyboard-underlay-v5-alpha.png"
LEFT_INDEX_DOWN_PATH = GENERATED_SOURCES / "luban-work-left-index-down-v5-alpha.png"
RIGHT_INDEX_DOWN_PATH = GENERATED_SOURCES / "luban-work-right-index-down-v5-alpha.png"
LEFT_MIDDLE_DOWN_PATH = GENERATED_SOURCES / "luban-work-left-middle-down-v5-alpha.png"
RIGHT_MIDDLE_DOWN_PATH = GENERATED_SOURCES / "luban-work-right-middle-down-v5-alpha.png"
SERIOUS_REFERENCE_PATH = GENERATED_SOURCES / "luban-work-serious-v2-alpha.png"
IDLE_PATH = ASSETS / "luban-idle.png"
QA_PATH = GENERATED_SOURCES / "luban-work-animation-qa.json"
ARM_CONTACT_PATH = GENERATED_SOURCES / "luban-work-arm-motion-contact-v5.png"
FACE_CONTACT_PATH = GENERATED_SOURCES / "luban-work-face-comparison-v5.png"
NORMALIZED_WORK_PATH = GENERATED_SOURCES / "luban-work-home-row-normalized-v5-alpha.png"
NORMALIZED_UNDERLAY_PATH = (
    GENERATED_SOURCES / "luban-work-keyboard-underlay-normalized-v5-alpha.png"
)
NORMALIZED_LEFT_INDEX_DOWN_PATH = (
    GENERATED_SOURCES / "luban-work-left-index-down-normalized-v5-alpha.png"
)
NORMALIZED_RIGHT_INDEX_DOWN_PATH = (
    GENERATED_SOURCES / "luban-work-right-index-down-normalized-v5-alpha.png"
)
NORMALIZED_LEFT_MIDDLE_DOWN_PATH = (
    GENERATED_SOURCES / "luban-work-left-middle-down-normalized-v5-alpha.png"
)
NORMALIZED_RIGHT_MIDDLE_DOWN_PATH = (
    GENERATED_SOURCES / "luban-work-right-middle-down-normalized-v5-alpha.png"
)
NORMALIZED_SERIOUS_REFERENCE_PATH = (
    GENERATED_SOURCES / "luban-work-serious-reference-normalized-v2-alpha.png"
)
NORMALIZED_SERIOUS_WORK_PATH = (
    GENERATED_SOURCES / "luban-work-serious-normalized-v2-alpha.png"
)

# The v4 rig keeps the shoulder, elbow, torso and keyboard fixed.  Only the
# compact hand/wrist patches below may change.  The generated targets are real
# index/middle-finger contact poses; dense local morphing supplies the lift,
# strike and rebound without moving an entire forearm like the old v3 rig.
LEFT_HAND_MOTION_REGION = (146.0, 394.0, 38.0, 35.0)
RIGHT_HAND_MOTION_REGION = (254.0, 414.0, 48.0, 38.0)
HAND_MOTION_REGIONS = (LEFT_HAND_MOTION_REGION, RIGHT_HAND_MOTION_REGION)
LEFT_WRIST_REGION = (171.0, 374.0, 25.0, 22.0)
RIGHT_WRIST_REGION = (276.0, 390.0, 29.0, 25.0)
WRIST_MOTION_REGIONS = (LEFT_WRIST_REGION, RIGHT_WRIST_REGION)
SHOULDER_LOCK_REGIONS = (
    (178.0, 337.0, 31.0, 24.0),
    (278.0, 350.0, 36.0, 27.0),
)
TORSO_LOCK_REGION = (228.0, 354.0, 48.0, 34.0)
FACE_REGION = (122, 154, 342, 306)
HEAD_LOCK_REGION = (105, 55, 440, 312)
PROTECTED_COMPUTER_REGION = (18, 322, 108, 528)
COMPUTER_LOCK_REGION = (15, 315, 142, 540)
LEFT_POSE_SEARCH_ROI = (150, 370, 207, 426)
RIGHT_POSE_SEARCH_ROI = (222, 385, 302, 442)
LEFT_POSE_ALLOWED_ROI = (145, 362, 213, 432)
RIGHT_POSE_ALLOWED_ROI = (217, 378, 308, 448)

# Each rig is a tiny two-joint raster mesh: the base barely follows while the
# fingertip travels 2.8-3.0 px.  Polygons deliberately stop before the cuffs,
# palm roots and neighbouring fingers, so their outlines/highlights are copied
# verbatim from the neutral frame instead of being cross-faded.
FINGER_RIGS = (
    {
        "name": "left-index",
        "allowed_roi": LEFT_POSE_ALLOWED_ROI,
        "polygon": ((183, 382), (198, 385), (201, 397), (198, 408), (192, 413), (185, 410), (181, 401), (181, 390)),
        "regions": ((190.0, 391.0, 9.0, 12.0), (193.0, 404.0, 10.0, 12.0)),
        "deltas": ((0.05, 0.50), (0.30, 3.55)),
    },
    {
        "name": "right-index",
        "allowed_roi": RIGHT_POSE_ALLOWED_ROI,
        "polygon": ((218, 398), (230, 392), (240, 396), (243, 407), (239, 418), (233, 421), (225, 417), (219, 411)),
        "regions": ((229.0, 401.0, 10.0, 11.0), (231.0, 412.0, 11.0, 12.0)),
        "deltas": ((-0.05, 0.50), (-0.30, 3.55)),
    },
    {
        "name": "left-middle",
        "allowed_roi": LEFT_POSE_ALLOWED_ROI,
        "polygon": ((168, 383), (181, 382), (185, 392), (185, 403), (181, 411), (175, 414), (168, 410), (165, 401), (166, 390)),
        "regions": ((176.0, 392.0, 9.0, 11.0), (177.0, 406.0, 9.5, 12.0)),
        "deltas": ((0.0, 0.50), (-0.25, 3.80)),
    },
    {
        "name": "right-middle",
        "allowed_roi": RIGHT_POSE_ALLOWED_ROI,
        "polygon": ((233, 406), (245, 400), (256, 403), (260, 413), (256, 424), (250, 428), (241, 425), (234, 419)),
        "regions": ((245.0, 410.0, 10.0, 11.0), (247.0, 421.0, 11.0, 11.0)),
        "deltas": ((0.0, 0.50), (0.25, 3.80)),
    },
)

# v5 renders each hand as an independent transparent layer over a hand-free
# keyboard underlay.  The control points below were read from the normalized
# home-row drawing and the four generated contact references.  The generated
# references are anatomical guides only: their repainted full canvases never
# enter a runtime frame.
V5_HAND_RIGS = (
    {
        "name": "left",
        "allowed_roi": (147, 350, 212, 427),
        "hand_contour": (
            (158, 374), (187, 374), (195, 379), (198, 389),
            (205, 397), (205, 405), (201, 412), (195, 415),
            (188, 412), (183, 415), (176, 414), (171, 411),
            (166, 413), (160, 410), (157, 402), (156, 387),
        ),
        "cuff_contour": (
            (148, 350), (177, 347), (192, 354), (201, 366),
            (198, 380), (187, 374), (158, 374), (153, 385),
            (148, 376),
        ),
        "wrist": (176.0, 376.0),
        "palm": (181.0, 391.0),
        "cuff_top": (174.0, 352.0),
        "cuff_bottom": (176.0, 371.0),
        "fingers": {
            "index": ((193.0, 392.0), (198.0, 401.0), (198.0, 411.0)),
            "middle": ((183.0, 393.0), (185.0, 405.0), (186.0, 420.0)),
            "ring": ((172.0, 392.0), (171.0, 403.0), (171.0, 414.0)),
            "little": ((162.0, 390.0), (160.0, 399.0), (160.0, 408.0)),
        },
        "contact_vectors": {
            "index": (1.20, 5.90),
            "middle": (0.70, 6.20),
        },
        "arc_sign": 1.0,
    },
    {
        "name": "right",
        "allowed_roi": (210, 366, 303, 440),
        "hand_contour": (
            (218, 390), (234, 386), (254, 383), (270, 389),
            (279, 397), (283, 408), (280, 417), (273, 422),
            (269, 429), (261, 431), (254, 426), (248, 430),
            (240, 429), (234, 424), (228, 423), (222, 417),
            (216, 409),
        ),
        "cuff_contour": (
            (242, 367), (272, 371), (290, 382), (297, 397),
            (291, 411), (281, 419), (279, 397), (270, 389),
            (254, 383), (234, 386),
        ),
        "wrist": (270.0, 393.0),
        "palm": (253.0, 402.0),
        "cuff_top": (270.0, 373.0),
        "cuff_bottom": (272.0, 389.0),
        "fingers": {
            "index": ((235.0, 400.0), (232.0, 412.0), (229.0, 425.0)),
            "middle": ((248.0, 402.0), (245.0, 415.0), (244.0, 429.0)),
            "ring": ((260.0, 403.0), (259.0, 415.0), (260.0, 427.0)),
            "little": ((271.0, 403.0), (273.0, 413.0), (271.0, 421.0)),
        },
        "contact_vectors": {
            "index": (-1.10, 6.00),
            "middle": (-0.70, 6.30),
        },
        "arc_sign": -1.0,
    },
)

# The 96-frame cycle lasts 1.6 seconds at the authored 60fps clock.  Eight
# deliberately unequal gaps avoid the old 12-frame metronome while preserving
# left/right alternation across the loop seam.
V5_TYPING_EVENTS = (
    (4.0, "left", "index", 1.00),
    (15.0, "right", "middle", 0.95),
    (27.0, "left", "middle", 1.08),
    (38.0, "right", "index", 0.96),
    (50.0, "left", "index", 0.99),
    (63.0, "right", "index", 1.02),
    (75.0, "left", "middle", 1.07),
    (87.0, "right", "middle", 1.01),
)
V5_NEUTRAL_SEAM_INDICES = (0, 10, 21, 33, 44, 56, 69, 81, 93)

# Preparation is a small lift, contact lasts two authored frames, and release
# approaches rest monotonically.  There is intentionally no post-release
# negative overshoot.
V5_TYPING_PRESS_CURVE = (
    (-4.0, 0.0),
    (-3.0, -0.18),
    (-2.0, 0.10),
    (-1.0, 0.70),
    (0.0, 1.0),
    (1.0, 0.97),
    (2.0, 0.72),
    (3.0, 0.42),
    (4.0, 0.16),
    (5.0, 0.0),
)
NEUTRAL_BROW_ERASE_REGIONS = (
    (174, 196, 201, 211),
    (262, 209, 290, 228),
)
NEUTRAL_MOUTH_ERASE_REGION = (204, 291, 232, 307)
BROW_REFERENCE_REGIONS = (
    (145, 194, 190, 217),
    (239, 194, 283, 219),
)
EXPRESSION_PATCH_REGIONS = (
    (150, 184, 205, 218),
    (240, 184, 294, 231),
    (202, 289, 234, 309),
)
SERIOUS_EYE_LOCK_REGIONS = (
    (158, 220, 207, 289),
    (245, 226, 302, 296),
)
NEUTRAL_BROW_PATHS = (
    (
        (181.0, 204.0),
        (184.0, 201.2),
        (191.0, 201.2),
        (195.0, 204.0),
    ),
    (
        (269.0, 217.0),
        (273.5, 214.8),
        (280.0, 218.0),
        (283.0, 221.0),
    ),
)
SERIOUS_BROW_PATHS = (
    (
        (158.0, 198.5),
        (166.0, 199.5),
        (176.0, 206.5),
        (184.0, 210.0),
    ),
    (
        (250.0, 211.0),
        (259.0, 207.0),
        (270.0, 201.0),
        (279.0, 199.5),
    ),
)
NEUTRAL_MOUTH_PATH = (
    (210.0, 297.0),
    (214.0, 301.5),
    (222.0, 302.0),
    (226.0, 297.0),
)
SERIOUS_MOUTH_PATH = (
    (209.0, 301.0),
    (214.0, 296.0),
    (221.0, 295.0),
    (227.0, 300.0),
)


def smoothstep(value: float) -> float:
    value = min(1.0, max(0.0, value))
    return value * value * (3.0 - 2.0 * value)


def clean_transparent_rgb(image: Image.Image) -> Image.Image:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    transparent = rgba[..., 3] == 0
    rgba[transparent, :3] = 0
    return Image.fromarray(rgba, "RGBA")


def resize_rgba_premultiplied(
    image: Image.Image,
    size: tuple[int, int],
) -> Image.Image:
    resized = (
        image.convert("RGBA")
        .convert("RGBa")
        .resize(size, Image.Resampling.LANCZOS)
        .convert("RGBA")
    )
    return clean_transparent_rgb(resized)


def visible_bbox(image: Image.Image, threshold: int = 8) -> tuple[int, int, int, int]:
    alpha = image.convert("RGBA").getchannel("A")
    bounds = alpha.point(lambda value: 255 if value >= threshold else 0).getbbox()
    if bounds is None:
        raise ValueError("Animation source contains no visible pixels")
    return bounds


def fit_source_to_runtime_canvas(
    image: Image.Image,
    *,
    maximum_size: tuple[int, int],
    bottom: int,
) -> Image.Image:
    source = image.convert("RGBA").crop(visible_bbox(image))
    scale = min(maximum_size[0] / source.width, maximum_size[1] / source.height)
    target_size = (
        max(1, round(source.width * scale)),
        max(1, round(source.height * scale)),
    )
    source = resize_rgba_premultiplied(source, target_size)
    canvas = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
    x = (CANVAS_SIZE[0] - source.width) // 2
    y = bottom - source.height
    if x < 5 or y < 5 or x + source.width > CANVAS_SIZE[0] - 5:
        raise ValueError(
            f"Work source does not fit the runtime safety margin: {(x, y, source.size)}"
        )
    canvas.alpha_composite(source, (x, y))
    return clean_transparent_rgb(canvas)


def load_work_neutral() -> Image.Image:
    with Image.open(WORK_ANCHOR_PATH) as opened:
        return fit_source_to_runtime_canvas(
            opened,
            maximum_size=WORK_MAX_SIZE,
            bottom=WORK_BOTTOM,
        )


def normalize_key_pose_to_neutral_bbox(
    path: Path,
    neutral: Image.Image,
) -> Image.Image:
    """Register an image-generated hand pose without trusting its outer canvas.

    The two v3 poses were deliberately redrawn, so whole-frame ECC would chase
    local brush differences in the hat and computer.  Their composition is the
    same as the approved neutral pose, however.  Cropping at the same alpha
    threshold and forcing the result into neutral's exact visible rectangle is
    deterministic, keeps both endpoint bboxes identical, and limits the tiny
    non-uniform correction to the source's one-pixel export discrepancy.
    """

    target_left, target_top, target_right, target_bottom = visible_bbox(neutral)
    with Image.open(path) as opened:
        source = opened.convert("RGBA").crop(visible_bbox(opened))
    source = resize_rgba_premultiplied(
        source,
        (target_right - target_left, target_bottom - target_top),
    )
    canvas = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
    canvas.alpha_composite(source, (target_left, target_top))
    return clean_transparent_rgb(canvas)


def load_serious_reference() -> Image.Image:
    with Image.open(SERIOUS_REFERENCE_PATH) as opened:
        return fit_source_to_runtime_canvas(
            opened,
            maximum_size=WORK_MAX_SIZE,
            bottom=WORK_BOTTOM,
        )


def load_idle() -> Image.Image:
    with Image.open(IDLE_PATH) as opened:
        idle = clean_transparent_rgb(opened)
    if idle.size != CANVAS_SIZE:
        raise ValueError(f"Idle frame must be {CANVAS_SIZE}, got {idle.size}")
    return idle


def region_support(
    regions: Iterable[tuple[float, float, float, float]],
) -> np.ndarray:
    yy, xx = np.mgrid[0 : CANVAS_SIZE[1], 0 : CANVAS_SIZE[0]]
    support = np.zeros((CANVAS_SIZE[1], CANVAS_SIZE[0]), dtype=bool)
    for center_x, center_y, radius_x, radius_y in regions:
        radius = ((xx - center_x) / radius_x) ** 2 + ((yy - center_y) / radius_y) ** 2
        support |= radius < 1.0
    return support


def rectangular_support(region: tuple[int, int, int, int]) -> np.ndarray:
    support = np.zeros((CANVAS_SIZE[1], CANVAS_SIZE[0]), dtype=bool)
    left, top, right, bottom = region
    support[top:bottom, left:right] = True
    return support


def warp_motion_regions(
    image: Image.Image,
    regions: tuple[tuple[float, float, float, float], ...],
    displacements: tuple[tuple[float, float], ...],
    *,
    allowed_support: np.ndarray | None = None,
) -> Image.Image:
    if len(regions) != len(displacements):
        raise ValueError("Every work motion region needs one displacement")
    if all(abs(dx) < 1e-12 and abs(dy) < 1e-12 for dx, dy in displacements):
        return image.copy()

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    height, width = rgba.shape[:2]
    yy, xx = np.mgrid[0:height, 0:width]
    displacement_x = np.zeros((height, width), dtype=np.float32)
    displacement_y = np.zeros((height, width), dtype=np.float32)
    support = np.zeros((height, width), dtype=bool)

    for region, (delta_x, delta_y) in zip(regions, displacements):
        center_x, center_y, radius_x, radius_y = region
        radial = ((xx - center_x) / radius_x) ** 2 + ((yy - center_y) / radius_y) ** 2
        inside = radial < 1.0
        weight = np.zeros_like(radial, dtype=np.float32)
        # Keep the complete hand silhouette in a nearly rigid inner island,
        # then feather the displacement to zero outside the wrist.  A simple
        # `(1-r)^2` falloff left the outline almost fixed and merely flexed the
        # interior shading, which did not read as an actual keyboard press.
        blend = np.clip((1.0 - radial[inside]) / 0.58, 0.0, 1.0)
        weight[inside] = blend * blend * (3.0 - 2.0 * blend)
        displacement_x += weight * delta_x
        displacement_y += weight * delta_y
        support |= inside & (weight > 1e-7)

    if allowed_support is not None:
        if allowed_support.shape != support.shape:
            raise ValueError("Finger support mask does not match the work canvas")
        support &= allowed_support

    source_x = np.clip(xx.astype(np.float32) - displacement_x, 0, width - 1)
    source_y = np.clip(yy.astype(np.float32) - displacement_y, 0, height - 1)
    x0 = np.floor(source_x).astype(np.int32)
    y0 = np.floor(source_y).astype(np.int32)
    x1 = np.minimum(x0 + 1, width - 1)
    y1 = np.minimum(y0 + 1, height - 1)
    fx = (source_x - x0)[..., None]
    fy = (source_y - y0)[..., None]

    alpha = rgba[..., 3:4].astype(np.float32) / 255.0
    premultiplied = np.concatenate(
        (rgba[..., :3].astype(np.float32) * alpha, rgba[..., 3:4].astype(np.float32)),
        axis=2,
    )

    top = premultiplied[y0, x0] * (1.0 - fx) + premultiplied[y0, x1] * fx
    bottom = premultiplied[y1, x0] * (1.0 - fx) + premultiplied[y1, x1] * fx
    sampled = top * (1.0 - fy) + bottom * fy

    output = rgba.copy()
    sampled_alpha = np.clip(np.rint(sampled[..., 3]), 0, 255).astype(np.uint8)
    sampled_rgb = np.zeros_like(sampled[..., :3], dtype=np.uint8)
    visible = sampled[..., 3] > 0.5
    sampled_rgb[visible] = np.clip(
        np.rint(sampled[..., :3][visible] * 255.0 / sampled[..., 3:4][visible]),
        0,
        255,
    ).astype(np.uint8)

    output[support, :3] = sampled_rgb[support]
    output[support, 3] = sampled_alpha[support]
    output[output[..., 3] == 0, :3] = 0
    return Image.fromarray(output, "RGBA")


def sample_serious_brow_colour(reference: Image.Image) -> tuple[int, int, int, int]:
    rgba = pixel_array(reference)
    samples: list[np.ndarray] = []
    for left, top, right, bottom in BROW_REFERENCE_REGIONS:
        crop = rgba[top:bottom, left:right]
        dark_brown = (
            (crop[..., 3] >= 220)
            & (crop[..., 0] >= 45)
            & (crop[..., 0] <= 175)
            & (crop[..., 1] <= 105)
            & (crop[..., 2] <= 90)
        )
        if dark_brown.any():
            samples.append(crop[dark_brown])
    if not samples:
        return 105, 43, 28, 255
    pixels = np.concatenate(samples, axis=0)
    median = np.median(pixels, axis=0).round().astype(np.uint8)
    return int(median[0]), int(median[1]), int(median[2]), 255


def neutral_expression_erase_mask(image: Image.Image) -> np.ndarray:
    rgba = pixel_array(image)
    erase_mask = np.zeros((CANVAS_SIZE[1], CANVAS_SIZE[0]), dtype=np.uint8)
    erase_regions = (*NEUTRAL_BROW_ERASE_REGIONS, NEUTRAL_MOUTH_ERASE_REGION)
    for left, top, right, bottom in erase_regions:
        crop = rgba[top:bottom, left:right]
        expression_pixels = (
            (crop[..., 3] >= 160)
            & (crop[..., 0] <= 205)
            & (crop[..., 1] <= 145)
            & (crop[..., 2] <= 120)
        )
        erase_mask[top:bottom, left:right][expression_pixels] = 255

    erase_mask = cv2.dilate(
        erase_mask,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)),
        iterations=1,
    )
    allowed = np.zeros_like(erase_mask)
    for left, top, right, bottom in erase_regions:
        allowed[top:bottom, left:right] = 255
    return cv2.bitwise_and(erase_mask, allowed)


def inpaint_neutral_expression(image: Image.Image) -> Image.Image:
    rgba = pixel_array(image).copy()
    erase_mask = neutral_expression_erase_mask(image)
    if not np.any(erase_mask):
        raise AssertionError("Neutral expression mask is empty")
    rgba[..., :3] = cv2.inpaint(
        rgba[..., :3],
        erase_mask,
        4.0,
        cv2.INPAINT_TELEA,
    )
    rgba[rgba[..., 3] == 0, :3] = 0
    return Image.fromarray(rgba, "RGBA")


def cubic_bezier_points(
    start: tuple[float, float],
    control_a: tuple[float, float],
    control_b: tuple[float, float],
    end: tuple[float, float],
    *,
    samples: int = 24,
) -> list[tuple[float, float]]:
    points: list[tuple[float, float]] = []
    for index in range(samples):
        value = index / (samples - 1)
        inverse = 1.0 - value
        x = (
            inverse**3 * start[0]
            + 3.0 * inverse**2 * value * control_a[0]
            + 3.0 * inverse * value**2 * control_b[0]
            + value**3 * end[0]
        )
        y = (
            inverse**3 * start[1]
            + 3.0 * inverse**2 * value * control_a[1]
            + 3.0 * inverse * value**2 * control_b[1]
            + value**3 * end[1]
        )
        points.append((x, y))
    return points


def interpolate_brow_path(
    neutral_path: tuple[tuple[float, float], ...],
    serious_path: tuple[tuple[float, float], ...],
    amount: float,
) -> tuple[tuple[float, float], ...]:
    return tuple(
        (
            first[0] + (second[0] - first[0]) * amount,
            first[1] + (second[1] - first[1]) * amount,
        )
        for first, second in zip(neutral_path, serious_path, strict=True)
    )


def draw_expression_details(
    clean_face: Image.Image,
    serious_reference: Image.Image,
    amount: float,
) -> Image.Image:
    amount = min(1.0, max(0.0, amount))
    antialias = 4
    layer = Image.new(
        "RGBA",
        (CANVAS_SIZE[0] * antialias, CANVAS_SIZE[1] * antialias),
        (0, 0, 0, 0),
    )
    draw = ImageDraw.Draw(layer)
    colour = sample_serious_brow_colour(serious_reference)
    for neutral_path, serious_path in zip(
        NEUTRAL_BROW_PATHS,
        SERIOUS_BROW_PATHS,
        strict=True,
    ):
        start, control_a, control_b, end = interpolate_brow_path(
            neutral_path,
            serious_path,
            amount,
        )
        path = cubic_bezier_points(start, control_a, control_b, end)
        draw.line(
            [(round(x * antialias), round(y * antialias)) for x, y in path],
            fill=colour,
            width=round((2.8 + 1.2 * amount) * antialias),
            joint="curve",
        )
    mouth_path = cubic_bezier_points(
        *interpolate_brow_path(
            NEUTRAL_MOUTH_PATH,
            SERIOUS_MOUTH_PATH,
            amount,
        )
    )
    draw.line(
        [(round(x * antialias), round(y * antialias)) for x, y in mouth_path],
        fill=colour,
        width=round((2.6 + 0.8 * amount) * antialias),
        joint="curve",
    )
    result = clean_face.copy()
    result.alpha_composite(resize_rgba_premultiplied(layer, CANVAS_SIZE))
    return clean_transparent_rgb(result)


def build_serious_neutral(
    neutral: Image.Image,
    serious_reference: Image.Image,
) -> Image.Image:
    return draw_expression_details(
        inpaint_neutral_expression(neutral),
        serious_reference,
        1.0,
    )


def build_expression_pose(
    neutral: Image.Image,
    clean_face: Image.Image,
    serious_reference: Image.Image,
    serious: Image.Image,
    amount: float,
) -> Image.Image:
    if amount <= 0.0:
        return neutral.copy()
    if amount >= 1.0:
        return serious.copy()
    # Draw exactly one interpolated brow per eye and one interpolated mouth on
    # a clean skin plate. Pixel cross-fading separated expressions creates the
    # obvious ghosting that older transitions exhibited.
    return draw_expression_details(clean_face, serious_reference, amount)


def premultiplied_array(image: Image.Image) -> np.ndarray:
    rgba = pixel_array(image).astype(np.float32)
    alpha = rgba[..., 3:4] / 255.0
    return np.concatenate((rgba[..., :3] * alpha, rgba[..., 3:4]), axis=2)


def image_from_premultiplied(array: np.ndarray) -> Image.Image:
    array = np.clip(array, 0.0, 255.0)
    alpha = array[..., 3:4]
    rgb = np.zeros_like(array[..., :3])
    visible = alpha[..., 0] > 0.5
    rgb[visible] = array[..., :3][visible] * 255.0 / alpha[visible]
    rgba = np.concatenate((rgb, alpha), axis=2)
    output = np.clip(np.rint(rgba), 0, 255).astype(np.uint8)
    output[output[..., 3] == 0, :3] = 0
    return Image.fromarray(output, "RGBA")


def blend_premultiplied(
    first: Image.Image,
    second: Image.Image,
    mask: np.ndarray,
) -> Image.Image:
    weight = np.clip(mask.astype(np.float32), 0.0, 1.0)[..., None]
    blended = (
        premultiplied_array(first) * (1.0 - weight)
        + premultiplied_array(second) * weight
    )
    return image_from_premultiplied(blended)


def rectangle_mask(region: tuple[int, int, int, int]) -> np.ndarray:
    result = np.zeros((CANVAS_SIZE[1], CANVAS_SIZE[0]), dtype=bool)
    left, top, right, bottom = region
    result[top:bottom, left:right] = True
    return result


def build_pose_replacement_mask(
    neutral: Image.Image,
    key_pose: Image.Image,
    *,
    search_roi: tuple[int, int, int, int],
    allowed_roi: tuple[int, int, int, int],
) -> tuple[np.ndarray, np.ndarray]:
    first = pixel_array(neutral)
    second = pixel_array(key_pose)
    channel_delta = np.max(
        np.abs(second.astype(np.int16) - first.astype(np.int16)),
        axis=2,
    )
    alpha_xor = (first[..., 3] >= 24) != (second[..., 3] >= 24)
    core = np.logical_or(channel_delta > 20, alpha_xor).astype(np.uint8)
    core &= rectangle_mask(search_roi).astype(np.uint8)
    core = cv2.morphologyEx(
        core,
        cv2.MORPH_OPEN,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)),
    )
    core = cv2.morphologyEx(
        core,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)),
    )

    component_count, labels, stats, _ = cv2.connectedComponentsWithStats(core, 8)
    selected = np.zeros_like(core)
    for label in range(1, component_count):
        if int(stats[label, cv2.CC_STAT_AREA]) >= 48:
            selected[labels == label] = 1
    if int(selected.sum()) < 150:
        raise AssertionError("Registered work key pose has no coherent finger change")

    allowed = rectangle_mask(allowed_roi)
    selected &= allowed.astype(np.uint8)
    selected[rectangle_mask(HEAD_LOCK_REGION)] = 0
    selected[rectangle_mask(COMPUTER_LOCK_REGION)] = 0
    body_lock = region_support((*SHOULDER_LOCK_REGIONS, TORSO_LOCK_REGION))
    selected[body_lock] = 0

    expanded = cv2.dilate(
        selected,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (21, 21)),
        iterations=1,
    ).astype(np.float32)
    feather = cv2.GaussianBlur(expanded, (0, 0), sigmaX=6.5, sigmaY=6.5)
    feather = np.clip(feather, 0.0, 1.0)
    feather[selected.astype(bool)] = 1.0
    feather[~allowed] = 0.0
    feather[rectangle_mask(HEAD_LOCK_REGION)] = 0.0
    feather[rectangle_mask(COMPUTER_LOCK_REGION)] = 0.0
    feather[body_lock] = 0.0
    return feather, selected.astype(bool)


def build_local_pose_model(
    neutral: Image.Image,
    key_pose: Image.Image,
    *,
    name: str,
    search_roi: tuple[int, int, int, int],
    allowed_roi: tuple[int, int, int, int],
) -> dict[str, object]:
    replacement_mask, change_core = build_pose_replacement_mask(
        neutral,
        key_pose,
        search_roi=search_roi,
        allowed_roi=allowed_roi,
    )
    target = blend_premultiplied(neutral, key_pose, replacement_mask)

    def flow_plate(image: Image.Image) -> np.ndarray:
        rgba = pixel_array(image).astype(np.float32)
        alpha = rgba[..., 3:4] / 255.0
        flattened = rgba[..., :3] * alpha + 250.0 * (1.0 - alpha)
        return cv2.cvtColor(
            np.clip(np.rint(flattened), 0, 255).astype(np.uint8),
            cv2.COLOR_RGB2GRAY,
        )

    neutral_plate = flow_plate(neutral)
    target_plate = flow_plate(target)
    forward_flow = cv2.calcOpticalFlowFarneback(
        neutral_plate,
        target_plate,
        None,
        0.5,
        4,
        21,
        5,
        7,
        1.5,
        0,
    )
    backward_flow = cv2.calcOpticalFlowFarneback(
        target_plate,
        neutral_plate,
        None,
        0.5,
        4,
        21,
        5,
        7,
        1.5,
        0,
    )
    flow_support = cv2.GaussianBlur(
        rectangle_mask(allowed_roi).astype(np.float32),
        (0, 0),
        sigmaX=2.0,
        sigmaY=2.0,
    )[..., None]
    forward_flow *= flow_support
    backward_flow *= flow_support
    return {
        "name": name,
        "neutral": neutral.copy(),
        "target": target,
        "replacement_mask": replacement_mask,
        "change_core": change_core,
        "allowed_roi": allowed_roi,
        "forward_flow": forward_flow,
        "backward_flow": backward_flow,
    }


def polygon_support(points: tuple[tuple[int, int], ...]) -> np.ndarray:
    mask = np.zeros((CANVAS_SIZE[1], CANVAS_SIZE[0]), dtype=np.uint8)
    cv2.fillPoly(mask, [np.asarray(points, dtype=np.int32)], 1)
    return mask.astype(bool)


def build_finger_pose_model(
    neutral: Image.Image,
    rig: dict[str, object],
) -> dict[str, object]:
    name = rig.get("name")
    points = rig.get("polygon")
    regions = rig.get("regions")
    deltas = rig.get("deltas")
    allowed_roi = rig.get("allowed_roi")
    if (
        not isinstance(name, str)
        or not isinstance(points, tuple)
        or not isinstance(regions, tuple)
        or not isinstance(deltas, tuple)
        or not isinstance(allowed_roi, tuple)
    ):
        raise TypeError("Invalid finger rig")
    support = polygon_support(points)
    target = warp_motion_regions(
        neutral,
        regions,
        deltas,
        allowed_support=support,
    )
    changed = np.any(pixel_array(target) != pixel_array(neutral), axis=2)
    if int(changed.sum()) < 40:
        raise AssertionError(f"{name} finger rig produces too little visible motion")
    maximum_tip_displacement = max(math.hypot(dx, dy) for dx, dy in deltas)
    return {
        "kind": "finger-rig",
        "name": name,
        "neutral": neutral.copy(),
        "target": target,
        "replacement_mask": support.astype(np.float32),
        "change_core": changed,
        "allowed_roi": allowed_roi,
        "motion_regions": regions,
        "motion_deltas": deltas,
        "maximum_tip_displacement_px": maximum_tip_displacement,
    }


def morph_local_pose(model: dict[str, object], amount: float) -> Image.Image:
    neutral = model["neutral"]
    target = model["target"]
    if not isinstance(neutral, Image.Image) or not isinstance(target, Image.Image):
        raise TypeError("Invalid local pose model")
    if abs(amount) <= 1e-8:
        return neutral.copy()
    if model.get("kind") == "finger-rig":
        regions = model.get("motion_regions")
        deltas = model.get("motion_deltas")
        replacement_mask = model.get("replacement_mask")
        if (
            not isinstance(regions, tuple)
            or not isinstance(deltas, tuple)
            or not isinstance(replacement_mask, np.ndarray)
        ):
            raise TypeError("Invalid finger rig articulation buffers")
        return warp_motion_regions(
            neutral,
            regions,
            tuple((dx * amount, dy * amount) for dx, dy in deltas),
            allowed_support=replacement_mask >= 0.5,
        )
    amount = min(1.0, amount)

    replacement_mask = model["replacement_mask"]
    forward_flow = model["forward_flow"]
    backward_flow = model["backward_flow"]
    if (
        not isinstance(replacement_mask, np.ndarray)
        or not isinstance(forward_flow, np.ndarray)
        or not isinstance(backward_flow, np.ndarray)
    ):
        raise TypeError("Invalid local pose articulation buffers")

    neutral_array = premultiplied_array(neutral)
    height, width = neutral_array.shape[:2]
    yy, xx = np.mgrid[0:height, 0:width].astype(np.float32)

    def remap(array: np.ndarray, flow: np.ndarray, progress: float) -> np.ndarray:
        map_x = xx - flow[..., 0] * progress
        map_y = yy - flow[..., 1] * progress
        return cv2.remap(
            array,
            map_x,
            map_y,
            cv2.INTER_CUBIC,
            borderMode=cv2.BORDER_REPLICATE,
        )

    if amount < 0.0:
        # A very small inverse extrapolation creates the anticipatory finger
        # lift.  The palm and wrist remain inside the same compact mask.
        mixed = remap(neutral_array, forward_flow, amount)
    else:
        target_array = premultiplied_array(target)
        from_neutral = remap(neutral_array, forward_flow, amount)
        from_target = remap(target_array, backward_flow, 1.0 - amount)
        mixed = from_neutral * (1.0 - amount) + from_target * amount

    candidate_image = image_from_premultiplied(mixed)
    motion_mask = np.clip(replacement_mask * 1.08, 0.0, 1.0)
    result = blend_premultiplied(neutral, candidate_image, motion_mask)
    result_array = pixel_array(result).copy()
    neutral_pixels = pixel_array(neutral)
    for region in (HEAD_LOCK_REGION, COMPUTER_LOCK_REGION):
        region_mask = rectangle_mask(region)
        result_array[region_mask] = neutral_pixels[region_mask]
    body_lock_mask = region_support((*SHOULDER_LOCK_REGIONS, TORSO_LOCK_REGION))
    result_array[body_lock_mask] = neutral_pixels[body_lock_mask]
    return clean_transparent_rgb(Image.fromarray(result_array, "RGBA"))


def apply_expression_pose(
    pose: Image.Image,
    expression: Image.Image,
) -> Image.Image:
    output = pixel_array(pose).copy()
    source = pixel_array(expression)
    for left, top, right, bottom in EXPRESSION_PATCH_REGIONS:
        output[top:bottom, left:right] = source[top:bottom, left:right]
    return Image.fromarray(output, "RGBA")


TYPING_EVENT_CENTERS = (3.0, 15.0, 27.0, 39.0)
TYPING_PRESS_CURVE = (
    (-7.0, 0.0),
    (-6.0, -0.12),
    (-4.0, -0.50),
    (-3.0, -0.25),
    (-2.0, 0.20),
    (-1.0, 0.82),
    (0.0, 1.0),
    (1.0, 0.95),
    (2.0, 0.55),
    (3.0, 0.18),
    (4.0, -0.15),
    (5.0, -0.06),
    (7.0, 0.0),
)


def typing_press_amount(frame_position: float, center: float) -> float:
    relative = (frame_position - center + LOOP_FRAME_COUNT / 2.0) % LOOP_FRAME_COUNT
    relative -= LOOP_FRAME_COUNT / 2.0
    if relative <= TYPING_PRESS_CURVE[0][0] or relative >= TYPING_PRESS_CURVE[-1][0]:
        return 0.0
    for (first_x, first_y), (second_x, second_y) in zip(
        TYPING_PRESS_CURVE,
        TYPING_PRESS_CURVE[1:],
        strict=True,
    ):
        if first_x <= relative <= second_x:
            progress = smoothstep((relative - first_x) / (second_x - first_x))
            return first_y + (second_y - first_y) * progress
    return 0.0


def render_work_pose(
    expression: Image.Image,
    pose_models: tuple[dict[str, object], ...],
    *,
    typing_frame_position: float,
) -> Image.Image:
    if len(pose_models) != len(TYPING_EVENT_CENTERS):
        raise ValueError("Typing rig requires four alternating finger models")
    neutral = pose_models[0]["neutral"]
    if not isinstance(neutral, Image.Image):
        raise TypeError("Invalid local pose neutral")
    pose = neutral.copy()
    for model, center in zip(pose_models, TYPING_EVENT_CENTERS, strict=True):
        amount = typing_press_amount(typing_frame_position, center)
        if abs(amount) <= 1e-8:
            continue
        articulated = morph_local_pose(model, amount)
        replacement_mask = model["replacement_mask"]
        if not isinstance(replacement_mask, np.ndarray):
            raise TypeError("Invalid local pose replacement mask")
        pose = blend_premultiplied(pose, articulated, replacement_mask)
    return apply_expression_pose(clean_transparent_rgb(pose), expression)


def build_work_loop(
    expression: Image.Image,
    pose_models: tuple[dict[str, object], ...],
) -> list[Image.Image]:
    return [
        render_work_pose(
            expression,
            pose_models,
            typing_frame_position=float(frame_index),
        )
        for frame_index in range(LOOP_FRAME_COUNT)
    ]


def typing_press_amount_v5(frame_position: float, center: float) -> float:
    relative = (frame_position - center + LOOP_FRAME_COUNT / 2.0) % LOOP_FRAME_COUNT
    relative -= LOOP_FRAME_COUNT / 2.0
    if (
        relative <= V5_TYPING_PRESS_CURVE[0][0]
        or relative >= V5_TYPING_PRESS_CURVE[-1][0]
    ):
        return 0.0
    for (first_x, first_y), (second_x, second_y) in zip(
        V5_TYPING_PRESS_CURVE,
        V5_TYPING_PRESS_CURVE[1:],
        strict=True,
    ):
        if first_x <= relative <= second_x:
            progress = smoothstep((relative - first_x) / (second_x - first_x))
            return first_y + (second_y - first_y) * progress
    return 0.0


def _v5_polygon_mask(
    polygons: Iterable[tuple[tuple[int, int], ...]],
) -> np.ndarray:
    mask = np.zeros((CANVAS_SIZE[1], CANVAS_SIZE[0]), dtype=np.uint8)
    for polygon in polygons:
        cv2.fillPoly(mask, [np.asarray(polygon, dtype=np.int32)], 255)
    return mask


def build_v5_hand_model(
    neutral: Image.Image,
    underlay: Image.Image,
    rig: dict[str, object],
) -> dict[str, object]:
    name = rig.get("name")
    allowed_roi = rig.get("allowed_roi")
    hand_contour = rig.get("hand_contour")
    cuff_contour = rig.get("cuff_contour")
    fingers = rig.get("fingers")
    if (
        not isinstance(name, str)
        or not isinstance(allowed_roi, tuple)
        or not isinstance(hand_contour, tuple)
        or not isinstance(cuff_contour, tuple)
        or not isinstance(fingers, dict)
    ):
        raise TypeError("Invalid v5 articulated-hand rig")

    allowed = rectangular_support(allowed_roi)
    hand_support = _v5_polygon_mask((hand_contour,)) > 0
    cuff_support = _v5_polygon_mask((cuff_contour,)) > 0
    neutral_array = pixel_array(neutral)
    underlay_array = pixel_array(underlay)
    red = neutral_array[..., 0].astype(np.int16)
    green = neutral_array[..., 1].astype(np.int16)
    blue = neutral_array[..., 2].astype(np.int16)
    alpha = neutral_array[..., 3]
    source_delta = np.max(
        np.abs(neutral_array.astype(np.int16) - underlay_array.astype(np.int16)),
        axis=2,
    )

    # Start from actual skin-coloured connected components, not a filled hand
    # polygon.  The latter also captures the cream keyboard between separated
    # fingers and makes a whole key row wobble when the hand mesh bends.
    skin_candidates = (
        (alpha > 0)
        & (red > 155)
        & ((red - green) > 10)
        & ((green - blue) > 2)
        & (green > 88)
        & (blue < 240)
        & hand_support
    )
    component_count, labels, statistics, _ = cv2.connectedComponentsWithStats(
        skin_candidates.astype(np.uint8),
        connectivity=8,
    )
    skin_core = np.zeros_like(skin_candidates)
    for component_index in range(1, component_count):
        if int(statistics[component_index, cv2.CC_STAT_AREA]) >= 6:
            skin_core |= labels == component_index
    if int(skin_core.sum()) < 900:
        raise AssertionError(f"{name} skin matte is unexpectedly small")

    # Recover only the antialiased skin edge and the dark brown hand outline.
    # A two-pixel semantic halo is narrow enough to preserve every keyboard
    # gap while keeping the complete moving fingertip contour intact.
    near_skin = cv2.dilate(
        skin_core.astype(np.uint8),
        np.ones((5, 5), dtype=np.uint8),
        iterations=1,
    ) > 0
    hand_outline = (red < 150) & (green < 108) & (blue < 96)
    hand_fringe = (source_delta >= 7) & (alpha > 0)
    hand_mask = skin_core | (
        near_skin
        & hand_support
        & (hand_outline | hand_fringe)
    )

    # The cuff is a separate narrow semantic band.  Select the purple fabric
    # nearest the hand, then recover its own antialiased outline.  In
    # particular, this avoids pulling the upper sleeve/shoulder with a key press.
    distance_from_hand = cv2.distanceTransform(
        (~skin_core).astype(np.uint8),
        cv2.DIST_L2,
        5,
    )
    purple_core = (
        (alpha > 0)
        & ((blue - red) > 14)
        & ((blue - green) > 18)
        & cuff_support
        & (distance_from_hand <= 19.0)
    )
    near_cuff = cv2.dilate(
        purple_core.astype(np.uint8),
        np.ones((5, 5), dtype=np.uint8),
        iterations=1,
    ) > 0
    purple_outline = ((blue - red) > 5) & ((blue - green) > 8)
    cuff_mask = purple_core | (
        near_cuff
        & cuff_support
        & (alpha > 0)
        & (purple_outline | (source_delta >= 9))
    )
    cuff_mask &= ~hand_mask

    mask = np.where((hand_mask | cuff_mask) & allowed, 255, 0).astype(np.uint8)
    if int((mask > 0).sum()) < 1200:
        raise AssertionError(f"{name} hand layer is unexpectedly small")

    source = pixel_array(neutral).copy()
    source[..., 3] = np.where(mask > 0, source[..., 3], 0)
    source[mask == 0, :3] = 0
    layer = clean_transparent_rgb(Image.fromarray(source, "RGBA"))

    controls: dict[str, tuple[float, float]] = {
        "wrist": tuple(rig["wrist"]),
        "palm": tuple(rig["palm"]),
        "cuff-top": tuple(rig["cuff_top"]),
        "cuff-bottom": tuple(rig["cuff_bottom"]),
    }
    for finger_name, joints in fingers.items():
        if not isinstance(finger_name, str) or not isinstance(joints, tuple):
            raise TypeError("Invalid v5 finger control points")
        for joint_index, point in enumerate(joints):
            controls[f"{finger_name}-{joint_index}"] = tuple(point)

    left, top, right, bottom = allowed_roi
    anchor_points = (
        (left, top), ((left + right) / 2.0, top), (right - 1, top),
        (right - 1, (top + bottom) / 2.0), (right - 1, bottom - 1),
        ((left + right) / 2.0, bottom - 1), (left, bottom - 1),
        (left, (top + bottom) / 2.0),
    )
    for index, point in enumerate(anchor_points):
        controls[f"anchor-{index}"] = (float(point[0]), float(point[1]))

    crop_margin = 10
    crop_box = (
        max(0, left - crop_margin),
        max(0, top - crop_margin),
        min(CANVAS_SIZE[0], right + crop_margin),
        min(CANVAS_SIZE[1], bottom + crop_margin),
    )
    return {
        "kind": "v5-articulated-hand",
        "name": name,
        "allowed_roi": allowed_roi,
        "mask": mask,
        "hand_mask": hand_mask & allowed,
        "cuff_mask": cuff_mask & allowed,
        "skin_core": skin_core & allowed,
        "layer": layer,
        "controls": controls,
        "fingers": fingers,
        "contact_vectors": rig["contact_vectors"],
        "arc_sign": float(rig["arc_sign"]),
        "crop_box": crop_box,
    }


def _v5_add_displacement(
    displacements: dict[str, np.ndarray],
    name: str,
    vector: np.ndarray,
    scale: float,
) -> None:
    displacements[name] += vector * scale


def _v5_tip_displacement(
    model: dict[str, object],
    finger_name: str,
    amount: float,
) -> np.ndarray:
    vectors = model.get("contact_vectors")
    if not isinstance(vectors, dict):
        raise TypeError("Missing v5 hand contact vectors")
    contact = np.asarray(vectors[finger_name], dtype=np.float64)
    arc_sign = float(model.get("arc_sign", 1.0))
    if amount < 0.0:
        lift = min(1.0, -amount / 0.25)
        return np.asarray((-0.55 * arc_sign * lift, -1.45 * lift))

    clamped = min(1.0, amount)
    arc = 4.0 * clamped * (1.0 - clamped)
    return contact * clamped + np.asarray(
        (0.72 * arc_sign * arc, -0.32 * arc),
        dtype=np.float64,
    )


def v5_hand_displacements_at(
    model: dict[str, object],
    frame_position: float,
) -> dict[str, np.ndarray]:
    controls = model.get("controls")
    fingers = model.get("fingers")
    side = model.get("name")
    if not isinstance(controls, dict) or not isinstance(fingers, dict) or not isinstance(side, str):
        raise TypeError("Invalid articulated hand model")
    displacements = {
        name: np.zeros(2, dtype=np.float64)
        for name in controls
    }

    for center, event_side, active_finger, strength in V5_TYPING_EVENTS:
        if event_side != side:
            continue
        amount = typing_press_amount_v5(frame_position, center)
        if abs(amount) <= 1e-9:
            continue
        tip = _v5_tip_displacement(model, active_finger, amount) * strength

        # The cuff remains attached to the sleeve while the wrist, palm and
        # active finger progressively inherit more of the contact arc.
        _v5_add_displacement(displacements, "cuff-bottom", tip, 0.08)
        _v5_add_displacement(displacements, "wrist", tip, 0.14)
        _v5_add_displacement(displacements, "palm", tip, 0.22)
        for joint_index, scale in enumerate((0.30, 0.65, 1.0)):
            _v5_add_displacement(
                displacements,
                f"{active_finger}-{joint_index}",
                tip,
                scale,
            )

        for finger_name in fingers:
            if finger_name == active_finger:
                continue
            follow = 0.22 if finger_name in ("index", "middle") else 0.16
            for joint_index, scale in enumerate((0.60, 0.82, 1.0)):
                _v5_add_displacement(
                    displacements,
                    f"{finger_name}-{joint_index}",
                    tip,
                    follow * scale,
                )
    return displacements


def warp_v5_hand_layer(
    model: dict[str, object],
    displacements: dict[str, np.ndarray],
    source_override: Image.Image | None = None,
) -> Image.Image:
    layer = source_override if source_override is not None else model.get("layer")
    controls = model.get("controls")
    crop_box = model.get("crop_box")
    if (
        not isinstance(layer, Image.Image)
        or not isinstance(controls, dict)
        or not isinstance(crop_box, tuple)
    ):
        raise TypeError("Invalid v5 hand warp buffers")
    if max(np.linalg.norm(value) for value in displacements.values()) <= 1e-9:
        return layer.copy()

    left, top, right, bottom = crop_box
    source = premultiplied_array(layer)[top:bottom, left:right]
    height, width = source.shape[:2]
    yy, xx = np.mgrid[top:bottom, left:right].astype(np.float32)
    numerator_x = np.zeros((height, width), dtype=np.float64)
    numerator_y = np.zeros((height, width), dtype=np.float64)
    denominator = np.zeros((height, width), dtype=np.float64)
    for control_name, point in controls.items():
        displacement = displacements[control_name]
        target_x = float(point[0]) + float(displacement[0])
        target_y = float(point[1]) + float(displacement[1])
        distance_squared = (xx - target_x) ** 2 + (yy - target_y) ** 2
        weight = 1.0 / np.power(distance_squared + 0.01, 1.32)
        numerator_x += weight * float(displacement[0])
        numerator_y += weight * float(displacement[1])
        denominator += weight
    displacement_x = numerator_x / np.maximum(denominator, 1e-12)
    displacement_y = numerator_y / np.maximum(denominator, 1e-12)
    map_x = (xx - displacement_x - left).astype(np.float32)
    map_y = (yy - displacement_y - top).astype(np.float32)
    warped = cv2.remap(
        source,
        map_x,
        map_y,
        cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0, 0),
    )
    crop = image_from_premultiplied(warped)
    output = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
    output.alpha_composite(crop, (left, top))
    return clean_transparent_rgb(output)


def prepare_v5_hand_base(
    expression: Image.Image,
    underlay: Image.Image,
    hand_models: tuple[dict[str, object], ...],
) -> Image.Image:
    output = pixel_array(expression).copy()
    replacement = pixel_array(underlay)
    for model in hand_models:
        mask = model.get("mask")
        if not isinstance(mask, np.ndarray):
            raise TypeError("Invalid v5 hand-layer mask")
        selected = mask > 0
        output[selected] = replacement[selected]
    return clean_transparent_rgb(Image.fromarray(output, "RGBA"))


def render_work_pose_v5(
    expression: Image.Image,
    underlay: Image.Image,
    hand_models: tuple[dict[str, object], ...],
    *,
    typing_frame_position: float,
) -> Image.Image:
    pose = prepare_v5_hand_base(expression, underlay, hand_models)
    for model in hand_models:
        displacements = v5_hand_displacements_at(model, typing_frame_position)
        articulated = warp_v5_hand_layer(model, displacements)
        pose.alpha_composite(articulated)
    return clean_transparent_rgb(pose)


def build_work_loop_v5(
    expression: Image.Image,
    underlay: Image.Image,
    hand_models: tuple[dict[str, object], ...],
) -> list[Image.Image]:
    return [
        render_work_pose_v5(
            expression,
            underlay,
            hand_models,
            typing_frame_position=float(frame_index),
        )
        for frame_index in range(LOOP_FRAME_COUNT)
    ]


def remove_isolated_temporal_shimmer(
    frames: list[Image.Image],
    *,
    wrap: bool,
    protected_indices: Iterable[int] = (),
) -> list[Image.Image]:
    """Remove one-frame high-contrast raster specks without blurring motion.

    The articulated contour is subpixel-positioned.  At a few outline pixels,
    bilinear sampling can otherwise choose a dark texel for exactly one frame
    even though the frames on both sides agree.  Only that temporal impulse is
    replaced; ordinary edge travel and all declared neutral seams stay exact.
    """

    if len(frames) < 3:
        return [frame.copy() for frame in frames]
    protected = set(protected_indices)
    premultiplied = [
        premultiplied_array(frame).astype(np.int16)
        for frame in frames
    ]
    outputs = [pixel_array(frame).copy() for frame in frames]
    first_index = 0 if wrap else 1
    last_index = len(frames) if wrap else len(frames) - 1
    for frame_index in range(first_index, last_index):
        if frame_index in protected:
            continue
        previous_index = (frame_index - 1) % len(frames)
        next_index = (frame_index + 1) % len(frames)
        previous = premultiplied[previous_index]
        current = premultiplied[frame_index]
        following = premultiplied[next_index]
        impulse = (
            (np.max(np.abs(current - previous), axis=2) > 48)
            & (np.max(np.abs(current - following), axis=2) > 48)
            & (np.max(np.abs(previous - following), axis=2) < 8)
        )
        if not impulse.any():
            continue
        averaged = ((previous + following + 1) // 2).astype(np.uint8)
        averaged_alpha = averaged[..., 3].astype(np.uint16)
        replacement = np.zeros_like(averaged)
        replacement[..., 3] = averaged[..., 3]
        visible = averaged_alpha > 0
        for channel in range(3):
            values = averaged[..., channel].astype(np.uint16)
            replacement[..., channel][visible] = np.minimum(
                255,
                (values[visible] * 255 + averaged_alpha[visible] // 2)
                // averaged_alpha[visible],
            ).astype(np.uint8)
        outputs[frame_index][impulse] = replacement[impulse]
    return [
        clean_transparent_rgb(Image.fromarray(frame, "RGBA"))
        for frame in outputs
    ]


def star_points(
    center: tuple[float, float],
    outer_radius: float,
    angle: float,
) -> list[tuple[float, float]]:
    points: list[tuple[float, float]] = []
    for point_index in range(10):
        radius = outer_radius if point_index % 2 == 0 else outer_radius * 0.43
        theta = angle - math.pi / 2.0 + point_index * math.pi / 5.0
        points.append(
            (center[0] + math.cos(theta) * radius, center[1] + math.sin(theta) * radius)
        )
    return points


def draw_cloud(progress: float, frame_index: int) -> Image.Image:
    if progress <= 0.0:
        return Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))

    antialias = 4
    layer = Image.new(
        "RGBA",
        (CANVAS_SIZE[0] * antialias, CANVAS_SIZE[1] * antialias),
        (0, 0, 0, 0),
    )
    draw = ImageDraw.Draw(layer)
    origin = (225.0, 320.0)
    puffs = (
        (52.0, 245.0, 122.0),
        (132.0, 148.0, 126.0),
        (246.0, 118.0, 142.0),
        (360.0, 168.0, 130.0),
        (416.0, 278.0, 112.0),
        (390.0, 405.0, 128.0),
        (308.0, 482.0, 136.0),
        (174.0, 492.0, 138.0),
        (60.0, 420.0, 128.0),
        (208.0, 320.0, 202.0),
    )

    def ellipse_box(cx: float, cy: float, radius: float) -> tuple[int, int, int, int]:
        return (
            round((cx - radius) * antialias),
            round((cy - radius) * antialias),
            round((cx + radius) * antialias),
            round((cy + radius) * antialias),
        )

    outer_colour = (246, 157, 111, 255)
    inner_colour = (255, 246, 232, 255)
    for target_x, target_y, target_radius in puffs:
        center_x = origin[0] + (target_x - origin[0]) * progress
        center_y = origin[1] + (target_y - origin[1]) * progress
        radius = target_radius * progress
        if radius < 0.75:
            continue
        draw.ellipse(ellipse_box(center_x, center_y, radius + 5.0), fill=outer_colour)
    for target_x, target_y, target_radius in puffs:
        center_x = origin[0] + (target_x - origin[0]) * progress
        center_y = origin[1] + (target_y - origin[1]) * progress
        radius = max(0.0, target_radius * progress - 1.5)
        if radius < 0.75:
            continue
        draw.ellipse(ellipse_box(center_x, center_y, radius), fill=inner_colour)

    if progress >= 0.38:
        decoration_scale = min(1.0, (progress - 0.38) / 0.30)
        rotation = frame_index * 0.17
        for center, radius, colour, offset in (
            ((103.0, 276.0), 11.0, (255, 151, 91, 255), 0.0),
            ((348.0, 303.0), 9.0, (246, 111, 101, 255), 0.45),
        ):
            scaled_points = star_points(
                (center[0] * antialias, center[1] * antialias),
                radius * decoration_scale * antialias,
                rotation + offset,
            )
            draw.polygon(scaled_points, fill=colour)

    return resize_rgba_premultiplied(layer, CANVAS_SIZE)


def cloud_progress(frame_index: int) -> float:
    if frame_index <= 23:
        return smoothstep(frame_index / 23.0)
    return smoothstep((47.0 - frame_index) / 23.0)


def build_work_enter(idle: Image.Image, neutral: Image.Image) -> list[Image.Image]:
    frames: list[Image.Image] = []
    for frame_index in range(ENTER_FRAME_COUNT):
        base = idle.copy() if frame_index <= 23 else neutral.copy()
        cloud = draw_cloud(cloud_progress(frame_index), frame_index)
        base.alpha_composite(cloud)
        frames.append(clean_transparent_rgb(base))
    return frames


def serious_exit_frame_position(value: float) -> float:
    # Keep the fast 2x cadence for one authored cycle while the serious brows
    # relax. Both endpoints share loop frame zero, preventing a hand-position pop.
    return LOOP_FRAME_COUNT * value


def build_serious_exit(
    neutral: Image.Image,
    clean_face: Image.Image,
    serious_reference: Image.Image,
    serious_neutral: Image.Image,
    pose_models: tuple[dict[str, object], ...],
) -> list[Image.Image]:
    frames: list[Image.Image] = []
    for frame_index in range(SERIOUS_EXIT_FRAME_COUNT):
        value = frame_index / (SERIOUS_EXIT_FRAME_COUNT - 1)
        relaxed = smoothstep(value)
        expression = build_expression_pose(
            neutral,
            clean_face,
            serious_reference,
            serious_neutral,
            1.0 - relaxed,
        )
        frame = render_work_pose(
            expression,
            pose_models,
            typing_frame_position=serious_exit_frame_position(value),
        )
        frames.append(frame)
    return frames


def build_serious_exit_v5(
    neutral: Image.Image,
    underlay: Image.Image,
    clean_face: Image.Image,
    serious_reference: Image.Image,
    serious_neutral: Image.Image,
    hand_models: tuple[dict[str, object], ...],
) -> list[Image.Image]:
    frames: list[Image.Image] = []
    for frame_index in range(SERIOUS_EXIT_FRAME_COUNT):
        value = frame_index / (SERIOUS_EXIT_FRAME_COUNT - 1)
        relaxed = smoothstep(value)
        expression = build_expression_pose(
            neutral,
            clean_face,
            serious_reference,
            serious_neutral,
            1.0 - relaxed,
        )
        # Both typing loops share geometry at frame zero.  Relax the serious
        # expression over that exact hand pose instead of squeezing a complete
        # 96-frame cycle into 24 frames, which would create a visible 4x burst.
        frame = render_work_pose_v5(
            expression,
            underlay,
            hand_models,
            typing_frame_position=0.0,
        )
        frames.append(frame)
    return frames


def save_png_atomically(image: Image.Image, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(f".{destination.stem}.tmp.png")
    image.save(temporary, format="PNG", optimize=True)
    temporary.replace(destination)


def write_sequence(prefix: str, frames: Iterable[Image.Image]) -> list[Path]:
    paths: list[Path] = []
    for frame_number, frame in enumerate(frames, start=1):
        destination = ASSETS / f"luban-work-{prefix}-{frame_number:03d}.png"
        save_png_atomically(frame, destination)
        paths.append(destination)
    keep = {path.resolve() for path in paths}
    for stale in ASSETS.glob(f"luban-work-{prefix}-*.png"):
        if stale.resolve() not in keep:
            stale.unlink()
    return paths


def contact_background(size: tuple[int, int]) -> Image.Image:
    background = Image.new("RGBA", size, (248, 250, 255, 255))
    draw = ImageDraw.Draw(background)
    square = 18
    for top in range(0, size[1], square):
        for left in range(0, size[0], square):
            if (left // square + top // square) % 2 == 0:
                draw.rectangle(
                    (left, top, left + square - 1, top + square - 1),
                    fill=(242, 246, 253, 255),
                )
    return background


def write_arm_motion_contact(
    loop_frames: list[Image.Image],
    serious_loop_frames: list[Image.Image],
) -> None:
    selected = list(range(LOOP_FRAME_COUNT))
    columns = 16
    crop_box = (135, 340, 310, 455)
    cell_size = (210, 138)
    label_height = 20
    rows_per_sequence = math.ceil(len(selected) / columns)
    sheet = Image.new(
        "RGBA",
        (
            columns * cell_size[0],
            2 * rows_per_sequence * (cell_size[1] + label_height),
        ),
        (248, 250, 255, 255),
    )
    draw = ImageDraw.Draw(sheet)
    for sequence_index, (name, frames) in enumerate(
        (("normal 96-frame cycle", loop_frames), ("serious 96-frame cycle", serious_loop_frames))
    ):
        sequence_row = sequence_index * rows_per_sequence
        for selected_index, frame_index in enumerate(selected):
            column = selected_index % columns
            row = sequence_row + selected_index // columns
            left = column * cell_size[0]
            top = row * (cell_size[1] + label_height)
            cell = contact_background(cell_size)
            crop = frames[frame_index].crop(crop_box).resize(
                cell_size,
                Image.Resampling.LANCZOS,
            )
            cell.alpha_composite(crop)
            sheet.alpha_composite(cell, (left, top + label_height))
            draw.text(
                (left + 4, top + 4),
                f"{name}  frame {frame_index + 1:03d}",
                fill=(69, 83, 112, 255),
            )
    save_png_atomically(sheet, ARM_CONTACT_PATH)


def write_face_transition_contact(
    serious_exit_frames: list[Image.Image],
) -> None:
    # Runtime enters the serious expression by sampling the authored exit
    # frames in reverse. Keep this sheet aligned with that exact eight-frame
    # contract and include the complete eyes, brows, and mouth region.
    serious_enter_frames = [
        serious_exit_frames[index]
        for index in SERIOUS_ENTER_SOURCE_FRAME_INDICES
    ]
    selections = (
        (
            "serious enter",
            serious_enter_frames,
            tuple(range(len(serious_enter_frames))),
        ),
        (
            "serious exit",
            serious_exit_frames,
            (0, 3, 7, 10, 13, 16, 20, 23),
        ),
    )
    crop_box = (140, 170, 310, 320)
    cell_size = (340, 300)
    label_height = 24
    sheet = Image.new(
        "RGBA",
        (
            len(selections[0][2]) * cell_size[0],
            len(selections) * (cell_size[1] + label_height),
        ),
        (248, 250, 255, 255),
    )
    draw = ImageDraw.Draw(sheet)
    for row, (name, frames, indices) in enumerate(selections):
        for column, frame_index in enumerate(indices):
            left = column * cell_size[0]
            top = row * (cell_size[1] + label_height)
            cell = contact_background(cell_size)
            crop = frames[frame_index].crop(crop_box).resize(
                cell_size,
                Image.Resampling.LANCZOS,
            )
            cell.alpha_composite(crop)
            sheet.alpha_composite(cell, (left, top + label_height))
            draw.text(
                (left + 5, top + 5),
                f"{name}  frame {frame_index + 1:03d}",
                fill=(69, 83, 112, 255),
            )
    save_png_atomically(sheet, FACE_CONTACT_PATH)


def pixel_array(image: Image.Image) -> np.ndarray:
    return np.asarray(image.convert("RGBA"), dtype=np.uint8)


def frame_digest(frame: np.ndarray) -> str:
    return hashlib.sha256(frame.tobytes()).hexdigest()


def alpha_iou(first: np.ndarray, second: np.ndarray, threshold: int = 24) -> float:
    first_mask = first[..., 3] >= threshold
    second_mask = second[..., 3] >= threshold
    union = np.logical_or(first_mask, second_mask)
    if not union.any():
        return 1.0
    return float(np.logical_and(first_mask, second_mask).sum() / union.sum())


def alpha_centroid(frame: np.ndarray) -> tuple[float, float]:
    alpha = frame[..., 3].astype(np.float64)
    total = float(alpha.sum())
    if total <= 0.0:
        return 0.0, 0.0
    yy, xx = np.mgrid[0 : frame.shape[0], 0 : frame.shape[1]]
    return float((xx * alpha).sum() / total), float((yy * alpha).sum() / total)


def alpha_bbox(frame: np.ndarray, threshold: int = 24) -> tuple[int, int, int, int]:
    ys, xs = np.nonzero(frame[..., 3] >= threshold)
    if len(xs) == 0:
        return 0, 0, 0, 0
    return int(xs.min()), int(ys.min()), int(xs.max() + 1), int(ys.max() + 1)


def duplicate_groups(digests: list[str]) -> list[list[int]]:
    groups: dict[str, list[int]] = {}
    for frame_number, digest in enumerate(digests, start=1):
        groups.setdefault(digest, []).append(frame_number)
    return [numbers for numbers in groups.values() if len(numbers) > 1]


def sequence_metrics(frames: list[Image.Image], *, wrap: bool) -> dict[str, object]:
    arrays = [pixel_array(frame) for frame in frames]
    digests = [frame_digest(frame) for frame in arrays]
    centroids = [alpha_centroid(frame) for frame in arrays]
    boxes = [alpha_bbox(frame) for frame in arrays]
    pair_indices = [(index, index + 1) for index in range(len(arrays) - 1)]
    if wrap:
        pair_indices.append((len(arrays) - 1, 0))

    pairs: list[dict[str, object]] = []
    for first_index, second_index in pair_indices:
        first_box = boxes[first_index]
        second_box = boxes[second_index]
        first_width = max(1, first_box[2] - first_box[0])
        first_height = max(1, first_box[3] - first_box[1])
        second_width = max(1, second_box[2] - second_box[0])
        second_height = max(1, second_box[3] - second_box[1])
        pairs.append(
            {
                "from": first_index + 1,
                "to": second_index + 1,
                "alpha_iou": alpha_iou(arrays[first_index], arrays[second_index]),
                "centroid_step_px": math.dist(centroids[first_index], centroids[second_index]),
                "bbox_coordinate_step_px": max(
                    abs(first_box[position] - second_box[position]) for position in range(4)
                ),
                "bbox_width_scale_step": abs(second_width / first_width - 1.0),
                "bbox_height_scale_step": abs(second_height / first_height - 1.0),
            }
        )

    transparent_rgb_counts = [
        int(
            np.logical_and(
                frame[..., 3] == 0,
                np.any(frame[..., :3] != 0, axis=2),
            ).sum()
        )
        for frame in arrays
    ]
    return {
        "frame_count": len(arrays),
        "unique_frame_count": len(set(digests)),
        "duplicate_groups_1_based": duplicate_groups(digests),
        "all_rgba_450x550": all(frame.shape == (550, 450, 4) for frame in arrays),
        "maximum_transparent_rgb_nonzero_pixels": max(transparent_rgb_counts, default=0),
        "minimum_alpha_iou": min((pair["alpha_iou"] for pair in pairs), default=1.0),
        "mean_alpha_iou": float(
            np.mean([pair["alpha_iou"] for pair in pairs]) if pairs else 1.0
        ),
        "maximum_centroid_step_px": max(
            (pair["centroid_step_px"] for pair in pairs), default=0.0
        ),
        "maximum_bbox_coordinate_step_px": max(
            (pair["bbox_coordinate_step_px"] for pair in pairs), default=0.0
        ),
        "maximum_bbox_width_scale_step": max(
            (pair["bbox_width_scale_step"] for pair in pairs), default=0.0
        ),
        "maximum_bbox_height_scale_step": max(
            (pair["bbox_height_scale_step"] for pair in pairs), default=0.0
        ),
        "pairs": pairs,
    }


def maximum_changed_pixels(
    frames: list[Image.Image],
    reference: Image.Image,
    mask: np.ndarray,
) -> int:
    reference_array = pixel_array(reference)
    maximum = 0
    for frame in frames:
        changed = np.any(pixel_array(frame) != reference_array, axis=2)
        maximum = max(maximum, int(np.logical_and(changed, mask).sum()))
    return maximum


def changed_pixel_count(
    first: Image.Image,
    second: Image.Image,
    mask: np.ndarray | None = None,
) -> int:
    changed = np.any(pixel_array(first) != pixel_array(second), axis=2)
    if mask is not None:
        changed &= mask
    return int(changed.sum())


def periodic_mismatch_pixels(frames: list[Image.Image], period: int) -> int:
    return max(
        (
            changed_pixel_count(frames[index], frames[index + period])
            for index in range(len(frames) - period)
        ),
        default=0,
    )


def brow_component_count(
    image: Image.Image,
    region: tuple[int, int, int, int],
) -> int:
    left, top, right, bottom = region
    crop = pixel_array(image)[top:bottom, left:right]
    dark = (
        (crop[..., 3] >= 160)
        & (crop[..., 0] <= 175)
        & (crop[..., 1] <= 110)
        & (crop[..., 2] <= 95)
    ).astype(np.uint8)
    count, _, stats, _ = cv2.connectedComponentsWithStats(dark, 8)
    return sum(
        int(stats[label, cv2.CC_STAT_AREA]) >= 2
        for label in range(1, count)
    )


def maximum_brow_components(frames: list[Image.Image]) -> int:
    validation_regions = (
        (158, 198, 199, 214),
        (250, 201, 286, 226),
    )
    return max(
        (
            brow_component_count(frame, region)
            for frame in frames
            for region in validation_regions
        ),
        default=0,
    )


def source_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def build_qa_v5(
    idle: Image.Image,
    neutral: Image.Image,
    underlay: Image.Image,
    registered_poses: tuple[Image.Image, ...],
    serious_neutral: Image.Image,
    hand_models: tuple[dict[str, object], ...],
    enter_frames: list[Image.Image],
    loop_frames: list[Image.Image],
    serious_loop_frames: list[Image.Image],
    serious_exit_frames: list[Image.Image],
) -> dict[str, object]:
    """Measure the v5 animation from the final rendered bitmaps."""

    if len(registered_poses) != 4 or len(hand_models) != 2:
        raise ValueError("v5 QA requires four references and two semantic hands")

    sequences = {
        "work-enter": sequence_metrics(enter_frames, wrap=False),
        "work-loop": sequence_metrics(loop_frames, wrap=True),
        "work-serious-loop": sequence_metrics(serious_loop_frames, wrap=True),
        "work-serious-exit": sequence_metrics(serious_exit_frames, wrap=False),
    }
    normal_neutral = loop_frames[0]
    serious_work_neutral = serious_loop_frames[0]
    normal_digest = frame_digest(pixel_array(normal_neutral))
    serious_digest = frame_digest(pixel_array(serious_work_neutral))
    loop_digests = [frame_digest(pixel_array(frame)) for frame in loop_frames]
    serious_digests = [
        frame_digest(pixel_array(frame)) for frame in serious_loop_frames
    ]

    def maximum_identical_run(digests: list[str]) -> int:
        if len(set(digests)) == 1:
            return len(digests)
        runs: list[int] = []
        run = 1
        for index in range(1, len(digests)):
            if digests[index] == digests[index - 1]:
                run += 1
            else:
                runs.append(run)
                run = 1
        runs.append(run)
        prefix = 1
        while prefix < len(digests) and digests[prefix] == digests[0]:
            prefix += 1
        suffix = 1
        while suffix < len(digests) and digests[-1 - suffix] == digests[-1]:
            suffix += 1
        if digests[0] == digests[-1]:
            runs.append(prefix + suffix)
        return max(runs)

    normal_pause_indices = [
        index for index, digest in enumerate(loop_digests) if digest == normal_digest
    ]
    serious_pause_indices = [
        index for index, digest in enumerate(serious_digests) if digest == serious_digest
    ]
    seam_checks = {
        "normal_loop_declared_seams_equal_neutral": all(
            loop_digests[index] == normal_digest
            for index in V5_NEUTRAL_SEAM_INDICES
        ),
        "serious_loop_declared_seams_equal_neutral": all(
            serious_digests[index] == serious_digest
            for index in V5_NEUTRAL_SEAM_INDICES
        ),
        "enter_last_equals_normal_neutral": frame_digest(
            pixel_array(enter_frames[-1])
        ) == normal_digest,
        "serious_exit_first_equals_serious_neutral": frame_digest(
            pixel_array(serious_exit_frames[0])
        ) == serious_digest,
        "serious_exit_last_equals_normal_neutral": frame_digest(
            pixel_array(serious_exit_frames[-1])
        ) == normal_digest,
        "normal_loop_wrap_is_exact": loop_digests[-1] == loop_digests[0],
        "serious_loop_wrap_is_exact": serious_digests[-1] == serious_digests[0],
    }

    allowed_union = np.zeros((CANVAS_SIZE[1], CANVAS_SIZE[0]), dtype=bool)
    moving_union = np.zeros_like(allowed_union)
    for model in hand_models:
        allowed_roi = model.get("allowed_roi")
        mask = model.get("mask")
        if not isinstance(allowed_roi, tuple) or not isinstance(mask, np.ndarray):
            raise TypeError("Invalid v5 semantic-hand model")
        allowed_union |= rectangular_support(allowed_roi)
        moving_union |= cv2.dilate(
            (mask > 0).astype(np.uint8),
            np.ones((15, 15), dtype=np.uint8),
            iterations=1,
        ) > 0

    keyboard_static_mask = rectangular_support((132, 384, 322, 456))
    keyboard_static_mask &= ~moving_union
    shoulder_static_mask = region_support(SHOULDER_LOCK_REGIONS) & ~allowed_union
    torso_static_mask = region_support((TORSO_LOCK_REGION,)) & ~allowed_union
    static_metrics = {
        "normal_outside_hand_rois_maximum_changed_pixels": maximum_changed_pixels(
            loop_frames, neutral, ~allowed_union
        ),
        "serious_outside_hand_rois_maximum_changed_pixels": maximum_changed_pixels(
            serious_loop_frames, serious_neutral, ~allowed_union
        ),
        "normal_face_maximum_changed_pixels": maximum_changed_pixels(
            loop_frames, neutral, rectangular_support(FACE_REGION)
        ),
        "serious_face_maximum_changed_pixels": maximum_changed_pixels(
            serious_loop_frames, serious_neutral, rectangular_support(FACE_REGION)
        ),
        "computer_maximum_changed_pixels": maximum_changed_pixels(
            loop_frames, neutral, rectangular_support(COMPUTER_LOCK_REGION)
        ),
        "non_target_keyboard_maximum_changed_pixels": maximum_changed_pixels(
            loop_frames, neutral, keyboard_static_mask
        ),
        "shoulder_maximum_changed_pixels": maximum_changed_pixels(
            loop_frames, neutral, shoulder_static_mask
        ),
        "torso_maximum_changed_pixels": maximum_changed_pixels(
            loop_frames, neutral, torso_static_mask
        ),
    }

    neutral_array = pixel_array(neutral)
    underlay_array = pixel_array(underlay)
    source_delta = np.max(
        np.abs(neutral_array.astype(np.int16) - underlay_array.astype(np.int16)),
        axis=2,
    )
    grid_y = np.indices(source_delta.shape)[0]
    semantic_metrics: list[dict[str, object]] = []
    model_by_name: dict[str, dict[str, object]] = {}
    for rig, model in zip(V5_HAND_RIGS, hand_models, strict=True):
        name = model.get("name")
        hand_mask = model.get("hand_mask")
        cuff_mask = model.get("cuff_mask")
        skin_core = model.get("skin_core")
        layer = model.get("layer")
        if (
            not isinstance(name, str)
            or not isinstance(hand_mask, np.ndarray)
            or not isinstance(cuff_mask, np.ndarray)
            or not isinstance(skin_core, np.ndarray)
            or not isinstance(layer, Image.Image)
        ):
            raise TypeError("Invalid v5 semantic matte metadata")
        model_by_name[name] = model
        distance_from_skin = cv2.distanceTransform(
            (~skin_core).astype(np.uint8),
            cv2.DIST_L2,
            5,
        )
        # Keyboard-coloured/key-line pixels are those matching the clean
        # underlay away from any genuine skin component.  They must never be
        # present in the moving hand layer.
        keyboard_contamination = (
            hand_mask
            & (distance_from_skin > 2.0)
            & (source_delta < 7)
            & (grid_y >= 395)
        )
        hand_support = _v5_polygon_mask((rig["hand_contour"],)) > 0
        preserved_gaps = (
            hand_support
            & ~hand_mask
            & (grid_y >= 395)
            & (underlay_array[..., 3] > 0)
        )
        layer_alpha = pixel_array(layer)[..., 3]
        semantic_metrics.append(
            {
                "name": name,
                "skin_core_pixels": int(skin_core.sum()),
                "hand_matte_pixels": int(hand_mask.sum()),
                "separate_cuff_matte_pixels": int(cuff_mask.sum()),
                "preserved_transparent_finger_gap_pixels": int(
                    preserved_gaps.sum()
                ),
                "keyboard_or_key_line_pixels_in_hand_layer": int(
                    keyboard_contamination.sum()
                ),
                "layer_pixels_outside_semantic_matte": int(
                    np.logical_and(layer_alpha > 0, model["mask"] == 0).sum()
                ),
            }
        )

    event_metrics: list[dict[str, object]] = []
    for center, side, finger, strength in V5_TYPING_EVENTS:
        model = model_by_name[side]
        fingers = model.get("fingers")
        layer = model.get("layer")
        if not isinstance(fingers, dict) or not isinstance(layer, Image.Image):
            raise TypeError("Invalid v5 event model")
        tip = fingers[finger][2]
        marker = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
        ImageDraw.Draw(marker).ellipse(
            (tip[0] - 2.0, tip[1] - 2.0, tip[0] + 2.0, tip[1] + 2.0),
            fill=(255, 255, 255, 255),
        )
        displacements = v5_hand_displacements_at(model, center)
        warped_marker = warp_v5_hand_layer(model, displacements, marker)
        measured_tip_displacement = math.dist(
            alpha_centroid(pixel_array(marker)),
            alpha_centroid(pixel_array(warped_marker)),
        )
        warped_layer = warp_v5_hand_layer(model, displacements)
        source_alpha = pixel_array(layer)[..., 3] >= 24
        warped_alpha = pixel_array(warped_layer)[..., 3] >= 24
        roi = rectangular_support(model["allowed_roi"])
        event_metrics.append(
            {
                "center_frame_0_based": int(center),
                "side": side,
                "finger": finger,
                "active_finger_joint_count": len(fingers[finger]),
                "strength": strength,
                "measured_raster_tip_displacement_px": measured_tip_displacement,
                "rendered_hand_contour_xor_pixels": int(
                    np.logical_xor(source_alpha, warped_alpha).sum()
                ),
                "rendered_peak_changed_pixels_in_hand_roi": changed_pixel_count(
                    loop_frames[int(center)], normal_neutral, roi
                ),
                "contact_amounts_center_and_next": [
                    typing_press_amount_v5(center, center),
                    typing_press_amount_v5(center + 1.0, center),
                ],
            }
        )

    refresh_sampling: dict[str, dict[str, object]] = {}
    for refresh_rate in (59, 60, 120, 144):
        refresh_sampling[str(refresh_rate)] = {}
        for label, speed, digests in (
            ("normal", 1.0, loop_digests),
            ("serious_2x", 2.0, serious_digests),
        ):
            duration = LOOP_FRAME_COUNT / (60.0 * speed)
            sample_count = math.ceil(duration * refresh_rate)
            indices = [
                int(math.floor(sample / refresh_rate * 60.0 * speed))
                % LOOP_FRAME_COUNT
                for sample in range(sample_count)
            ]
            refresh_sampling[str(refresh_rate)][label] = {
                "sample_count": sample_count,
                "distinct_authored_frame_indices": len(set(indices)),
                "distinct_rendered_bitmaps": len({digests[index] for index in indices}),
                "duration_seconds": duration,
                "sampling_duration_error_seconds": abs(
                    sample_count / refresh_rate - duration
                ),
                "one_refresh_period_seconds": 1.0 / refresh_rate,
            }

    def display_frame(frame: Image.Image, size: tuple[int, int]) -> Image.Image:
        authored = resize_rgba_premultiplied(
            frame,
            (399, round(frame.height * 399 / frame.width)),
        )
        atlas_frame = Image.new("RGBA", (399, 509), (0, 0, 0, 0))
        atlas_frame.alpha_composite(authored, (0, 509 - authored.height))
        return resize_rgba_premultiplied(atlas_frame, size)

    def shimmer_metrics(
        frames: list[Image.Image],
        size: tuple[int, int],
    ) -> dict[str, int]:
        rendered = [
            premultiplied_array(display_frame(frame, size)).astype(np.int16)
            for frame in frames
        ]
        total_pixels = 0
        maximum_pixels = 0
        maximum_component_area = 0
        for index, current in enumerate(rendered):
            previous = rendered[index - 1]
            following = rendered[(index + 1) % len(rendered)]
            impulse = (
                (np.max(np.abs(current - previous), axis=2) > 64)
                & (np.max(np.abs(current - following), axis=2) > 64)
                & (np.max(np.abs(previous - following), axis=2) < 8)
            )
            count = int(impulse.sum())
            total_pixels += count
            maximum_pixels = max(maximum_pixels, count)
            component_count, _, statistics, _ = cv2.connectedComponentsWithStats(
                impulse.astype(np.uint8),
                connectivity=8,
            )
            for component_index in range(1, component_count):
                maximum_component_area = max(
                    maximum_component_area,
                    int(statistics[component_index, cv2.CC_STAT_AREA]),
                )
        return {
            "total_high_contrast_single_frame_pixels": total_pixels,
            "maximum_per_frame": maximum_pixels,
            "maximum_connected_component_area": maximum_component_area,
        }

    display_sizes = {
        "100pct_dpi": (190, 242),
        "125pct_dpi": (238, 302),
        "150pct_dpi": (285, 363),
        "140pct_pet_at_150pct_dpi": (399, 508),
    }
    display_shimmer = {
        label: {
            "normal": shimmer_metrics(loop_frames, size),
            "serious": shimmer_metrics(serious_loop_frames, size),
        }
        for label, size in display_sizes.items()
    }
    expression_patch_support = np.logical_or.reduce(
        [rectangular_support(region) for region in EXPRESSION_PATCH_REGIONS]
    )
    serious_brow_support = np.logical_or.reduce(
        [rectangular_support(region) for region in EXPRESSION_PATCH_REGIONS[:2]]
    )
    serious_eye_lock_support = np.logical_or.reduce(
        [rectangular_support(region) for region in SERIOUS_EYE_LOCK_REGIONS]
    )

    invariants = {
        "neutral_bbox_alpha8": list(visible_bbox(neutral)),
        "registered_reference_bboxes_alpha8": [
            list(visible_bbox(pose)) for pose in registered_poses
        ],
        "declared_neutral_seam_indices_0_based": list(V5_NEUTRAL_SEAM_INDICES),
        "normal_actual_neutral_pause_indices_0_based": normal_pause_indices,
        "serious_actual_neutral_pause_indices_0_based": serious_pause_indices,
        "normal_maximum_consecutive_identical_frames": maximum_identical_run(
            loop_digests
        ),
        "serious_maximum_consecutive_identical_frames": maximum_identical_run(
            serious_digests
        ),
        "normal_loop_seconds": LOOP_FRAME_COUNT / 60.0,
        "serious_loop_seconds_at_runtime_2x": LOOP_FRAME_COUNT / 120.0,
        "normal_key_presses_per_second": len(V5_TYPING_EVENTS)
        / (LOOP_FRAME_COUNT / 60.0),
        "serious_key_presses_per_second": len(V5_TYPING_EVENTS)
        / (LOOP_FRAME_COUNT / 120.0),
        "press_curve_has_negative_prelift": any(
            amount < 0.0 for _, amount in V5_TYPING_PRESS_CURVE
        ),
        "press_curve_release_is_monotonic": all(
            first >= second >= 0.0
            for first, second in zip(
                [amount for offset, amount in V5_TYPING_PRESS_CURVE if offset >= 1.0],
                [amount for offset, amount in V5_TYPING_PRESS_CURVE if offset >= 1.0][1:],
            )
        ),
        "semantic_hands": semantic_metrics,
        "events": event_metrics,
        "static_locks": static_metrics,
        "refresh_sampling": refresh_sampling,
        "display_shimmer": display_shimmer,
        "serious_expression_changed_pixels": changed_pixel_count(
            serious_work_neutral,
            normal_neutral,
            rectangular_support(FACE_REGION),
        ),
        "serious_brow_changed_pixels": changed_pixel_count(
            serious_work_neutral,
            normal_neutral,
            serious_brow_support,
        ),
        "serious_mouth_changed_pixels": changed_pixel_count(
            serious_work_neutral,
            normal_neutral,
            rectangular_support(EXPRESSION_PATCH_REGIONS[2]),
        ),
        "serious_eye_lock_changed_pixels": changed_pixel_count(
            serious_work_neutral,
            normal_neutral,
            serious_eye_lock_support,
        ),
        "serious_expression_outside_patch_changed_pixels": changed_pixel_count(
            serious_work_neutral,
            normal_neutral,
            ~expression_patch_support,
        ),
    }

    failures: list[str] = []
    expected_counts = {
        "work-enter": ENTER_FRAME_COUNT,
        "work-loop": LOOP_FRAME_COUNT,
        "work-serious-loop": SERIOUS_LOOP_FRAME_COUNT,
        "work-serious-exit": SERIOUS_EXIT_FRAME_COUNT,
    }
    for name, expected_count in expected_counts.items():
        metrics = sequences[name]
        if metrics["frame_count"] != expected_count:
            failures.append(f"{name} frame count")
        if not metrics["all_rgba_450x550"]:
            failures.append(f"{name} frame geometry")
        if metrics["maximum_transparent_rgb_nonzero_pixels"] != 0:
            failures.append(f"{name} transparent RGB")
    if sequences["work-loop"]["unique_frame_count"] < 56:
        failures.append("normal loop pose diversity")
    if sequences["work-serious-loop"]["unique_frame_count"] < 56:
        failures.append("serious loop pose diversity")
    if sequences["work-serious-exit"]["unique_frame_count"] < 20:
        failures.append("serious exit pose diversity")
    for name, passed in seam_checks.items():
        if not passed:
            failures.append(name)
    for name, changed in static_metrics.items():
        if changed != 0:
            failures.append(name)
    for metrics in semantic_metrics:
        if metrics["keyboard_or_key_line_pixels_in_hand_layer"] != 0:
            failures.append(f"{metrics['name']} keyboard contamination")
        if metrics["layer_pixels_outside_semantic_matte"] != 0:
            failures.append(f"{metrics['name']} layer escaped matte")
        if metrics["preserved_transparent_finger_gap_pixels"] < 20:
            failures.append(f"{metrics['name']} finger gaps collapsed")
        if metrics["separate_cuff_matte_pixels"] < 200:
            failures.append(f"{metrics['name']} cuff matte too small")
    for metrics in event_metrics:
        displacement = metrics["measured_raster_tip_displacement_px"]
        if not 5.5 <= displacement <= 7.0:
            failures.append(
                f"frame {metrics['center_frame_0_based']} raster tip displacement"
            )
        if metrics["active_finger_joint_count"] != 3:
            failures.append(f"{metrics['side']} {metrics['finger']} joint count")
        if metrics["rendered_hand_contour_xor_pixels"] < 150:
            failures.append(f"{metrics['side']} {metrics['finger']} contour motion")
        if metrics["rendered_peak_changed_pixels_in_hand_roi"] < 1200:
            failures.append(f"{metrics['side']} {metrics['finger']} visible motion")
        if min(metrics["contact_amounts_center_and_next"]) < 0.95:
            failures.append(f"{metrics['side']} {metrics['finger']} contact hold")
    if invariants["normal_maximum_consecutive_identical_frames"] > 5:
        failures.append("normal loop excessive still run")
    if invariants["serious_maximum_consecutive_identical_frames"] > 5:
        failures.append("serious loop excessive still run")
    for refresh_rate, modes in refresh_sampling.items():
        normal = modes["normal"]
        serious = modes["serious_2x"]
        if normal["distinct_authored_frame_indices"] < 95:
            failures.append(f"{refresh_rate}Hz normal clock coverage")
        if serious["distinct_authored_frame_indices"] < 48:
            failures.append(f"{refresh_rate}Hz serious clock coverage")
        for mode_name, metrics in modes.items():
            if metrics["sampling_duration_error_seconds"] > metrics[
                "one_refresh_period_seconds"
            ] + 1e-12:
                failures.append(f"{refresh_rate}Hz {mode_name} duration")
    for size_name, modes in display_shimmer.items():
        for mode_name, metrics in modes.items():
            if metrics["maximum_connected_component_area"] > 1:
                failures.append(f"{size_name} {mode_name} isolated shimmer cluster")
            if metrics["maximum_per_frame"] > 1:
                failures.append(f"{size_name} {mode_name} isolated shimmer count")
    if not invariants["press_curve_has_negative_prelift"]:
        failures.append("missing prelift")
    if not invariants["press_curve_release_is_monotonic"]:
        failures.append("non-monotonic release")
    if not 80 <= invariants["serious_expression_changed_pixels"] <= 4000:
        failures.append("serious expression locality")
    if invariants["serious_brow_changed_pixels"] < 120:
        failures.append("serious brow expression too subtle")
    if invariants["serious_mouth_changed_pixels"] < 40:
        failures.append("serious mouth expression too subtle")
    if invariants["serious_eye_lock_changed_pixels"] != 0:
        failures.append("serious expression changed locked eyes")
    if invariants["serious_expression_outside_patch_changed_pixels"] != 0:
        failures.append("serious expression escaped local patches")

    source_paths = (
        WORK_ANCHOR_PATH,
        KEYBOARD_UNDERLAY_PATH,
        LEFT_INDEX_DOWN_PATH,
        RIGHT_INDEX_DOWN_PATH,
        LEFT_MIDDLE_DOWN_PATH,
        RIGHT_MIDDLE_DOWN_PATH,
        SERIOUS_REFERENCE_PATH,
        IDLE_PATH,
    )
    return {
        "version": 5,
        "canvas": {"width": CANVAS_SIZE[0], "height": CANVAS_SIZE[1], "mode": "RGBA"},
        "sources": {
            str(path.relative_to(ROOT)).replace("\\", "/"): source_sha256(path)
            for path in source_paths
        },
        "sequences": sequences,
        "contacts": {
            "arm_motion": str(ARM_CONTACT_PATH.relative_to(ROOT)).replace("\\", "/"),
            "face_transition": str(FACE_CONTACT_PATH.relative_to(ROOT)).replace("\\", "/"),
        },
        "seams": seam_checks,
        "invariants": invariants,
        "failures": failures,
        "passed": not failures,
    }


def main() -> None:
    for required in (
        WORK_ANCHOR_PATH,
        KEYBOARD_UNDERLAY_PATH,
        LEFT_INDEX_DOWN_PATH,
        RIGHT_INDEX_DOWN_PATH,
        LEFT_MIDDLE_DOWN_PATH,
        RIGHT_MIDDLE_DOWN_PATH,
        SERIOUS_REFERENCE_PATH,
        IDLE_PATH,
    ):
        if not required.is_file():
            raise FileNotFoundError(required)

    idle = load_idle()
    neutral = load_work_neutral()
    with Image.open(KEYBOARD_UNDERLAY_PATH) as opened:
        underlay = fit_source_to_runtime_canvas(
            opened,
            maximum_size=WORK_MAX_SIZE,
            bottom=WORK_BOTTOM,
        )
    left_index_down = normalize_key_pose_to_neutral_bbox(LEFT_INDEX_DOWN_PATH, neutral)
    right_index_down = normalize_key_pose_to_neutral_bbox(RIGHT_INDEX_DOWN_PATH, neutral)
    left_middle_down = normalize_key_pose_to_neutral_bbox(LEFT_MIDDLE_DOWN_PATH, neutral)
    right_middle_down = normalize_key_pose_to_neutral_bbox(RIGHT_MIDDLE_DOWN_PATH, neutral)
    serious_reference = load_serious_reference()
    clean_face = inpaint_neutral_expression(neutral)
    serious_neutral = build_serious_neutral(neutral, serious_reference)
    # Generated contact poses remain immutable anatomy references. Runtime v5
    # uses the approved home-row character plus a clean keyboard underlay and
    # two independent semantic hand/cuff mattes.
    hand_models = tuple(
        build_v5_hand_model(neutral, underlay, rig)
        for rig in V5_HAND_RIGS
    )
    save_png_atomically(neutral, NORMALIZED_WORK_PATH)
    save_png_atomically(underlay, NORMALIZED_UNDERLAY_PATH)
    for pose, destination in (
        (left_index_down, NORMALIZED_LEFT_INDEX_DOWN_PATH),
        (right_index_down, NORMALIZED_RIGHT_INDEX_DOWN_PATH),
        (left_middle_down, NORMALIZED_LEFT_MIDDLE_DOWN_PATH),
        (right_middle_down, NORMALIZED_RIGHT_MIDDLE_DOWN_PATH),
    ):
        save_png_atomically(pose, destination)
    save_png_atomically(
        serious_reference,
        NORMALIZED_SERIOUS_REFERENCE_PATH,
    )
    save_png_atomically(serious_neutral, NORMALIZED_SERIOUS_WORK_PATH)

    loop_frames = remove_isolated_temporal_shimmer(
        build_work_loop_v5(neutral, underlay, hand_models),
        wrap=True,
        protected_indices=V5_NEUTRAL_SEAM_INDICES,
    )
    serious_loop_frames = remove_isolated_temporal_shimmer(
        build_work_loop_v5(serious_neutral, underlay, hand_models),
        wrap=True,
        protected_indices=V5_NEUTRAL_SEAM_INDICES,
    )
    enter_frames = build_work_enter(idle, loop_frames[0])
    serious_exit_frames = remove_isolated_temporal_shimmer(
        build_serious_exit_v5(
            neutral,
            underlay,
            clean_face,
            serious_reference,
            serious_neutral,
            hand_models,
        ),
        wrap=False,
        protected_indices=(0, SERIOUS_EXIT_FRAME_COUNT - 1),
    )

    qa = build_qa_v5(
        idle,
        neutral,
        underlay,
        (left_index_down, right_index_down, left_middle_down, right_middle_down),
        serious_neutral,
        hand_models,
        enter_frames,
        loop_frames,
        serious_loop_frames,
        serious_exit_frames,
    )
    QA_PATH.write_text(json.dumps(qa, ensure_ascii=False, indent=2), encoding="utf-8")
    if not qa["passed"]:
        raise AssertionError("; ".join(qa["failures"]))

    write_arm_motion_contact(loop_frames, serious_loop_frames)
    write_face_transition_contact(serious_exit_frames)

    paths = {
        "enter": write_sequence("enter", enter_frames),
        "loop": write_sequence("loop", loop_frames),
        "serious-loop": write_sequence("serious-loop", serious_loop_frames),
        "serious-exit": write_sequence("serious-exit", serious_exit_frames),
    }

    print("Built v5 semantic articulated work animation from immutable local sources.")
    for name in ("enter", "loop", "serious-loop", "serious-exit"):
        metrics = qa["sequences"][f"work-{name}"]
        print(
            f"{name}: {len(paths[name])} frames, "
            f"unique={metrics['unique_frame_count']}, "
            f"min/mean IoU={metrics['minimum_alpha_iou']:.6f}/"
            f"{metrics['mean_alpha_iou']:.6f}, "
            f"max centroid step={metrics['maximum_centroid_step_px']:.4f}px"
        )
    print(
        "locked-region drift: "
        f"outside={qa['invariants']['static_locks']['normal_outside_hand_rois_maximum_changed_pixels']}, "
        f"computer={qa['invariants']['static_locks']['computer_maximum_changed_pixels']}, "
        f"keyboard={qa['invariants']['static_locks']['non_target_keyboard_maximum_changed_pixels']}, "
        f"shoulder={qa['invariants']['static_locks']['shoulder_maximum_changed_pixels']}, "
        f"torso={qa['invariants']['static_locks']['torso_maximum_changed_pixels']}"
    )
    print(
        "natural typing motion: "
        "measured-tip-range="
        f"{min(event['measured_raster_tip_displacement_px'] for event in qa['invariants']['events']):.3f}-"
        f"{max(event['measured_raster_tip_displacement_px'] for event in qa['invariants']['events']):.3f}px, "
        "normal/serious keypresses="
        f"{qa['invariants']['normal_key_presses_per_second']:.1f}/"
        f"{qa['invariants']['serious_key_presses_per_second']:.1f} per second"
    )
    print(f"QA: {QA_PATH}")


if __name__ == "__main__":
    main()
