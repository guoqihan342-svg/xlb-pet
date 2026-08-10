"""Build the compact, rounded lower arm used by the side-edge peek loop.

The retired implementation shifted a rectangular 7px strip after RIFE.  It
made the lower sleeve look like a long tube and duplicated x=0 into visible
scanline bars.  This replacement keeps the authored character pixels, edits
only the lower purple sleeve ROI, and gives the arm one deterministic curved
silhouette.  The right-edge pose is the runtime mirror of this left sequence.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
DEFAULT_REPORT_PATH = ROOT / ".codex_tmp" / "edge-compact-grip-qa.json"

CONTRACT_NAME = "compact-side-grip-v1"
FRAME_COUNT = 48
CANVAS_SIZE = (450, 550)
ROI = (0, 342, 146, 426)
ALPHA_THRESHOLD = 24
KEY_PHASE_FRAMES = {1: 48, 2: 12, 3: 24, 4: 36}
MAX_HORIZONTAL_BOTTOM_RUN = 6
MIN_ADJACENT_ROI_ALPHA_IOU = 0.94
MAX_ADJACENT_ROI_AREA_CHANGE = 0.04
MAX_ADJACENT_WRIST_CENTER_STEP = 2.0
CUFF_DROP_CLEANUP_Y = 411
CUFF_DROP_CLEANUP_X = 31
ENDPOINT_BRIDGE_FRAMES = frozenset({1, 2, 3, 4, 5, 44, 45, 46, 47, 48})
MIN_ENDPOINT_BRIDGE_ALPHA_PIXELS = 260
FOREARM_OUTLINE_RGB = np.array([83, 40, 93], dtype=np.uint8)
MAX_FINAL_OUTSIDE_CURVE_PIXELS = 16
MIN_FINAL_OUTLINE_PIXELS = 250
AUTHORED_SOURCE_REVEAL = 7.0
AUTHORED_SOURCE_Y_IN = (345.0, 361.0)
AUTHORED_SOURCE_Y_OUT = (420.0, 438.0)
AUTHORED_SOURCE_X_OUT = (95.0, 120.0)


def pixel_sha256(pixels: np.ndarray) -> str:
    return hashlib.sha256(pixels.tobytes()).hexdigest()


def sequence_sha256(frames: list[np.ndarray]) -> str:
    digest = hashlib.sha256()
    for frame_number, pixels in enumerate(frames, start=1):
        height, width = pixels.shape[:2]
        digest.update(frame_number.to_bytes(2, "big"))
        digest.update(width.to_bytes(2, "big"))
        digest.update(height.to_bytes(2, "big"))
        digest.update(pixels.tobytes())
    return digest.hexdigest()


def load_pixels(path: Path) -> np.ndarray:
    with Image.open(path) as opened:
        image = opened.convert("RGBA")
    if image.size != CANVAS_SIZE:
        raise ValueError(f"Unexpected edge frame size for {path.name}: {image.size}")
    return np.asarray(image, dtype=np.uint8).copy()


def save_png_atomically(pixels: np.ndarray, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(f".{destination.stem}.tmp.png")
    Image.fromarray(pixels, "RGBA").save(temporary, format="PNG", optimize=True)
    temporary.replace(destination)


def dilate(mask: np.ndarray, size: int) -> np.ndarray:
    kernel = np.ones((size, size), dtype=np.uint8)
    return cv2.dilate(mask.astype(np.uint8), kernel, iterations=1) > 0


def _smoothstep(values: np.ndarray) -> np.ndarray:
    values = np.clip(values, 0.0, 1.0)
    return values * values * (3.0 - 2.0 * values)


def authored_lower_arm_source(frame: np.ndarray) -> np.ndarray:
    """Reconstruct the old authored pixel field without restoring its shape.

    Earlier releases sampled this field directly into the full sleeve, which
    produced a long tube at the screen boundary.  The field is now private
    source material only: callers may copy its pixels through the approved
    short C-shaped mask, but can never expose its former outer silhouette.
    """

    height, width = frame.shape[:2]
    yy, xx = np.mgrid[:height, :width]
    fade_in = _smoothstep(
        (yy.astype(np.float32) - AUTHORED_SOURCE_Y_IN[0])
        / (AUTHORED_SOURCE_Y_IN[1] - AUTHORED_SOURCE_Y_IN[0])
    )
    fade_out = 1.0 - _smoothstep(
        (yy.astype(np.float32) - AUTHORED_SOURCE_Y_OUT[0])
        / (AUTHORED_SOURCE_Y_OUT[1] - AUTHORED_SOURCE_Y_OUT[0])
    )
    horizontal = 1.0 - _smoothstep(
        (xx.astype(np.float32) - AUTHORED_SOURCE_X_OUT[0])
        / (AUTHORED_SOURCE_X_OUT[1] - AUTHORED_SOURCE_X_OUT[0])
    )
    displacement = AUTHORED_SOURCE_REVEAL * fade_in * fade_out * horizontal
    source_x = np.clip(xx.astype(np.float32) - displacement, 0, width - 1)
    x0 = np.floor(source_x).astype(np.int32)
    x1 = np.minimum(x0 + 1, width - 1)
    fraction = (source_x - x0)[..., None]
    alpha = frame[..., 3:4].astype(np.float32) / 255.0
    premultiplied = np.concatenate(
        (
            frame[..., :3].astype(np.float32) * alpha,
            frame[..., 3:4].astype(np.float32),
        ),
        axis=2,
    )
    sampled = (
        premultiplied[yy, x0] * (1.0 - fraction)
        + premultiplied[yy, x1] * fraction
    )
    authored = frame.copy()
    support = displacement > 1e-6
    sampled_alpha = np.clip(np.rint(sampled[..., 3]), 0, 255).astype(np.uint8)
    sampled_rgb = np.zeros_like(sampled[..., :3], dtype=np.uint8)
    visible = sampled[..., 3] > 0.5
    sampled_rgb[visible] = np.clip(
        np.rint(sampled[..., :3][visible] * 255.0 / sampled[..., 3:4][visible]),
        0,
        255,
    ).astype(np.uint8)
    authored[support, :3] = sampled_rgb[support]
    authored[support, 3] = sampled_alpha[support]
    authored[authored[..., 3] == 0, :3] = 0
    return authored


def purple_mask(frame: np.ndarray) -> np.ndarray:
    rgb = frame[..., :3].astype(np.int16)
    alpha = frame[..., 3]
    return (
        (alpha > 3)
        & (rgb[..., 2] > rgb[..., 1] * 1.05)
        & (rgb[..., 2] > rgb[..., 0] * 0.80)
        & (rgb[..., 0] > 28)
    )


def skin_mask(frame: np.ndarray) -> np.ndarray:
    rgb = frame[..., :3].astype(np.int16)
    alpha = frame[..., 3]
    return (
        (alpha > 16)
        & (rgb[..., 0] >= 150)
        & (rgb[..., 1] >= 70)
        & (rgb[..., 2] >= 45)
        & (rgb[..., 0] >= rgb[..., 1] + 4)
        & (rgb[..., 0] >= rgb[..., 2] + 10)
    )


def reddish_outline_mask(frame: np.ndarray) -> np.ndarray:
    rgb = frame[..., :3].astype(np.int16)
    alpha = frame[..., 3]
    return (
        (alpha > 3)
        & (rgb[..., 0] >= 35)
        & (rgb[..., 0] >= rgb[..., 1] * 1.08)
        & (rgb[..., 0] >= rgb[..., 2] * 1.03)
    )


def lower_hand_core_mask(frame: np.ndarray) -> np.ndarray:
    """Return the flesh component for the hand gripping the left boundary."""

    height, width = frame.shape[:2]
    yy, xx = np.mgrid[:height, :width]
    candidates = (
        skin_mask(frame)
        & (xx <= 58)
        & (yy >= 330)
        & (yy <= 416)
    )
    component_count, labels, stats, _ = cv2.connectedComponentsWithStats(
        candidates.astype(np.uint8), connectivity=8
    )
    # The real lower hand is the large flesh component that reaches the screen
    # boundary.  The retired sleeve contains a much smaller pink/magenta cuff
    # highlight; selecting every skin-like component accidentally protected
    # that highlight and left its lower half hanging in transparent space.
    selected_label = 0
    selected_area = 0
    for label in range(1, component_count):
        x, y, component_width, component_height, area = (
            int(value) for value in stats[label]
        )
        if (
            area >= 200
            and x <= 6
            and y + component_height - 1 >= 389
            and area > selected_area
        ):
            selected_label = label
            selected_area = area
    if not selected_label:
        raise AssertionError("side-grip lower hand flesh component is missing")
    return labels == selected_label


def detached_lower_hand_fragment_mask(frame: np.ndarray) -> np.ndarray:
    """Return isolated RIFE specks below the real lower-hand silhouette."""

    alpha = frame[..., 3] > 0
    component_count, labels, stats, _ = cv2.connectedComponentsWithStats(
        alpha.astype(np.uint8), connectivity=8
    )
    fragments = np.zeros_like(alpha)
    for label in range(1, component_count):
        x, y, component_width, component_height, area = (
            int(value) for value in stats[label]
        )
        if (
            area <= 3
            and x < 42
            and y >= 401
            and y + component_height <= 417
        ):
            fragments |= labels == label
    return fragments


def protected_character_mask(frame: np.ndarray) -> np.ndarray:
    """Lock the head, face, hat, headset and both gripping hands."""

    height, width = frame.shape[:2]
    yy, xx = np.mgrid[:height, :width]
    skin = skin_mask(frame)
    purple = purple_mask(frame)
    near_skin = dilate(skin, 7)
    skin_and_outline = skin | (near_skin & reddish_outline_mask(frame))
    face = (
        skin_and_outline
        & (xx <= 160)
        & (yy >= 325)
        & (yy <= 388)
    )
    hand_core = lower_hand_core_mask(frame)
    lower_hand = hand_core | (
        dilate(hand_core, 7)
        & (skin | reddish_outline_mask(frame))
        & (yy <= 412)
    )
    # The authored lower hand ends at y=410.  Older RIFE inputs also contain a
    # magenta cuff highlight that curls down to y=416; the broad skin-colour
    # detector sees that highlight as flesh.  Cap this protection at the
    # verified hand baseline so the detached lower half of that cuff can be
    # removed without touching any hand pixel in the protected 342..410 ROI.
    protected = face | (lower_hand & (yy <= 410))
    # Single-pixel RIFE specks below the connected hand outline are not hand
    # artwork.  Keep them out of the protection mask so they cannot shimmer on
    # alternating frames.
    protected &= ~detached_lower_hand_fragment_mask(frame)
    protected |= yy < ROI[1]
    protected |= xx >= ROI[2]
    return protected


def arm_support_mask(frame: np.ndarray, protected: np.ndarray) -> np.ndarray:
    height, width = frame.shape[:2]
    yy, xx = np.mgrid[:height, :width]
    alpha = frame[..., 3]
    near_purple = dilate(purple_mask(frame), 15)
    roi = (
        (xx >= ROI[0])
        & (xx < ROI[2])
        & (yy >= ROI[1])
        & (yy < ROI[3])
    )
    lower_body = (xx >= 45) & (yy >= 382)
    return roi & (near_purple | lower_body) & (alpha > 0) & ~protected


def side_grip_phase(frame_number: int) -> float:
    if frame_number < 1 or frame_number > FRAME_COUNT:
        raise ValueError(f"Invalid side-grip frame: {frame_number}")
    # f001/f048 are shortest, f012/f036 half out, and f024 fully peeks.
    return max(0.0, math.sin(math.pi * frame_number / FRAME_COUNT))


def compact_baseline_bottom_curve(
    frame_number: int, xx: np.ndarray
) -> tuple[np.ndarray, float]:
    peek = side_grip_phase(frame_number)
    x_end = 92.0 + 30.0 * peek
    first_run = np.clip(xx.astype(np.float32) / 28.0, 0.0, 1.0)
    tail_u = np.clip(
        (xx.astype(np.float32) - 28.0) / max(1.0, x_end - 28.0),
        0.0,
        1.0,
    )
    smooth = tail_u * tail_u * (3.0 - 2.0 * tail_u)
    # The linear component retains a small slope at both ends. It makes the
    # arm read as a rounded teardrop and limits every quantized horizontal run.
    eased = 0.55 * tail_u + 0.45 * smooth
    end_y = 378.0 - 4.0 * peek
    curve = 412.0 - 5.0 * first_run + (end_y - 407.0) * eased
    return curve, x_end


def side_grip_bottom_curve(
    frame_number: int, xx: np.ndarray
) -> tuple[np.ndarray, float]:
    """Return the approved short, full C-shaped forearm silhouette."""

    peek = side_grip_phase(frame_number)
    x_end = 102.0 + 30.0 * peek
    cap_width = 28.0
    cap_start = x_end - cap_width
    start_y = 418.0 - 2.0 * peek
    end_y = 378.0 - 4.0 * peek
    join_y = end_y + 28.0
    values = xx.astype(np.float32)
    body_u = np.clip(
        (values - 28.0) / max(1.0, cap_start - 28.0),
        0.0,
        1.0,
    )
    body = start_y + (join_y - start_y) * (
        0.90 * body_u + 0.10 * body_u * body_u
    )
    cap_u = np.clip((values - cap_start) / cap_width, 0.0, 1.0)
    cap = join_y + (end_y - join_y) * (
        0.20 * cap_u + 0.80 * cap_u * cap_u
    )
    return np.where(values <= cap_start, body, cap), x_end


def is_final_side_grip_frame(frame: np.ndarray, frame_number: int) -> bool:
    """Recognize the projected final geometry so repeated installs are no-ops."""

    height, width = frame.shape[:2]
    yy, xx = np.mgrid[:height, :width]
    protected = protected_character_mask(frame)
    curve, x_end = side_grip_bottom_curve(frame_number, xx)
    outside_curve = (
        (xx >= 28)
        & (xx < ROI[2])
        & (yy >= ROI[1])
        & (yy < ROI[3])
        & ~protected
        & purple_mask(frame)
        & ((xx > x_end + 1.0) | (yy > curve + 2.0))
    )

    line_mask = np.zeros((height, width), dtype=np.uint8)
    curve_1d, _ = side_grip_bottom_curve(
        frame_number, np.arange(width)[None, :]
    )
    points = np.asarray(
        [
            (x, int(round(float(curve_1d[0, x]))))
            for x in range(28, int(math.floor(x_end)) + 1)
        ],
        dtype=np.int32,
    )
    cv2.polylines(
        line_mask,
        [points],
        False,
        255,
        thickness=2,
        lineType=cv2.LINE_AA,
    )
    exact_outline = (
        (line_mask > 0)
        & np.all(frame[..., :3] == FOREARM_OUTLINE_RGB, axis=2)
        & (frame[..., 3] > 0)
        & ~protected
    )
    if int(outside_curve.sum()) > MAX_FINAL_OUTSIDE_CURVE_PIXELS:
        return False
    if int(exact_outline.sum()) < MIN_FINAL_OUTLINE_PIXELS:
        return False
    if (
        frame_number in ENDPOINT_BRIDGE_FRAMES
        and endpoint_bridge_alpha_area(frame) < MIN_ENDPOINT_BRIDGE_ALPHA_PIXELS
    ):
        return False
    return True


def repeated_warp_prefix_mask(
    frame: np.ndarray, hand_flesh: np.ndarray
) -> np.ndarray:
    """Find the exact x=0 repetitions left by the retired 7px clamp warp."""

    height, width = frame.shape[:2]
    repeated = np.zeros((height, width), dtype=bool)
    for y in range(ROI[1], ROI[3]):
        first = frame[y, 0]
        if first[3] == 0 or hand_flesh[y, 0]:
            continue
        run = 1
        while run < ROI[2] and np.array_equal(frame[y, run], first):
            run += 1
        if run >= 3:
            repeated[y, :run] = True
    return repeated


def reshape_side_grip_frame(
    frame: np.ndarray, frame_number: int
) -> tuple[np.ndarray, dict[str, object]]:
    if frame.shape != (CANVAS_SIZE[1], CANVAS_SIZE[0], 4):
        raise ValueError(f"Unexpected side-grip array shape: {frame.shape}")

    if is_final_side_grip_frame(frame, frame_number):
        _, x_end = side_grip_bottom_curve(
            frame_number, np.zeros((1, 1), dtype=np.float32)
        )
        return frame.copy(), {
            "frame": frame_number,
            "phase": side_grip_phase(frame_number),
            "xEnd": x_end,
            "changedPixels": 0,
            "protectedChanges": 0,
            "outsideSupportChanges": 0,
            "removedRepeatedPrefixPixels": 0,
            "alreadyFinal": True,
        }

    output = frame.copy()
    authored_source = authored_lower_arm_source(frame)
    height, width = frame.shape[:2]
    yy, xx = np.mgrid[:height, :width]
    protected = protected_character_mask(frame)
    support = arm_support_mask(frame, protected)
    curve, x_end = compact_baseline_bottom_curve(frame_number, xx)
    remove = support & (
        (xx.astype(np.float32) > x_end)
        | (yy.astype(np.float32) > curve)
    )

    hand_flesh = skin_mask(frame)
    hand_color = hand_flesh | reddish_outline_mask(frame)
    legacy_contact_fill = (
        (xx < 26)
        & (yy >= 392)
        & (frame[..., 3] > 0)
        & ~protected
    )
    legacy_tip = (
        (xx < 8)
        & (yy >= 390)
        & (frame[..., 3] > 0)
        & ~hand_color
    )
    repeated_prefix = repeated_warp_prefix_mask(frame, hand_flesh)
    detached_fragments = detached_lower_hand_fragment_mask(frame)
    # RIFE reproduces the old cuff's lower half as a thin U-shaped arc below
    # the real hand.  Keep the upper/right cuff arc that joins the wrist and
    # discard only the part hanging below the verified hand baseline.
    cuff_drop = (
        (xx <= CUFF_DROP_CLEANUP_X)
        & (yy >= CUFF_DROP_CLEANUP_Y)
        & (yy < ROI[3])
        & (frame[..., 3] > 0)
        & ~protected
    )
    edit_support = (
        support
        | legacy_contact_fill
        | legacy_tip
        | repeated_prefix
        | cuff_drop
        | detached_fragments
    )
    remove |= legacy_contact_fill | legacy_tip | repeated_prefix
    remove |= cuff_drop
    remove |= detached_fragments

    if remove.any():
        output[remove] = 0

        line_mask = np.zeros((height, width), dtype=np.uint8)
        curve_1d, _ = compact_baseline_bottom_curve(
            frame_number, np.arange(width)[None, :]
        )
        points = [
            (x, int(round(float(curve_1d[0, x]))))
            for x in range(32, min(width, int(math.floor(x_end)) + 1))
        ]
        if len(points) >= 2:
            cv2.polylines(
                line_mask,
                [np.asarray(points, dtype=np.int32)],
                False,
                255,
                thickness=2,
                lineType=cv2.LINE_AA,
            )
        line_alpha = line_mask.astype(np.float32) / 255.0
        line = (line_alpha > 0.0) & support & ~remove & ~protected
        if line.any():
            coverage = line_alpha[line, None]
            original_rgb = output[line, :3].astype(np.float32)
            outline_rgb = np.array([72.0, 38.0, 102.0], dtype=np.float32)
            output[line, :3] = np.clip(
                np.rint(
                    original_rgb * (1.0 - coverage)
                    + outline_rgb * coverage
                ),
                0,
                255,
            ).astype(np.uint8)
            output[line, 3] = np.maximum(
                output[line, 3],
                np.rint(220.0 * line_alpha[line]).astype(np.uint8),
            )

    # Restore the approved full, short forearm from the authored input after
    # producing the compact baseline above.  This deliberately reuses only
    # source artwork; the character, hands and face stay byte-for-byte locked.
    final_protected = protected_character_mask(output)
    final_curve, final_x_end = side_grip_bottom_curve(frame_number, xx)
    authored_near_purple = dilate(purple_mask(authored_source), 15)
    restore = (
        (xx >= 28)
        & (xx <= final_x_end)
        & (yy >= ROI[1])
        & (yy <= final_curve)
        & (authored_source[..., 3] > 0)
        & authored_near_purple
        & ~final_protected
    )
    output[restore] = authored_source[restore]

    # Rebuild the short curved boundary with one exact colour from the authored
    # sleeve palette.  The curve is continuously sloped and its quantized run
    # is hard-limited by QA to six source pixels.
    final_line_mask = np.zeros((height, width), dtype=np.uint8)
    final_curve_1d, _ = side_grip_bottom_curve(
        frame_number, np.arange(width)[None, :]
    )
    final_points = np.asarray(
        [
            (x, int(round(float(final_curve_1d[0, x]))))
            for x in range(28, int(math.floor(final_x_end)) + 1)
        ],
        dtype=np.int32,
    )
    cv2.polylines(
        final_line_mask,
        [final_points],
        False,
        255,
        thickness=2,
        lineType=cv2.LINE_AA,
    )
    near_output = dilate(output[..., 3] > 0, 3)
    final_line = (
        (final_line_mask > 0)
        & near_output
        & ~final_protected
        & (xx >= 28)
        & (xx <= final_x_end)
    )
    output[final_line, :3] = FOREARM_OUTLINE_RGB
    output[final_line, 3] = np.maximum(
        output[final_line, 3],
        np.rint(
            220.0 * final_line_mask[final_line].astype(np.float32) / 255.0
        ).astype(np.uint8),
    )
    edit_support |= restore | final_line
    source_layer_edits = restore | final_line

    # At the shortest loop endpoints the compact baseline exposes a rectangular
    # bite below the gripping hand.  Fill only that bite with same-coordinate
    # authored pixels under a tilted elliptical boundary.  The bridge never
    # reaches the retired flat row and introduces no generated RGB.
    if frame_number in ENDPOINT_BRIDGE_FRAMES:
        dx = (xx.astype(np.float32) - 25.0) / 17.0
        shifted_y = yy.astype(np.float32) - 0.25 * (
            xx.astype(np.float32) - 25.0
        )
        dy = (shifted_y - 399.0) / 18.0
        endpoint_bridge = (
            (dx * dx + dy * dy <= 1.0)
            & (yy >= 390)
            & (yy <= 417)
            & (xx <= 34)
            & (authored_source[..., 3] > 0)
            & (output[..., 3] == 0)
            & ~final_protected
        )
        output[endpoint_bridge] = authored_source[endpoint_bridge]
        edit_support |= endpoint_bridge
        source_layer_edits |= endpoint_bridge

    # The full-reveal authored key contains two premultiplied-resample specks
    # in the vacated cuff field.  They are absent from the accepted key and are
    # not part of either protected hand component.
    if frame_number == 24:
        full_reveal_specks = np.zeros((height, width), dtype=bool)
        full_reveal_specks[402, 17] = True
        full_reveal_specks[403, 19] = True
        output[full_reveal_specks] = 0
        final_protected &= ~full_reveal_specks
        edit_support |= full_reveal_specks

    # Match the asset installer's alpha-preserving despill before its second,
    # now-idempotent pass.  Restrict it to restored source pixels so untouched
    # character art remains byte-for-byte identical.
    red = output[..., 0]
    green = output[..., 1]
    blue = output[..., 2]
    despill = (
        source_layer_edits
        & (output[..., 3] > 0)
        & (green > red)
        & (green > blue)
    )
    output[despill, 1] = np.maximum(red[despill], blue[despill])

    output[output[..., 3] == 0, :3] = 0

    changed = np.any(frame != output, axis=2)
    if np.any(changed & final_protected):
        raise AssertionError("compact side grip changed protected character pixels")
    if np.any(changed & ~edit_support):
        raise AssertionError("compact side grip changed pixels outside its sleeve support")
    if np.any((output[..., 3] == 0) & np.any(output[..., :3] != 0, axis=2)):
        raise AssertionError("compact side grip introduced dirty transparent RGB")

    return output, {
        "frame": frame_number,
        "phase": side_grip_phase(frame_number),
        "xEnd": final_x_end,
        "changedPixels": int(changed.sum()),
        "protectedChanges": int(np.count_nonzero(changed & final_protected)),
        "outsideSupportChanges": int(np.count_nonzero(changed & ~edit_support)),
        "removedRepeatedPrefixPixels": int(repeated_prefix.sum()),
    }


def reshape_side_grip_image(
    image: Image.Image, frame_number: int
) -> tuple[Image.Image, dict[str, object]]:
    frame = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    output, metrics = reshape_side_grip_frame(frame, frame_number)
    return Image.fromarray(output, "RGBA"), metrics


def alpha_iou(first: np.ndarray, second: np.ndarray) -> float:
    first_mask = first[ROI[1] : ROI[3], ROI[0] : ROI[2], 3] >= ALPHA_THRESHOLD
    second_mask = second[ROI[1] : ROI[3], ROI[0] : ROI[2], 3] >= ALPHA_THRESHOLD
    union = int(np.count_nonzero(first_mask | second_mask))
    return (
        float(np.count_nonzero(first_mask & second_mask) / union)
        if union
        else 1.0
    )


def roi_alpha_area(frame: np.ndarray) -> int:
    return int(
        np.count_nonzero(
            frame[ROI[1] : ROI[3], ROI[0] : ROI[2], 3] >= ALPHA_THRESHOLD
        )
    )


def endpoint_bridge_alpha_area(frame: np.ndarray) -> int:
    """Measure the once-missing rectangular wrist-to-sleeve connection."""

    return int(np.count_nonzero(frame[399:419, 19:35, 3] >= ALPHA_THRESHOLD))


def wrist_center(frame: np.ndarray) -> tuple[float, float]:
    height, width = frame.shape[:2]
    yy, xx = np.mgrid[:height, :width]
    # Use one fixed visible wrist/hand window. Component labels can merge with
    # the cheek on in-between RIFE frames even though the pixels move smoothly.
    wrist = (
        skin_mask(frame)
        & (xx <= 45)
        & (yy >= 345)
        & (yy <= 416)
    )
    ys, xs = np.nonzero(wrist)
    if not len(xs):
        raise AssertionError("side-grip lower hand flesh component is missing")
    return float(xs.mean()), float(ys.mean())


def maximum_quantized_horizontal_run(frame_number: int) -> int:
    xx = np.arange(ROI[2])[None, :]
    curve, x_end = side_grip_bottom_curve(frame_number, xx)
    runs = [
        np.rint(curve[0, 28 : int(math.floor(x_end)) + 1]).astype(np.int32)
    ]
    if frame_number in ENDPOINT_BRIDGE_FRAMES:
        bridge_x = np.arange(8, 29, dtype=np.float32)
        bridge_dx = (bridge_x - 25.0) / 17.0
        bridge_bottom = (
            399.0
            + 0.25 * (bridge_x - 25.0)
            + 18.0 * np.sqrt(np.clip(1.0 - bridge_dx * bridge_dx, 0.0, 1.0))
        )
        runs.append(np.rint(bridge_bottom).astype(np.int32))

    maximum = 1
    for values in runs:
        current = 1
        for first, second in zip(values, values[1:]):
            if int(first) == int(second):
                current += 1
                maximum = max(maximum, current)
            else:
                current = 1
    return maximum


def analyze_sequence(frames: list[np.ndarray]) -> dict[str, object]:
    if len(frames) != FRAME_COUNT:
        raise AssertionError(
            f"compact side-grip count {len(frames)} != {FRAME_COUNT}"
        )
    hashes = [pixel_sha256(frame) for frame in frames]
    cyclic = [*frames, frames[0]]
    areas = [roi_alpha_area(frame) for frame in frames]
    cyclic_areas = [*areas, areas[0]]
    wrists = [wrist_center(frame) for frame in frames]
    cyclic_wrists = [*wrists, wrists[0]]
    x_ends = [
        side_grip_bottom_curve(number, np.zeros((1, 1), dtype=np.float32))[1]
        for number in range(1, FRAME_COUNT + 1)
    ]
    cyclic_x_ends = [*x_ends, x_ends[0]]
    metrics = {
        "contract": CONTRACT_NAME,
        "frameCount": len(frames),
        "uniqueFrames": len(set(hashes)),
        "decodedSequenceSha256": sequence_sha256(frames),
        "minAdjacentRoiAlphaIou": min(
            alpha_iou(first, second)
            for first, second in zip(cyclic, cyclic[1:])
        ),
        "maxAdjacentRoiAreaChangeRatio": max(
            abs(second - first) / max((first + second) / 2.0, 1.0)
            for first, second in zip(cyclic_areas, cyclic_areas[1:])
        ),
        "maxAdjacentWristCenterStep": max(
            math.hypot(second[0] - first[0], second[1] - first[1])
            for first, second in zip(cyclic_wrists, cyclic_wrists[1:])
        ),
        "maxTargetEndpointStep": max(
            abs(second - first)
            for first, second in zip(cyclic_x_ends, cyclic_x_ends[1:])
        ),
        "maxQuantizedHorizontalBottomRun": max(
            maximum_quantized_horizontal_run(number)
            for number in range(1, FRAME_COUNT + 1)
        ),
        "minEndpointBridgeAlphaPixels": min(
            endpoint_bridge_alpha_area(frames[number - 1])
            for number in sorted(ENDPOINT_BRIDGE_FRAMES)
        ),
        "rightEdgeRuntimeContract": "horizontal-mirror-of-left",
        "rightMirroredDecodedSequenceSha256": sequence_sha256(
            [np.ascontiguousarray(frame[:, ::-1]) for frame in frames]
        ),
    }
    failures: list[str] = []
    if metrics["uniqueFrames"] != FRAME_COUNT:
        failures.append("all 48 side-grip frames must be unique")
    if metrics["minAdjacentRoiAlphaIou"] < MIN_ADJACENT_ROI_ALPHA_IOU:
        failures.append("side-grip adjacent ROI alpha IoU is below 0.94")
    if metrics["maxAdjacentRoiAreaChangeRatio"] > MAX_ADJACENT_ROI_AREA_CHANGE:
        failures.append("side-grip adjacent ROI area change exceeds 4%")
    if metrics["maxAdjacentWristCenterStep"] > MAX_ADJACENT_WRIST_CENTER_STEP:
        failures.append("side-grip wrist center moves by more than 2px")
    if metrics["maxTargetEndpointStep"] > 2.0:
        failures.append("side-grip curved endpoint moves by more than 2px")
    if metrics["maxQuantizedHorizontalBottomRun"] > MAX_HORIZONTAL_BOTTOM_RUN:
        failures.append("side-grip bottom contains a horizontal run over 6px")
    if metrics["minEndpointBridgeAlphaPixels"] < MIN_ENDPOINT_BRIDGE_ALPHA_PIXELS:
        failures.append("side-grip endpoint wrist bridge has a transparent bite")
    metrics["failures"] = failures
    return metrics


def install_compact_side_grip_keys(
    assets_directory: Path = ASSETS,
) -> list[dict[str, object]]:
    metrics: list[dict[str, object]] = []
    for key_number, frame_number in KEY_PHASE_FRAMES.items():
        path = assets_directory / f"luban-edge-left-{key_number:02d}.png"
        frame = load_pixels(path)
        output, frame_metrics = reshape_side_grip_frame(frame, frame_number)
        if not np.array_equal(frame, output):
            save_png_atomically(output, path)
        metrics.append(frame_metrics)
    return metrics


def install_compact_side_grip_smooth(
    assets_directory: Path = ASSETS,
) -> tuple[list[dict[str, object]], dict[str, object]]:
    paths = [
        assets_directory / f"luban-edge-left-smooth-{number:03d}.png"
        for number in range(1, FRAME_COUNT + 1)
    ]
    missing = [path.name for path in paths if not path.is_file()]
    if missing:
        raise FileNotFoundError(f"Missing side-edge frames: {missing[:4]}")
    metrics: list[dict[str, object]] = []
    outputs: list[np.ndarray] = []
    for frame_number, path in enumerate(paths, start=1):
        frame = load_pixels(path)
        output, frame_metrics = reshape_side_grip_frame(frame, frame_number)
        if not np.array_equal(frame, output):
            save_png_atomically(output, path)
        outputs.append(output)
        metrics.append(frame_metrics)
    sequence_metrics = analyze_sequence(outputs)
    if sequence_metrics["failures"]:
        raise AssertionError("; ".join(sequence_metrics["failures"]))
    return metrics, sequence_metrics


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Replace the retired 7px side-arm warp with a compact grip"
    )
    parser.add_argument("--assets-directory", type=Path, default=ASSETS)
    parser.add_argument("--report", type=Path, default=DEFAULT_REPORT_PATH)
    parser.add_argument("--keys-only", action="store_true")
    parser.add_argument("--smooth-only", action="store_true")
    args = parser.parse_args()
    if args.keys_only and args.smooth_only:
        parser.error("--keys-only and --smooth-only are mutually exclusive")

    report: dict[str, object] = {"contract": CONTRACT_NAME}
    if not args.smooth_only:
        report["keys"] = install_compact_side_grip_keys(args.assets_directory)
    if not args.keys_only:
        smooth, sequence = install_compact_side_grip_smooth(args.assets_directory)
        report["smooth"] = smooth
        report["sequence"] = sequence
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
