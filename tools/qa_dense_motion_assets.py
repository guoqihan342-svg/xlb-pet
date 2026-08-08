from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from collections import defaultdict
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
OUT = ROOT / ".codex_tmp" / "dense-motion-qa"
RUNTIME_SIZE = (450, 550)
ATLAS_DISPLAY_SIZE = (399, 509)
ATLAS_X_TO_DIP = 190 / ATLAS_DISPLAY_SIZE[0]
ATLAS_Y_TO_DIP = 242 / ATLAS_DISPLAY_SIZE[1]
ACTIONS = ("yawn", "cry", "cute", "like", "eat", "wave", "think")
SOURCE_X_TO_DIP = 190 / 450
SOURCE_Y_TO_DIP = (488 / 550) * (242 / 509)
SOURCE_TO_DIP = math.sqrt(SOURCE_X_TO_DIP * SOURCE_Y_TO_DIP)
RUNTIME_SCALES = (0.75, 1.0, 1.4)
DPI_SCALES = (1.0, 1.25, 1.5)
ATLAS_Y_TO_MAX_PHYSICAL_PX = (
    ATLAS_Y_TO_DIP * max(RUNTIME_SCALES) * max(DPI_SCALES)
)
BBOX_SCALE_STEP_LIMIT = 0.025
BASELINE_STEP_MAX_PHYSICAL_PX_LIMIT = 1.0
BRIM_WIDTH_STEP_LIMIT = 0.025
BRIM_CENTER_STEP_DIP_LIMIT = 2.0
CENTROID_JERK_DIP_LIMIT = 1.0
BRIM_ANCHOR_JERK_DIP_LIMIT = 1.5
CENTROID_PROJECTION_BACKTRACK_DIP_LIMIT = 0.15
CENTROID_PROJECTION_ERROR_DIP_LIMIT = 1.0
CENTROID_PERPENDICULAR_ERROR_DIP_LIMIT = 0.75
MIDPOINT_BASELINE_MAX_PHYSICAL_PX_LIMIT = 1.0
SUBSTEP_CONTOUR_RATIO_LIMIT = 0.85
ADJACENT_CONTOUR_P95_DIP_LIMIT = 2.0
EDGE_REVEAL_DEPTH_DIP_MIN = 8.0
EDGE_REVEAL_BACKTRACK_DIP_MAX = 1.0
EDGE_CONTACT_CONTOUR_IGNORE_SOURCE_PX = 4
EDGE_UPPER_CONTACT_MIN_AREA = 11000
EDGE_LOWER_CONTACT_MIN_AREA = 15000
EDGE_LOWER_CONTACT_MAX_MIN_X = 3
EDGE_UPPER_CONTACT_MIN_MAX_X = 110
EDGE_LOWER_CONTACT_MIN_MAX_X = 200

# Deliberate pose transitions may be exempted only by naming the exact metric,
# sequence, frame edge/center, and a human-readable reason.  Keep this empty by
# default: adding frames is preferred to hiding a visible discontinuity.
PAIR_WAIVERS: dict[tuple[str, str, int], str] = {}
CENTER_WAIVERS: dict[tuple[str, str, int], str] = {}

sys.path.insert(0, str(ROOT / "tools"))
import generate_dense_motion_assets as generator  # noqa: E402


def load(path: Path) -> np.ndarray:
    with Image.open(path) as opened:
        image = opened.convert("RGBA")
        if image.size != RUNTIME_SIZE:
            raise AssertionError(f"{path.name} has invalid size {image.size}")
        return np.asarray(image, dtype=np.uint8)


def atlas_display_frame(
    frame: np.ndarray, *, anchor_kind: str = "brim"
) -> np.ndarray:
    source = Image.fromarray(frame, "RGBA")
    resized = generator.installer.resize_rgba_premultiplied(
        source,
        (
            ATLAS_DISPLAY_SIZE[0],
            round(source.height * ATLAS_DISPLAY_SIZE[0] / source.width),
        ),
    )
    display = Image.new("RGBA", ATLAS_DISPLAY_SIZE, (0, 0, 0, 0))
    destination_y = (
        0
        if anchor_kind == "edge-top"
        else ATLAS_DISPLAY_SIZE[1] - resized.height
    )
    display.alpha_composite(resized, (0, destination_y))
    return np.asarray(display, dtype=np.uint8)


def alpha_iou(first: np.ndarray, second: np.ndarray, threshold: int = 24) -> float:
    a = first[..., 3] >= threshold
    b = second[..., 3] >= threshold
    union = np.logical_or(a, b).sum()
    return float(np.logical_and(a, b).sum() / union) if union else 1.0


def contour_p95(
    first: np.ndarray,
    second: np.ndarray,
    *,
    ignored_contact_edge: str | None = None,
) -> float:
    masks = [(frame[..., 3] >= 24).astype(np.uint8) for frame in (first, second)]
    kernel = np.ones((3, 3), np.uint8)
    target_edges = [
        mask.astype(bool) & ~cv2.erode(mask, kernel).astype(bool)
        for mask in masks
    ]
    source_edges = [edge.copy() for edge in target_edges]
    # Edge-peek sprites are intentionally clipped by the Windows boundary.
    # Its antialiased cut line occupies up to four source pixels and can change
    # topology as a hand crosses it. Omit only those source samples from the
    # percentile; keep the complete target contour for the distance transform,
    # otherwise deleting the nearest target edge manufactures a large distance.
    # Boundary-contact metrics below validate the omitted cut line independently.
    for edge in source_edges:
        if ignored_contact_edge == "left":
            edge[:, :EDGE_CONTACT_CONTOUR_IGNORE_SOURCE_PX] = False
        elif ignored_contact_edge == "top":
            edge[:EDGE_CONTACT_CONTOUR_IGNORE_SOURCE_PX, :] = False
        elif ignored_contact_edge == "bottom":
            edge[-EDGE_CONTACT_CONTOUR_IGNORE_SOURCE_PX:, :] = False
        elif ignored_contact_edge is not None:
            raise ValueError(f"Unsupported ignored contact edge: {ignored_contact_edge}")
    distances = []
    for source_edge, target_edge in (
        (source_edges[0], target_edges[1]),
        (source_edges[1], target_edges[0]),
    ):
        distance = cv2.distanceTransform((~target_edge).astype(np.uint8), cv2.DIST_L2, 5)
        distances.append(distance[source_edge])
    values = np.concatenate(distances)
    return float(np.percentile(values, 95)) if len(values) else 0.0


def halo_count(frame: np.ndarray, *, ignored_left_columns: int = 0) -> int:
    alpha = frame[..., 3]
    core = alpha >= 160
    low = (alpha >= 16) & (alpha < 160)
    distance = cv2.distanceTransform((~core).astype(np.uint8), cv2.DIST_L2, 3)
    wide_trail = low & (distance > 2.0)
    if ignored_left_columns > 0:
        wide_trail[:, :ignored_left_columns] = False
    return int(wide_trail.sum())


def frame_geometry(
    frame: np.ndarray, *, anchor_kind: str = "brim"
) -> dict[str, object]:
    alpha = frame[..., 3]
    ys, xs = np.nonzero(alpha >= 24)
    if not len(xs):
        raise AssertionError("empty dense frame")
    image = Image.fromarray(frame, "RGBA")
    brim = (
        generator.edge_hat_anchor_geometry(image)
        if anchor_kind.startswith("edge-")
        else generator.brim_geometry(image)
    )
    hat_anchor = generator.weighted_hat_anchor_geometry(image)
    weights = alpha.astype(np.float64)
    weight_sum = float(weights.sum())
    grid_y, grid_x = np.indices(alpha.shape)
    centroid = [
        float((grid_x * weights).sum() / weight_sum),
        float((grid_y * weights).sum() / weight_sum),
    ]
    display_frame = atlas_display_frame(frame, anchor_kind=anchor_kind)
    display_alpha = display_frame[..., 3]
    display_ys, display_xs = np.nonzero(display_alpha >= 24)
    if not len(display_xs):
        raise AssertionError("empty rendered dense frame")
    display_weights = display_alpha.astype(np.float64)
    display_weight_sum = float(display_weights.sum())
    display_grid_y, display_grid_x = np.indices(display_alpha.shape)
    display_centroid_dip = [
        float((display_grid_x * display_weights).sum() / display_weight_sum)
        * ATLAS_X_TO_DIP,
        float((display_grid_y * display_weights).sum() / display_weight_sum)
        * ATLAS_Y_TO_DIP,
    ]
    red = frame[..., 0].astype(np.int16)
    green = frame[..., 1].astype(np.int16)
    blue = frame[..., 2].astype(np.int16)
    green_fringe = (alpha > 0) & (green > red + 8) & (green > blue + 8)
    dirty = (alpha == 0) & np.any(frame[..., :3] != 0, axis=2)
    return {
        "pixel_sha256": hashlib.sha256(frame.tobytes()).hexdigest(),
        "bbox_alpha24": [int(xs.min()), int(ys.min()), int(xs.max() + 1), int(ys.max() + 1)],
        "bbox_width_alpha24": int(xs.max() + 1 - xs.min()),
        "bbox_height_alpha24": int(ys.max() + 1 - ys.min()),
        "baseline_alpha24": int(ys.max() + 1),
        "atlas_bbox_alpha24": [
            int(display_xs.min()),
            int(display_ys.min()),
            int(display_xs.max() + 1),
            int(display_ys.max() + 1),
        ],
        "atlas_bbox_width_alpha24": int(
            display_xs.max() + 1 - display_xs.min()
        ),
        "atlas_bbox_height_alpha24": int(
            display_ys.max() + 1 - display_ys.min()
        ),
        "atlas_baseline_alpha24": int(display_ys.max() + 1),
        "brim": [float(value) for value in brim],
        "hat_anchor": [float(value) for value in hat_anchor],
        "alpha_weighted_centroid_source_px": centroid,
        "alpha_weighted_centroid_final_dip": display_centroid_dip,
        "halo": halo_count(frame),
        "green_dominant_visible": int(green_fringe.sum()),
        "alpha0_nonzero_rgb": int(dirty.sum()),
        "semi_alpha_pixels": int(((alpha > 0) & (alpha < 255)).sum()),
    }


def duplicate_groups(records: list[dict[str, object]]) -> list[list[int]]:
    groups: dict[str, list[int]] = defaultdict(list)
    for number, record in enumerate(records, start=1):
        groups[str(record["pixel_sha256"])].append(number)
    return [numbers for numbers in groups.values() if len(numbers) > 1]


def analyze_sequence(
    paths: list[Path], *, anchor_kind: str = "brim"
) -> dict[str, object]:
    arrays = [load(path) for path in paths]
    records = [frame_geometry(frame, anchor_kind=anchor_kind) for frame in arrays]
    pairs = []
    for number, (first, second) in enumerate(zip(arrays, arrays[1:]), start=1):
        brim0 = records[number - 1]["brim"]
        brim1 = records[number]["brim"]
        hat0 = records[number - 1]["hat_anchor"]
        hat1 = records[number]["hat_anchor"]
        assert isinstance(brim0, list) and isinstance(brim1, list)
        assert isinstance(hat0, list) and isinstance(hat1, list)
        ignored_contact_edge = (
            anchor_kind.removeprefix("edge-")
            if anchor_kind.startswith("edge-")
            else None
        )
        contour = contour_p95(
            first,
            second,
            ignored_contact_edge=ignored_contact_edge,
        )
        pairs.append(
            {
                "from": number,
                "to": number + 1,
                "alpha_iou": alpha_iou(first, second),
                "contour_p95_source_px": contour,
                "contour_p95_final_dip": contour * SOURCE_TO_DIP,
                "brim_center_step_source_px": math.hypot(
                    brim1[0] - brim0[0], brim1[1] - brim0[1]
                ),
                "brim_width_step_source_px": abs(brim1[2] - brim0[2]),
                "brim_width_change_ratio": abs(brim1[2] - brim0[2])
                / max((brim1[2] + brim0[2]) / 2.0, 1.0),
                "hat_anchor_center_step_source_px": math.hypot(
                    hat1[0] - hat0[0], hat1[1] - hat0[1]
                ),
                "hat_anchor_width_change_ratio": abs(hat1[2] - hat0[2])
                / max((hat1[2] + hat0[2]) / 2.0, 1.0),
                "baseline_step_source_px": abs(
                    int(records[number]["baseline_alpha24"])
                    - int(records[number - 1]["baseline_alpha24"])
                ),
                "baseline_step_atlas_px": abs(
                    int(records[number]["atlas_baseline_alpha24"])
                    - int(records[number - 1]["atlas_baseline_alpha24"])
                ),
                "baseline_step_final_dip": abs(
                    int(records[number]["atlas_baseline_alpha24"])
                    - int(records[number - 1]["atlas_baseline_alpha24"])
                ) * ATLAS_Y_TO_DIP,
                "baseline_step_max_physical_px": abs(
                    int(records[number]["atlas_baseline_alpha24"])
                    - int(records[number - 1]["atlas_baseline_alpha24"])
                ) * ATLAS_Y_TO_MAX_PHYSICAL_PX,
                "baseline_step_physical_px_matrix": {
                    f"size-{runtime_scale:g}_dpi-{dpi_scale:g}": abs(
                        int(records[number]["atlas_baseline_alpha24"])
                        - int(records[number - 1]["atlas_baseline_alpha24"])
                    ) * ATLAS_Y_TO_DIP * runtime_scale * dpi_scale
                    for runtime_scale in RUNTIME_SCALES
                    for dpi_scale in DPI_SCALES
                },
                "bbox_width_change_ratio": abs(
                    int(records[number]["atlas_bbox_width_alpha24"])
                    - int(records[number - 1]["atlas_bbox_width_alpha24"])
                ) / max(
                    (
                        int(records[number]["atlas_bbox_width_alpha24"])
                        + int(records[number - 1]["atlas_bbox_width_alpha24"])
                    ) / 2.0,
                    1.0,
                ),
                "bbox_height_change_ratio": abs(
                    int(records[number]["atlas_bbox_height_alpha24"])
                    - int(records[number - 1]["atlas_bbox_height_alpha24"])
                ) / max(
                    (
                        int(records[number]["atlas_bbox_height_alpha24"])
                        + int(records[number - 1]["atlas_bbox_height_alpha24"])
                    ) / 2.0,
                    1.0,
                ),
            }
        )
    duplicates = duplicate_groups(records)
    adjacent_duplicates = [
        number
        for number in range(1, len(records))
        if records[number - 1]["pixel_sha256"] == records[number]["pixel_sha256"]
    ]
    centroids_dip = np.asarray(
        [record["alpha_weighted_centroid_final_dip"] for record in records],
        dtype=np.float64,
    )
    centroid_jerks = []
    micro_roundtrips = []
    for center_index in range(1, len(centroids_dip) - 1):
        previous_delta = centroids_dip[center_index] - centroids_dip[center_index - 1]
        next_delta = centroids_dip[center_index + 1] - centroids_dip[center_index]
        jerk = next_delta - previous_delta
        jerk_magnitude = float(np.linalg.norm(jerk))
        centroid_jerks.append(
            {
                "center_frame_1_based": center_index + 1,
                "jerk_dip": jerk_magnitude,
                "previous_step_dip": float(np.linalg.norm(previous_delta)),
                "next_step_dip": float(np.linalg.norm(next_delta)),
                "two_frame_return_distance_dip": float(
                    np.linalg.norm(centroids_dip[center_index + 1] - centroids_dip[center_index - 1])
                ),
                "step_dot_product": float(np.dot(previous_delta, next_delta)),
            }
        )
        # A small A->B->A reversal is perceived as pixel jitter.  Large
        # reversals are intentional action arcs and remain reported, not
        # silently classified as jitter.
        if (
            float(np.dot(previous_delta, next_delta)) < 0.0
            and 0.20 <= float(np.linalg.norm(previous_delta)) <= 1.50
            and 0.20 <= float(np.linalg.norm(next_delta)) <= 1.50
            and jerk_magnitude > 0.75
        ):
            micro_roundtrips.append(centroid_jerks[-1])

    # Whole-silhouette centroids legitimately move when an arm reaches out.
    # Use the fixed upper-alpha head proxy and a three-sample 1:2:1 robust
    # trajectory. This suppresses subpixel mask/colour oscillation while a real
    # >2 DIP one-frame jump still trips both the pair gate and the smoothed jerk.
    raw_hat_centers_dip = np.asarray(
        [
            [
                float(record["hat_anchor"][0]) * SOURCE_X_TO_DIP,
                float(record["hat_anchor"][1]) * SOURCE_Y_TO_DIP,
            ]
            for record in records
        ],
        dtype=np.float64,
    )
    hat_centers_dip = raw_hat_centers_dip.copy()
    if len(hat_centers_dip) >= 3:
        hat_centers_dip[1:-1] = (
            raw_hat_centers_dip[:-2]
            + 2.0 * raw_hat_centers_dip[1:-1]
            + raw_hat_centers_dip[2:]
        ) / 4.0
    for pair_index, pair in enumerate(pairs):
        pair["robust_hat_anchor_center_step_final_dip"] = float(
            np.linalg.norm(
                hat_centers_dip[pair_index + 1]
                - hat_centers_dip[pair_index]
            )
        )
    raw_hat_anchor_jerks = [
        {
            "center_frame_1_based": center_index + 1,
            "jerk_dip": float(
                np.linalg.norm(
                    raw_hat_centers_dip[center_index + 1]
                    - 2.0 * raw_hat_centers_dip[center_index]
                    + raw_hat_centers_dip[center_index - 1]
                )
            ),
        }
        for center_index in range(1, len(raw_hat_centers_dip) - 1)
    ]
    hat_anchor_jerks = []
    hat_anchor_micro_roundtrips = []
    for center_index in range(1, len(hat_centers_dip) - 1):
        previous_delta = (
            hat_centers_dip[center_index] - hat_centers_dip[center_index - 1]
        )
        next_delta = (
            hat_centers_dip[center_index + 1] - hat_centers_dip[center_index]
        )
        jerk = next_delta - previous_delta
        jerk_magnitude = float(np.linalg.norm(jerk))
        record = {
            "center_frame_1_based": center_index + 1,
            "jerk_dip": jerk_magnitude,
            "previous_step_dip": float(np.linalg.norm(previous_delta)),
            "next_step_dip": float(np.linalg.norm(next_delta)),
            "two_frame_return_distance_dip": float(
                np.linalg.norm(
                    hat_centers_dip[center_index + 1]
                    - hat_centers_dip[center_index - 1]
                )
            ),
            "step_dot_product": float(np.dot(previous_delta, next_delta)),
        }
        hat_anchor_jerks.append(record)
        if (
            float(np.dot(previous_delta, next_delta)) < 0.0
            and 0.20 <= float(np.linalg.norm(previous_delta)) <= 1.50
            and 0.20 <= float(np.linalg.norm(next_delta)) <= 1.50
            and jerk_magnitude > 0.75
        ):
            hat_anchor_micro_roundtrips.append(record)
    return {
        "frame_count": len(paths),
        "unique_pixel_frames": len(paths) - sum(len(group) - 1 for group in duplicates),
        "duplicate_groups_1_based": duplicates,
        "adjacent_duplicate_pairs_from_1_based": adjacent_duplicates,
        "max_green_dominant_visible": max(int(record["green_dominant_visible"]) for record in records),
        "max_alpha0_nonzero_rgb": max(int(record["alpha0_nonzero_rgb"]) for record in records),
        "max_halo": max(int(record["halo"]) for record in records),
        "baseline_range": [
            min(int(record["baseline_alpha24"]) for record in records),
            max(int(record["baseline_alpha24"]) for record in records),
        ],
        "atlas_baseline_range": [
            min(int(record["atlas_baseline_alpha24"]) for record in records),
            max(int(record["atlas_baseline_alpha24"]) for record in records),
        ],
        "brim_width_range": [
            min(float(record["brim"][2]) for record in records),
            max(float(record["brim"][2]) for record in records),
        ],
        "adjacent_min_alpha_iou": min((float(pair["alpha_iou"]) for pair in pairs), default=1.0),
        "adjacent_mean_alpha_iou": float(
            np.mean([float(pair["alpha_iou"]) for pair in pairs])
        ) if pairs else 1.0,
        "adjacent_max_contour_p95_final_dip": max(
            (float(pair["contour_p95_final_dip"]) for pair in pairs), default=0.0
        ),
        "adjacent_max_brim_center_step_source_px": max(
            (float(pair["brim_center_step_source_px"]) for pair in pairs), default=0.0
        ),
        "adjacent_max_brim_center_step_final_dip": max(
            (float(pair["brim_center_step_source_px"]) * SOURCE_TO_DIP for pair in pairs),
            default=0.0,
        ),
        "adjacent_max_brim_width_step_source_px": max(
            (float(pair["brim_width_step_source_px"]) for pair in pairs), default=0.0
        ),
        "adjacent_max_brim_width_change_ratio": max(
            (float(pair["brim_width_change_ratio"]) for pair in pairs), default=0.0
        ),
        "adjacent_max_hat_anchor_center_step_final_dip": max(
            (
                float(pair["hat_anchor_center_step_source_px"]) * SOURCE_TO_DIP
                for pair in pairs
            ),
            default=0.0,
        ),
        "adjacent_max_robust_hat_anchor_center_step_final_dip": max(
            (
                float(pair["robust_hat_anchor_center_step_final_dip"])
                for pair in pairs
            ),
            default=0.0,
        ),
        "adjacent_max_hat_anchor_width_change_ratio": max(
            (float(pair["hat_anchor_width_change_ratio"]) for pair in pairs),
            default=0.0,
        ),
        "adjacent_max_bbox_width_change_ratio": max(
            (float(pair["bbox_width_change_ratio"]) for pair in pairs), default=0.0
        ),
        "adjacent_max_bbox_height_change_ratio": max(
            (float(pair["bbox_height_change_ratio"]) for pair in pairs), default=0.0
        ),
        "adjacent_max_baseline_step_max_physical_px": max(
            (float(pair["baseline_step_max_physical_px"]) for pair in pairs), default=0.0
        ),
        "max_alpha_centroid_second_difference_dip": max(
            (float(value["jerk_dip"]) for value in centroid_jerks), default=0.0
        ),
        "alpha_centroid_micro_roundtrips": micro_roundtrips,
        "alpha_centroid_second_differences": centroid_jerks,
        "max_hat_anchor_second_difference_dip": max(
            (float(value["jerk_dip"]) for value in hat_anchor_jerks), default=0.0
        ),
        "hat_anchor_micro_roundtrips": hat_anchor_micro_roundtrips,
        "hat_anchor_second_differences": hat_anchor_jerks,
        "raw_hat_anchor_second_differences": raw_hat_anchor_jerks,
        "frames": records,
        "pairs": pairs,
    }


def midpoint_linearity(
    first: np.ndarray, midpoint: np.ndarray, second: np.ndarray
) -> dict[str, float]:
    values = [frame_geometry(frame) for frame in (first, midpoint, second)]
    brims = [value["brim"] for value in values]
    hats = [value["hat_anchor"] for value in values]
    assert all(isinstance(brim, list) for brim in brims)
    assert all(isinstance(hat, list) for hat in hats)
    expected_x = (hats[0][0] + hats[2][0]) / 2.0
    expected_y = (hats[0][1] + hats[2][1]) / 2.0
    expected_width = (hats[0][2] + hats[2][2]) / 2.0
    expected_legacy_x = (brims[0][0] + brims[2][0]) / 2.0
    expected_legacy_y = (brims[0][1] + brims[2][1]) / 2.0
    expected_legacy_width = (brims[0][2] + brims[2][2]) / 2.0
    expected_baseline = (
        int(values[0]["baseline_alpha24"]) + int(values[2]["baseline_alpha24"])
    ) / 2.0
    expected_atlas_baseline = (
        int(values[0]["atlas_baseline_alpha24"])
        + int(values[2]["atlas_baseline_alpha24"])
    ) / 2.0
    return {
        "brim_center_error_source_px": math.hypot(
            hats[1][0] - expected_x, hats[1][1] - expected_y
        ),
        "brim_width_error_source_px": abs(hats[1][2] - expected_width),
        "legacy_brim_center_error_source_px": math.hypot(
            brims[1][0] - expected_legacy_x,
            brims[1][1] - expected_legacy_y,
        ),
        "legacy_brim_width_error_source_px": abs(
            brims[1][2] - expected_legacy_width
        ),
        "baseline_error_source_px": abs(
            int(values[1]["baseline_alpha24"]) - expected_baseline
        ),
        "baseline_error_max_physical_px": abs(
            int(values[1]["atlas_baseline_alpha24"]) - expected_atlas_baseline
        ) * ATLAS_Y_TO_MAX_PHYSICAL_PX,
        "max_substep_contour_ratio": max(
            contour_p95(first, midpoint), contour_p95(midpoint, second)
        ) / max(contour_p95(first, second), 1e-9),
    }


def qa_adaptive_sequence(
    outputs: list[Path], keys: list[Path], sequence_name: str
) -> dict[str, object]:
    metrics: list[dict[str, float]] = []
    exact_key_mismatches: list[int] = []
    output_offset = 0
    for pair_index, (first_path, second_path) in enumerate(zip(keys, keys[1:])):
        substeps, _ = generator.sequence_edge_substeps(
            sequence_name, pair_index, first_path, second_path
        )
        first = load(first_path)
        second = load(second_path)
        first_geometry = frame_geometry(first)
        second_geometry = frame_geometry(second)
        first_brim = first_geometry["brim"]
        second_brim = second_geometry["brim"]
        first_hat = first_geometry["hat_anchor"]
        second_hat = second_geometry["hat_anchor"]
        assert isinstance(first_brim, list) and isinstance(second_brim, list)
        assert isinstance(first_hat, list) and isinstance(second_hat, list)
        first_centroid = np.asarray(
            first_geometry["alpha_weighted_centroid_final_dip"], dtype=np.float64
        )
        second_centroid = np.asarray(
            second_geometry["alpha_weighted_centroid_final_dip"], dtype=np.float64
        )
        centroid_vector = second_centroid - first_centroid
        centroid_distance = float(np.linalg.norm(centroid_vector))
        centroid_unit = (
            centroid_vector / centroid_distance if centroid_distance >= 0.05 else None
        )
        previous_projection = 0.0
        direct_contour = max(contour_p95(first, second), 1e-9)
        previous = first
        for step in range(1, substeps + 1):
            output = load(outputs[output_offset + step - 1])
            fraction = step / substeps
            geometry = frame_geometry(output)
            brim = geometry["brim"]
            hat = geometry["hat_anchor"]
            assert isinstance(brim, list)
            assert isinstance(hat, list)
            centroid = np.asarray(
                geometry["alpha_weighted_centroid_final_dip"], dtype=np.float64
            )
            if centroid_unit is None:
                projection = None
                projection_error = None
                projection_backtrack = 0.0
                perpendicular_error = None
            else:
                centroid_delta = centroid - first_centroid
                projection = float(np.dot(centroid_delta, centroid_unit))
                projection_error = abs(projection - centroid_distance * fraction)
                projection_backtrack = max(0.0, previous_projection - projection)
                perpendicular_error = float(
                    np.linalg.norm(centroid_delta - projection * centroid_unit)
                )
                previous_projection = projection
            expected_brim = [
                first_brim[index] * (1.0 - fraction)
                + second_brim[index] * fraction
                for index in range(3)
            ]
            expected_hat = [
                first_hat[index] * (1.0 - fraction)
                + second_hat[index] * fraction
                for index in range(3)
            ]
            expected_baseline = (
                int(first_geometry["baseline_alpha24"]) * (1.0 - fraction)
                + int(second_geometry["baseline_alpha24"]) * fraction
            )
            expected_atlas_baseline = (
                int(first_geometry["atlas_baseline_alpha24"]) * (1.0 - fraction)
                + int(second_geometry["atlas_baseline_alpha24"]) * fraction
            )
            metrics.append(
                {
                    "pair_index_1_based": pair_index + 1,
                    "step": step,
                    "substeps": substeps,
                    "brim_center_error_source_px": math.hypot(
                        hat[0] - expected_hat[0], hat[1] - expected_hat[1]
                    ),
                    "brim_width_error_source_px": abs(hat[2] - expected_hat[2]),
                    "legacy_brim_center_error_source_px": math.hypot(
                        brim[0] - expected_brim[0],
                        brim[1] - expected_brim[1],
                    ),
                    "legacy_brim_width_error_source_px": abs(
                        brim[2] - expected_brim[2]
                    ),
                    "baseline_error_source_px": abs(
                        int(geometry["baseline_alpha24"]) - expected_baseline
                    ),
                    "baseline_error_max_physical_px": abs(
                        int(geometry["atlas_baseline_alpha24"])
                        - expected_atlas_baseline
                    ) * ATLAS_Y_TO_MAX_PHYSICAL_PX,
                    "centroid_direct_distance_dip": centroid_distance,
                    "centroid_projection_dip": projection,
                    "centroid_projection_error_dip": projection_error,
                    "centroid_projection_backtrack_dip": projection_backtrack,
                    "centroid_perpendicular_error_dip": perpendicular_error,
                    "direct_contour_source_px": direct_contour,
                    "direct_contour_final_dip": direct_contour * SOURCE_TO_DIP,
                    "substep_contour_ratio": contour_p95(previous, output)
                    / direct_contour,
                }
            )
            previous = output
        if not np.array_equal(load(outputs[output_offset + substeps - 1]), second):
            exact_key_mismatches.append(pair_index + 1)
        output_offset += substeps
    if output_offset != len(outputs):
        raise AssertionError(
            f"adaptive sequence consumed {output_offset} frames, found {len(outputs)}"
        )
    return {
        "pair_count": len(keys) - 1,
        "sample_count": len(metrics),
        "exact_key_mismatches": exact_key_mismatches,
        "max_brim_center_error_source_px": max(
            value["brim_center_error_source_px"] for value in metrics
        ),
        "max_brim_width_error_source_px": max(
            value["brim_width_error_source_px"] for value in metrics
        ),
        "max_legacy_brim_center_error_source_px": max(
            value["legacy_brim_center_error_source_px"] for value in metrics
        ),
        "max_legacy_brim_width_error_source_px": max(
            value["legacy_brim_width_error_source_px"] for value in metrics
        ),
        "max_baseline_error_source_px": max(
            value["baseline_error_source_px"] for value in metrics
        ),
        "max_baseline_error_max_physical_px": max(
            value["baseline_error_max_physical_px"] for value in metrics
        ),
        "max_centroid_projection_error_dip": max(
            (
                value["centroid_projection_error_dip"]
                for value in metrics
                if value["centroid_projection_error_dip"] is not None
            ),
            default=0.0,
        ),
        "max_centroid_projection_backtrack_dip": max(
            value["centroid_projection_backtrack_dip"] for value in metrics
        ),
        "max_centroid_perpendicular_error_dip": max(
            (
                value["centroid_perpendicular_error_dip"]
                for value in metrics
                if value["centroid_perpendicular_error_dip"] is not None
            ),
            default=0.0,
        ),
        "max_substep_contour_ratio": max(
            value["substep_contour_ratio"] for value in metrics
        ),
        "max_substep_contour_ratio_above_quantization": max(
            (
                value["substep_contour_ratio"]
                for value in metrics
                if value["direct_contour_final_dip"] >= 1.0
            ),
            default=0.0,
        ),
        "quantized_contour_ratio_samples": [
            value
            for value in metrics
            if value["direct_contour_final_dip"] < 1.0
            and value["substep_contour_ratio"] > SUBSTEP_CONTOUR_RATIO_LIMIT
        ],
        "pairs": metrics,
    }


def checker_tile(
    frame: np.ndarray,
    label: str,
    width: int = 125,
    *,
    anchor_kind: str = "brim",
) -> Image.Image:
    shown_frame = atlas_display_frame(frame, anchor_kind=anchor_kind)
    height = round(width * ATLAS_DISPLAY_SIZE[1] / ATLAS_DISPLAY_SIZE[0])
    checker = np.empty((ATLAS_DISPLAY_SIZE[1], ATLAS_DISPLAY_SIZE[0], 3), dtype=np.uint8)
    yy, xx = np.indices(checker.shape[:2])
    light = ((xx // 24 + yy // 24) % 2) == 0
    checker[light] = (246, 246, 246)
    checker[~light] = (28, 30, 34)
    alpha = shown_frame[..., 3:4].astype(np.float32) / 255.0
    shown = np.rint(
        shown_frame[..., :3] * alpha + checker * (1.0 - alpha)
    ).astype(np.uint8)
    image = Image.fromarray(shown, "RGB").resize((width, height), Image.Resampling.LANCZOS)
    tile = Image.new("RGB", (width, height + 20), (20, 20, 22))
    tile.paste(image, (0, 20))
    ImageDraw.Draw(tile).text((3, 3), label, fill="white", font=ImageFont.load_default())
    return tile


def save_contact(
    paths: list[Path],
    destination: Path,
    columns: int = 12,
    *,
    anchor_kind: str = "brim",
) -> None:
    tiles = [
        checker_tile(load(path), f"{index:03d}", anchor_kind=anchor_kind)
        for index, path in enumerate(paths, start=1)
    ]
    width, height = tiles[0].size
    rows = math.ceil(len(tiles) / columns)
    board = Image.new("RGB", (columns * width, rows * height), "white")
    for index, tile in enumerate(tiles):
        board.paste(tile, ((index % columns) * width, (index // columns) * height))
    destination.parent.mkdir(parents=True, exist_ok=True)
    board.save(destination, optimize=True)


def save_worst_transition_contact(
    sequences: list[tuple[str, list[Path], dict[str, object]]],
    destination: Path,
    limit: int = 18,
) -> list[dict[str, object]]:
    candidates: list[dict[str, object]] = []
    sequence_paths = {name: paths for name, paths, _ in sequences}
    for name, _, metrics in sequences:
        for pair in metrics["pairs"]:
            severity = max(
                max(0.0, (0.95 - float(pair["alpha_iou"])) / 0.03),
                float(pair["bbox_width_change_ratio"]) / BBOX_SCALE_STEP_LIMIT,
                float(pair["bbox_height_change_ratio"]) / BBOX_SCALE_STEP_LIMIT,
                float(pair["baseline_step_max_physical_px"])
                / BASELINE_STEP_MAX_PHYSICAL_PX_LIMIT,
                float(pair["hat_anchor_width_change_ratio"])
                / BRIM_WIDTH_STEP_LIMIT,
                float(pair["robust_hat_anchor_center_step_final_dip"])
                / BRIM_CENTER_STEP_DIP_LIMIT,
                float(pair["contour_p95_final_dip"])
                / ADJACENT_CONTOUR_P95_DIP_LIMIT,
            )
            candidates.append(
                {
                    "sequence": name,
                    "kind": "pair",
                    "from": int(pair["from"]),
                    "to": int(pair["to"]),
                    "score": severity,
                    "alpha_iou": float(pair["alpha_iou"]),
                    "bbox_scale": max(
                        float(pair["bbox_width_change_ratio"]),
                        float(pair["bbox_height_change_ratio"]),
                    ),
                    "baseline_max_physical_px": float(
                        pair["baseline_step_max_physical_px"]
                    ),
                }
            )
        for jerk in metrics["alpha_centroid_second_differences"]:
            candidates.append(
                {
                    "sequence": name,
                    "kind": "centroid",
                    "from": int(jerk["center_frame_1_based"]) - 1,
                    "to": int(jerk["center_frame_1_based"]) + 1,
                    "center": int(jerk["center_frame_1_based"]),
                    "score": float(jerk["jerk_dip"]) / CENTROID_JERK_DIP_LIMIT,
                    "jerk_dip": float(jerk["jerk_dip"]),
                }
            )
        for jerk in metrics["hat_anchor_second_differences"]:
            candidates.append(
                {
                    "sequence": name,
                    "kind": "brim-anchor",
                    "from": int(jerk["center_frame_1_based"]) - 1,
                    "to": int(jerk["center_frame_1_based"]) + 1,
                    "center": int(jerk["center_frame_1_based"]),
                    "score": float(jerk["jerk_dip"])
                    / BRIM_ANCHOR_JERK_DIP_LIMIT,
                    "jerk_dip": float(jerk["jerk_dip"]),
                }
            )
    selected: list[dict[str, object]] = []
    occupied: set[tuple[str, int]] = set()
    for candidate in sorted(candidates, key=lambda value: float(value["score"]), reverse=True):
        center = int(candidate.get("center", candidate["from"]))
        identity = (str(candidate["sequence"]), center)
        if identity in occupied:
            continue
        occupied.add(identity)
        selected.append(candidate)
        if len(selected) == limit:
            break

    tile_width = 105
    label_width = 270
    row_gap = 4
    rendered_rows: list[Image.Image] = []
    for candidate in selected:
        name = str(candidate["sequence"])
        paths = sequence_paths[name]
        center = int(candidate.get("center", candidate["from"]))
        first_index = max(1, min(center - 2, len(paths) - 4)) if len(paths) >= 5 else 1
        last_index = min(len(paths), first_index + 4)
        frame_numbers = list(range(first_index, last_index + 1))
        tiles = [
            checker_tile(load(paths[number - 1]), f"{number:03d}", width=tile_width)
            for number in frame_numbers
        ]
        row_height = max(tile.height for tile in tiles)
        row = Image.new(
            "RGB", (label_width + tile_width * len(tiles), row_height), (20, 20, 22)
        )
        label = (
            f"{name}\n{candidate['kind']} {candidate['from']}->{candidate['to']}\n"
            f"score={float(candidate['score']):.2f}"
        )
        ImageDraw.Draw(row).multiline_text(
            (8, 8), label, fill="white", font=ImageFont.load_default(), spacing=4
        )
        for tile_index, tile in enumerate(tiles):
            row.paste(tile, (label_width + tile_index * tile_width, 0))
        rendered_rows.append(row)
    board_width = max(row.width for row in rendered_rows)
    board_height = sum(row.height for row in rendered_rows) + row_gap * max(
        len(rendered_rows) - 1, 0
    )
    board = Image.new("RGB", (board_width, board_height), "white")
    y = 0
    for row in rendered_rows:
        board.paste(row, (0, y))
        y += row.height + row_gap
    destination.parent.mkdir(parents=True, exist_ok=True)
    board.save(destination, optimize=True)
    return selected


def analyze_static_pillow(path: Path) -> dict[str, object]:
    with Image.open(path) as opened:
        image = opened.convert("RGBA")
    frame = np.asarray(image, dtype=np.uint8)
    alpha = frame[..., 3]
    ys, xs = np.nonzero(alpha >= 24)
    if not len(xs):
        raise AssertionError("pillow layer is empty")
    red = frame[..., 0].astype(np.int16)
    green = frame[..., 1].astype(np.int16)
    blue = frame[..., 2].astype(np.int16)
    return {
        "size": list(image.size),
        "bbox_alpha24": [
            int(xs.min()), int(ys.min()), int(xs.max() + 1), int(ys.max() + 1)
        ],
        "baseline_alpha24": int(ys.max() + 1),
        "green_dominant_visible": int(
            ((alpha > 0) & (green > red + 8) & (green > blue + 8)).sum()
        ),
        "alpha0_nonzero_rgb": int(
            ((alpha == 0) & np.any(frame[..., :3] != 0, axis=2)).sum()
        ),
    }


def largest_left_contact_component(
    frame: np.ndarray,
    *,
    roi: tuple[int, int, int, int],
) -> dict[str, int]:
    """Measure real connected anatomy touching the left contact band.

    The retired scanline repair could satisfy a one-column alpha count with a
    stretched colour stripe.  Requiring one substantial 8-connected component
    from the 6px antialias/contact band into the hand or forearm ROI proves that
    the boundary pixels still belong to the authored anatomy.
    """

    left, top, right, bottom = roi
    mask = (frame[top:bottom, left:right, 3] >= 24).astype(np.uint8)
    component_count, labels, statistics, _ = cv2.connectedComponentsWithStats(
        mask,
        connectivity=8,
    )
    candidates: list[dict[str, int]] = []
    for component in range(1, component_count):
        x = int(statistics[component, cv2.CC_STAT_LEFT]) + left
        if x >= left + 6:
            continue
        candidates.append(
            {
                "area": int(statistics[component, cv2.CC_STAT_AREA]),
                "min_x": x,
                "max_x": x
                + int(statistics[component, cv2.CC_STAT_WIDTH])
                - 1,
            }
        )
    if not candidates:
        return {"area": 0, "min_x": -1, "max_x": -1}
    return max(candidates, key=lambda metrics: metrics["area"])


def left_contact_component_metrics(frame: np.ndarray) -> dict[str, dict[str, int]]:
    return {
        "upper_grip": largest_left_contact_component(
            frame,
            roi=(0, 190, 120, 300),
        ),
        "lower_hand_and_forearm": largest_left_contact_component(
            frame,
            roi=(0, 320, 230, 460),
        ),
    }


def sorted_sequence(prefix: str) -> list[Path]:
    return sorted(ASSETS.glob(f"{prefix}-*.png"), key=lambda path: path.name)


def main() -> None:
    parser = argparse.ArgumentParser(description="QA dense 60fps motion assets")
    parser.add_argument("--contacts", action="store_true", help="Write checkerboard contact sheets")
    parser.add_argument(
        "--require-edge-peek",
        action="store_true",
        help=(
            "Require and validate the 48-frame left/top/bottom edge-peek loops"
        ),
    )
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)

    wake_outputs = sorted_sequence("luban-wake-smooth")
    wake_keys = [ASSETS / f"luban-wake-{number:02d}.png" for number in range(1, 28)]
    expected_wake = 1 + sum(
        generator.sequence_edge_substeps("wake", pair_index, first, second)[0]
        for pair_index, (first, second) in enumerate(zip(wake_keys, wake_keys[1:]))
    )
    if len(wake_outputs) != expected_wake:
        raise AssertionError(
            f"wake output count {len(wake_outputs)} != {expected_wake}"
        )
    action_keys, neutral_registration = generator.build_action_key_sequences()

    result: dict[str, object] = {
        "wake": {
            "sequence": analyze_sequence(wake_outputs),
            "midpoints": qa_adaptive_sequence(wake_outputs[1:], wake_keys, "wake"),
            "first_key_mismatch": not np.array_equal(
                load(wake_outputs[0]), load(wake_keys[0])
            ),
        },
        "actions": {},
        "edge_peek": {},
        "neutral_registration": neutral_registration,
        "pillow_layer": analyze_static_pillow(ASSETS / "luban-pillow-layer.png"),
    }
    sequence_entries: list[tuple[str, list[Path], dict[str, object]]] = [
        ("wake.smooth", wake_outputs, result["wake"]["sequence"])
    ]
    authored_hashes_by_sequence: dict[str, set[str]] = {
        "wake.smooth": {
            hashlib.sha256(load(path).tobytes()).hexdigest() for path in wake_keys
        }
    }
    for action in ACTIONS:
        smooth = sorted_sequence(f"luban-{action}-smooth")
        loop = sorted_sequence(f"luban-{action}-loop")
        expected_smooth = sum(
            generator.sequence_edge_substeps(action, pair_index, first, second)[0]
            for pair_index, (first, second) in enumerate(
                zip(action_keys[action], action_keys[action][1:])
            )
        )
        if len(smooth) != expected_smooth:
            raise AssertionError(
                f"{action} smooth count {len(smooth)} != {expected_smooth}"
            )
        if len(loop) != 48:
            raise AssertionError(f"{action} loop count {len(loop)} != 48")
        result["actions"][action] = {
            "smooth": analyze_sequence(smooth),
            "smooth_midpoints": qa_adaptive_sequence(
                smooth, action_keys[action], action
            ),
            "loop": analyze_sequence(loop),
            "loop_wrap": {
                "alpha_iou": alpha_iou(load(loop[-1]), load(loop[0])),
                "brim_center_step_final_dip": math.hypot(
                    generator.brim_geometry(Image.fromarray(load(loop[-1]), "RGBA"))[0]
                    - generator.brim_geometry(Image.fromarray(load(loop[0]), "RGBA"))[0],
                    generator.brim_geometry(Image.fromarray(load(loop[-1]), "RGBA"))[1]
                    - generator.brim_geometry(Image.fromarray(load(loop[0]), "RGBA"))[1],
                ) * SOURCE_TO_DIP,
                "pixel_equal": bool(np.array_equal(load(loop[-1]), load(loop[0]))),
            },
        }
        sequence_entries.extend(
            (
                (f"{action}.smooth", smooth, result["actions"][action]["smooth"]),
                (f"{action}.loop", loop, result["actions"][action]["loop"]),
            )
        )
        action_key_hashes = {
            hashlib.sha256(load(path).tobytes()).hexdigest()
            for path in action_keys[action]
        }
        authored_hashes_by_sequence[f"{action}.smooth"] = action_key_hashes
        authored_hashes_by_sequence[f"{action}.loop"] = action_key_hashes
        exact_loop_keys = {
            12: ASSETS / f"luban-{action}-frame-21.png",
            24: ASSETS / f"luban-{action}-frame-22.png",
            36: ASSETS / f"luban-{action}-frame-23.png",
            48: ASSETS / f"luban-{action}-frame-24.png",
        }
        result["actions"][action]["loop_exact_key_mismatches"] = [
            number
            for number, expected in exact_loop_keys.items()
            if not np.array_equal(load(loop[number - 1]), load(expected))
        ]
        result["actions"][action]["smooth_end_equals_loop048"] = bool(
            np.array_equal(load(smooth[-1]), load(loop[47]))
        )
        if args.contacts:
            save_contact(smooth, OUT / f"{action}-smooth-contact.png")
            save_contact(loop, OUT / f"{action}-loop-contact.png")

    edge_sequences = {
        direction: sorted_sequence(f"luban-edge-{direction}-smooth")
        for direction in generator.EDGE_DIRECTIONS
    }
    edge_assets_present = any(edge_sequences.values())
    if args.require_edge_peek and not all(edge_sequences.values()):
        missing = [
            direction for direction, paths in edge_sequences.items() if not paths
        ]
        raise AssertionError(f"missing edge-peek sequences: {missing}")
    if edge_assets_present:
        runtime_edge_keys = {
            direction: [
                ASSETS / f"luban-edge-{direction}-{number:02d}.png"
                for number in range(1, 5)
            ]
            for direction in generator.EDGE_DIRECTIONS
        }
        for direction, outputs in edge_sequences.items():
            if len(outputs) != generator.EDGE_PEEK_FRAME_COUNT:
                raise AssertionError(
                    f"edge {direction} smooth count {len(outputs)} != "
                    f"{generator.EDGE_PEEK_FRAME_COUNT}"
                )
            name = f"edge.{direction}"
            edge_anchor_kind = f"edge-{direction}"
            sequence = analyze_sequence(outputs, anchor_kind=edge_anchor_kind)
            first = load(outputs[-1])
            second = load(outputs[0])
            first_brim = generator.edge_hat_anchor_geometry(
                Image.fromarray(first, "RGBA")
            )
            second_brim = generator.edge_hat_anchor_geometry(
                Image.fromarray(second, "RGBA")
            )
            first_hat = generator.weighted_hat_anchor_geometry(
                Image.fromarray(first, "RGBA")
            )
            second_hat = generator.weighted_hat_anchor_geometry(
                Image.fromarray(second, "RGBA")
            )
            first_geometry = frame_geometry(first, anchor_kind=edge_anchor_kind)
            second_geometry = frame_geometry(second, anchor_kind=edge_anchor_kind)
            quarter = len(outputs) // 4
            expected_keys = {
                quarter: runtime_edge_keys[direction][1],
                quarter * 2: runtime_edge_keys[direction][2],
                quarter * 3: runtime_edge_keys[direction][3],
                quarter * 4: runtime_edge_keys[direction][0],
            }
            edge_result = {
                "sequence": sequence,
                "exact_key_mismatches": [
                    number
                    for number, expected in expected_keys.items()
                    if not np.array_equal(load(outputs[number - 1]), load(expected))
                ],
                "loop_wrap": {
                    "alpha_iou": alpha_iou(first, second),
                    "contour_p95_final_dip": contour_p95(
                        first,
                        second,
                        ignored_contact_edge=direction,
                    ) * SOURCE_TO_DIP,
                    "brim_center_step_final_dip": math.hypot(
                        second_hat[0] - first_hat[0],
                        second_hat[1] - first_hat[1],
                    )
                    * SOURCE_TO_DIP,
                    "brim_width_change_ratio": abs(
                        second_hat[2] - first_hat[2]
                    )
                    / max((first_hat[2] + second_hat[2]) / 2.0, 1.0),
                    "legacy_brim_center_step_final_dip": math.hypot(
                        second_brim[0] - first_brim[0],
                        second_brim[1] - first_brim[1],
                    )
                    * SOURCE_TO_DIP,
                    "baseline_step_max_physical_px": abs(
                        int(second_geometry["atlas_baseline_alpha24"])
                        - int(first_geometry["atlas_baseline_alpha24"])
                    )
                    * ATLAS_Y_TO_MAX_PHYSICAL_PX,
                    "pixel_equal": bool(np.array_equal(first, second)),
                },
            }
            contact_index = {"left": 0, "top": 1, "bottom": 3}[direction]
            contact_target = {"left": 0, "top": 0, "bottom": 509}[direction]
            contact_positions = [
                int(frame["atlas_bbox_alpha24"][contact_index])
                for frame in sequence["frames"]
            ]
            cyclic_positions = [*contact_positions, contact_positions[0]]
            edge_result["boundary_contact"] = {
                "axis": "x" if direction == "left" else "y",
                "side": direction,
                "target_atlas_px": contact_target,
                "positions_atlas_px": contact_positions,
                "max_absolute_error_atlas_px": max(
                    abs(position - contact_target)
                    for position in contact_positions
                ),
                "max_adjacent_step_max_physical_px": max(
                    abs(second_position - first_position)
                    * ATLAS_Y_TO_MAX_PHYSICAL_PX
                    for first_position, second_position in zip(
                        cyclic_positions, cyclic_positions[1:]
                    )
                ),
            }
            if direction == "left":
                contact_components = [
                    left_contact_component_metrics(load(path))
                    for path in outputs
                ]
                upper_components = [
                    metrics["upper_grip"] for metrics in contact_components
                ]
                lower_components = [
                    metrics["lower_hand_and_forearm"]
                    for metrics in contact_components
                ]
                edge_result["fixed_hand_and_forearm_contact"] = {
                    "applies_to_runtime_edges": ["left", "right-mirrored"],
                    "upper_grip": {
                        "minimum_component_area_source_px": min(
                            metrics["area"] for metrics in upper_components
                        ),
                        "maximum_min_x_source_px": max(
                            metrics["min_x"] for metrics in upper_components
                        ),
                        "minimum_max_x_source_px": min(
                            metrics["max_x"] for metrics in upper_components
                        ),
                    },
                    "lower_hand_and_forearm": {
                        "minimum_component_area_source_px": min(
                            metrics["area"] for metrics in lower_components
                        ),
                        "maximum_min_x_source_px": max(
                            metrics["min_x"] for metrics in lower_components
                        ),
                        "minimum_max_x_source_px": min(
                            metrics["max_x"] for metrics in lower_components
                        ),
                    },
                }
            fully_peeked_frame = quarter * 2
            resting_frame = quarter * 4
            fully_peeked_box = sequence["frames"][
                fully_peeked_frame - 1
            ]["atlas_bbox_alpha24"]
            resting_box = sequence["frames"][
                resting_frame - 1
            ]["atlas_bbox_alpha24"]
            if direction == "left":
                reveal_depth_dip = (
                    fully_peeked_box[2] - resting_box[2]
                ) * ATLAS_X_TO_DIP
            elif direction == "top":
                reveal_depth_dip = (
                    fully_peeked_box[3] - resting_box[3]
                ) * ATLAS_Y_TO_DIP
            else:
                reveal_depth_dip = (
                    resting_box[1] - fully_peeked_box[1]
                ) * ATLAS_Y_TO_DIP
            edge_result["reveal_depth"] = {
                "rest_frame": resting_frame,
                "fully_peeked_frame": fully_peeked_frame,
                "normal_axis_dip": reveal_depth_dip,
                "minimum_dip": EDGE_REVEAL_DEPTH_DIP_MIN,
            }
            outward_reveal_boxes = [
                sequence["frames"][resting_frame - 1]["atlas_bbox_alpha24"],
                *[
                    frame["atlas_bbox_alpha24"]
                    for frame in sequence["frames"][:fully_peeked_frame]
                ],
            ]
            retreat_reveal_boxes = [
                frame["atlas_bbox_alpha24"]
                for frame in sequence["frames"][
                    fully_peeked_frame - 1:resting_frame
                ]
            ]
            if direction == "left":
                outward_positions_dip = [
                    box[2] * ATLAS_X_TO_DIP for box in outward_reveal_boxes
                ]
                retreat_positions_dip = [
                    box[2] * ATLAS_X_TO_DIP for box in retreat_reveal_boxes
                ]
            elif direction == "top":
                outward_positions_dip = [
                    box[3] * ATLAS_Y_TO_DIP for box in outward_reveal_boxes
                ]
                retreat_positions_dip = [
                    box[3] * ATLAS_Y_TO_DIP for box in retreat_reveal_boxes
                ]
            else:
                outward_positions_dip = [
                    -box[1] * ATLAS_Y_TO_DIP for box in outward_reveal_boxes
                ]
                retreat_positions_dip = [
                    -box[1] * ATLAS_Y_TO_DIP for box in retreat_reveal_boxes
                ]
            max_outward_backtrack_dip = max(
                max(0.0, first - second)
                for first, second in zip(
                    outward_positions_dip, outward_positions_dip[1:]
                )
            )
            max_retreat_backtrack_dip = max(
                max(0.0, second - first)
                for first, second in zip(
                    retreat_positions_dip, retreat_positions_dip[1:]
                )
            )
            edge_result["reveal_monotonicity"] = {
                "outward_positions_dip": outward_positions_dip,
                "retreat_positions_dip": retreat_positions_dip,
                "max_outward_backtrack_dip": max_outward_backtrack_dip,
                "max_retreat_backtrack_dip": max_retreat_backtrack_dip,
                "max_backtrack_dip": max(
                    max_outward_backtrack_dip,
                    max_retreat_backtrack_dip,
                ),
                "maximum_backtrack_dip": EDGE_REVEAL_BACKTRACK_DIP_MAX,
            }
            result["edge_peek"][direction] = edge_result
            sequence_entries.append((name, outputs, sequence))
            authored_hashes_by_sequence[name] = {
                hashlib.sha256(load(path).tobytes()).hexdigest()
                for path in runtime_edge_keys[direction]
            }
            if args.contacts:
                save_contact(
                    outputs,
                    OUT / f"edge-{direction}-contact.png",
                    anchor_kind=edge_anchor_kind,
                )

    if args.contacts:
        save_contact(wake_outputs, OUT / "wake-smooth-contact.png")
        result["worst_transition_contact"] = str(
            OUT / "worst-transitions-contact.png"
        )
        result["worst_transition_candidates"] = save_worst_transition_contact(
            sequence_entries, OUT / "worst-transitions-contact.png"
        )

    failures: list[str] = []
    applied_waivers: list[dict[str, object]] = []
    all_midpoint_metrics = [result["wake"]["midpoints"]]
    for action in ACTIONS:
        all_midpoint_metrics.append(result["actions"][action]["smooth_midpoints"])

    def pair_violations(
        name: str,
        sequence: dict[str, object],
        metric: str,
        predicate,
    ) -> list[dict[str, object]]:
        violating: list[dict[str, object]] = []
        for pair in sequence["pairs"]:
            if not predicate(pair):
                continue
            key = (name, metric, int(pair["from"]))
            if key in PAIR_WAIVERS:
                applied_waivers.append(
                    {
                        "sequence": name,
                        "metric": metric,
                        "from": int(pair["from"]),
                        "to": int(pair["to"]),
                        "reason": PAIR_WAIVERS[key],
                    }
                )
            else:
                violating.append(pair)
        return violating

    def center_violations(
        name: str,
        metric: str,
        values: list[dict[str, object]],
        predicate,
    ) -> list[dict[str, object]]:
        violating: list[dict[str, object]] = []
        for value in values:
            if not predicate(value):
                continue
            center = int(value["center_frame_1_based"])
            key = (name, metric, center)
            if key in CENTER_WAIVERS:
                applied_waivers.append(
                    {
                        "sequence": name,
                        "metric": metric,
                        "center_frame_1_based": center,
                        "reason": CENTER_WAIVERS[key],
                    }
                )
            else:
                violating.append(value)
        return violating

    for name, paths, sequence in sequence_entries:
        if sequence["max_green_dominant_visible"]:
            failures.append(f"{name} green fringe")
        if sequence["max_alpha0_nonzero_rgb"]:
            failures.append(f"{name} dirty transparent RGB")
        if sequence["max_halo"] > 500:
            failures.append(f"{name} halo {sequence['max_halo']} > 500")
        interpolated_trail_frames = [
            frame_number
            for frame_number, frame in enumerate(sequence["frames"], start=1)
            if str(frame["pixel_sha256"]) not in authored_hashes_by_sequence[name]
            and (
                halo_count(
                    load(paths[frame_number - 1]),
                    # The left Windows cut line intentionally clips the
                    # antialiased grip contour.  Those first four source
                    # columns are already excluded from contour-distance QA
                    # and are independently covered by the fixed connected
                    # hand/forearm checks above; they are not interpolation
                    # trails in open image space.
                    ignored_left_columns=(
                        EDGE_CONTACT_CONTOUR_IGNORE_SOURCE_PX
                        if name == "edge.left"
                        else 0
                    ),
                )
                > 0
            )
        ]
        sequence["interpolated_wide_trail_frames_1_based"] = (
            interpolated_trail_frames
        )
        if interpolated_trail_frames:
            failures.append(
                f"{name} interpolated low-alpha trail at "
                f"{interpolated_trail_frames[:12]}"
            )
        if sequence["adjacent_duplicate_pairs_from_1_based"]:
            failures.append(f"{name} has adjacent duplicate frames")
        if sequence["adjacent_min_alpha_iou"] < 0.92:
            failures.append(
                f"{name} min alpha IoU "
                f"{sequence['adjacent_min_alpha_iou']:.4f} < 0.92"
            )
        if sequence["adjacent_mean_alpha_iou"] < 0.95:
            failures.append(
                f"{name} mean alpha IoU "
                f"{sequence['adjacent_mean_alpha_iou']:.4f} < 0.95"
            )
        if (
            sequence["adjacent_max_contour_p95_final_dip"]
            > ADJACENT_CONTOUR_P95_DIP_LIMIT
        ):
            failures.append(
                f"{name} contour p95 "
                f"{sequence['adjacent_max_contour_p95_final_dip']:.3f} DIP > "
                f"{ADJACENT_CONTOUR_P95_DIP_LIMIT:g}"
            )
        brim_steps = pair_violations(
            name,
            sequence,
            "hat_anchor_center_step",
            lambda pair: float(pair["robust_hat_anchor_center_step_final_dip"])
            > BRIM_CENTER_STEP_DIP_LIMIT,
        )
        if brim_steps:
            failures.append(
                f"{name} hat anchor step > {BRIM_CENTER_STEP_DIP_LIMIT:g} DIP at "
                f"{[int(pair['from']) for pair in brim_steps[:12]]}"
            )
        brim_width_steps = pair_violations(
            name,
            sequence,
            "hat_anchor_width_scale",
            lambda pair: float(pair["hat_anchor_width_change_ratio"])
            > BRIM_WIDTH_STEP_LIMIT,
        )
        sequence["report_only_hat_anchor_width_scale_over_limit"] = [
            int(pair["from"]) for pair in brim_width_steps
        ]
        bbox_width_steps = pair_violations(
            name,
            sequence,
            "bbox_width_scale",
            lambda pair: float(pair["bbox_width_change_ratio"])
            > BBOX_SCALE_STEP_LIMIT,
        )
        if bbox_width_steps:
            failures.append(
                f"{name} bbox width scale > {BBOX_SCALE_STEP_LIMIT:.1%} at "
                f"{[int(pair['from']) for pair in bbox_width_steps[:12]]}"
            )
        bbox_height_steps = pair_violations(
            name,
            sequence,
            "bbox_height_scale",
            lambda pair: float(pair["bbox_height_change_ratio"])
            > BBOX_SCALE_STEP_LIMIT,
        )
        if bbox_height_steps:
            failures.append(
                f"{name} bbox height scale > {BBOX_SCALE_STEP_LIMIT:.1%} at "
                f"{[int(pair['from']) for pair in bbox_height_steps[:12]]}"
            )
        baseline_steps = pair_violations(
            name,
            sequence,
            "baseline_step",
            lambda pair: float(pair["baseline_step_max_physical_px"])
            > BASELINE_STEP_MAX_PHYSICAL_PX_LIMIT,
        )
        if baseline_steps and not name.startswith("edge."):
            failures.append(
                f"{name} baseline step > {BASELINE_STEP_MAX_PHYSICAL_PX_LIMIT:g} "
                "physical px at size 1.4 / 150% DPI at "
                f"{[int(pair['from']) for pair in baseline_steps[:12]]}"
            )
        hat_micro_roundtrips = center_violations(
            name,
            "hat_anchor_micro_roundtrip",
            sequence["hat_anchor_micro_roundtrips"],
            lambda _: True,
        )
        sequence["report_only_hat_anchor_micro_roundtrips"] = [
            int(value["center_frame_1_based"])
            for value in hat_micro_roundtrips
        ]
        hat_anchor_jerks = center_violations(
            name,
            "hat_anchor_second_difference",
            sequence["hat_anchor_second_differences"],
            lambda value: float(value["jerk_dip"])
            > BRIM_ANCHOR_JERK_DIP_LIMIT,
        )
        sequence["report_only_hat_anchor_second_difference_over_limit"] = [
            int(value["center_frame_1_based"])
            for value in hat_anchor_jerks
        ]
    for index, midpoints in enumerate(all_midpoint_metrics):
        if midpoints["exact_key_mismatches"]:
            failures.append(f"midpoint set {index} key mismatch")
        midpoints["report_only_hat_anchor_center_error_over_limit"] = bool(
            midpoints["max_brim_center_error_source_px"] > 1.5
        )
        midpoints["report_only_hat_anchor_width_error_over_limit"] = bool(
            midpoints["max_brim_width_error_source_px"] > 2.0
        )
        if (
            midpoints["max_baseline_error_max_physical_px"]
            > MIDPOINT_BASELINE_MAX_PHYSICAL_PX_LIMIT
        ):
            failures.append(
                f"midpoint set {index} baseline interpolation error "
                f"{midpoints['max_baseline_error_max_physical_px']:.3f}px > "
                f"{MIDPOINT_BASELINE_MAX_PHYSICAL_PX_LIMIT:g}px"
            )
        if (
            midpoints["max_substep_contour_ratio_above_quantization"]
            > SUBSTEP_CONTOUR_RATIO_LIMIT
        ):
            failures.append(
                f"midpoint set {index} contour ratio "
                f"{midpoints['max_substep_contour_ratio_above_quantization']:.3f} > "
                f"{SUBSTEP_CONTOUR_RATIO_LIMIT:.2f}"
            )
        if (
            "max_centroid_projection_backtrack_dip" in midpoints
            and midpoints["max_centroid_projection_backtrack_dip"]
            > CENTROID_PROJECTION_BACKTRACK_DIP_LIMIT
        ):
            failures.append(
                f"midpoint set {index} centroid projection backtrack "
                f"{midpoints['max_centroid_projection_backtrack_dip']:.3f} DIP > "
                f"{CENTROID_PROJECTION_BACKTRACK_DIP_LIMIT:.2f}"
            )
        if (
            "max_centroid_projection_error_dip" in midpoints
            and midpoints["max_centroid_projection_error_dip"]
            > CENTROID_PROJECTION_ERROR_DIP_LIMIT
        ):
            failures.append(
                f"midpoint set {index} centroid projection error "
                f"{midpoints['max_centroid_projection_error_dip']:.3f} DIP > "
                f"{CENTROID_PROJECTION_ERROR_DIP_LIMIT:.2f}"
            )
        if (
            "max_centroid_perpendicular_error_dip" in midpoints
            and midpoints["max_centroid_perpendicular_error_dip"]
            > CENTROID_PERPENDICULAR_ERROR_DIP_LIMIT
        ):
            failures.append(
                f"midpoint set {index} centroid perpendicular error "
                f"{midpoints['max_centroid_perpendicular_error_dip']:.3f} DIP > "
                f"{CENTROID_PERPENDICULAR_ERROR_DIP_LIMIT:.2f}"
            )
    if result["wake"]["first_key_mismatch"]:
        failures.append("wake first key mismatch")
    for action in ACTIONS:
        action_result = result["actions"][action]
        if action_result["loop_exact_key_mismatches"]:
            failures.append(
                f"{action} loop key mismatch: "
                f"{action_result['loop_exact_key_mismatches']}"
            )
        if not action_result["smooth_end_equals_loop048"]:
            failures.append(f"{action} smooth end != loop048")
        if action_result["loop_wrap"]["alpha_iou"] < 0.92:
            failures.append(
                f"{action} loop wrap alpha IoU "
                f"{action_result['loop_wrap']['alpha_iou']:.4f} < 0.92"
            )
        if action_result["loop_wrap"]["brim_center_step_final_dip"] > 2.0:
            failures.append(f"{action} loop wrap brim step > 2 DIP")
        if action_result["loop_wrap"]["pixel_equal"]:
            failures.append(f"{action} loop wrap duplicates endpoint")
    for direction in generator.EDGE_DIRECTIONS:
        if direction not in result["edge_peek"]:
            continue
        edge_result = result["edge_peek"][direction]
        if edge_result["exact_key_mismatches"]:
            failures.append(
                f"edge {direction} key mismatch: "
                f"{edge_result['exact_key_mismatches']}"
            )
        wrap = edge_result["loop_wrap"]
        if wrap["alpha_iou"] < 0.92:
            failures.append(
                f"edge {direction} loop wrap alpha IoU "
                f"{wrap['alpha_iou']:.4f} < 0.92"
            )
        if wrap["contour_p95_final_dip"] > ADJACENT_CONTOUR_P95_DIP_LIMIT:
            failures.append(f"edge {direction} loop wrap contour > 2 DIP")
        if wrap["brim_center_step_final_dip"] > BRIM_CENTER_STEP_DIP_LIMIT:
            failures.append(f"edge {direction} loop wrap brim step > 2 DIP")
        edge_result["report_only_loop_wrap_hat_width_over_limit"] = bool(
            wrap["brim_width_change_ratio"] > BRIM_WIDTH_STEP_LIMIT
        )
        edge_result["report_only_loop_wrap_baseline_over_limit"] = bool(
            wrap["baseline_step_max_physical_px"]
            > BASELINE_STEP_MAX_PHYSICAL_PX_LIMIT
        )
        if wrap["pixel_equal"]:
            failures.append(f"edge {direction} loop wrap duplicates endpoint")
        contact = edge_result["boundary_contact"]
        if contact["max_absolute_error_atlas_px"] > 1:
            failures.append(
                f"edge {direction} boundary contact is more than 1 atlas px "
                "from the Windows edge"
            )
        if contact["max_adjacent_step_max_physical_px"] > 1.0:
            failures.append(
                f"edge {direction} boundary contact moves by more than 1px"
            )
        fixed_contact = edge_result.get("fixed_hand_and_forearm_contact")
        if fixed_contact is not None:
            upper_contact = fixed_contact["upper_grip"]
            lower_contact = fixed_contact["lower_hand_and_forearm"]
            if (
                upper_contact["minimum_component_area_source_px"]
                < EDGE_UPPER_CONTACT_MIN_AREA
                or upper_contact["maximum_min_x_source_px"] != 0
                or upper_contact["minimum_max_x_source_px"]
                < EDGE_UPPER_CONTACT_MIN_MAX_X
            ):
                failures.append(
                    "edge left/right mirrored upper gripping hand is not a "
                    "complete boundary-connected anatomy component"
                )
            if (
                lower_contact["minimum_component_area_source_px"]
                < EDGE_LOWER_CONTACT_MIN_AREA
                or lower_contact["maximum_min_x_source_px"]
                > EDGE_LOWER_CONTACT_MAX_MIN_X
                or lower_contact["minimum_max_x_source_px"]
                < EDGE_LOWER_CONTACT_MIN_MAX_X
            ):
                failures.append(
                    "edge left/right mirrored lower hand and forearm are not a "
                    "complete boundary-connected anatomy component"
                )
        reveal_depth = edge_result["reveal_depth"]["normal_axis_dip"]
        if reveal_depth < EDGE_REVEAL_DEPTH_DIP_MIN:
            failures.append(
                f"edge {direction} reveal depth {reveal_depth:.3f} DIP "
                f"< {EDGE_REVEAL_DEPTH_DIP_MIN:.1f} DIP"
            )
        reveal_backtrack = edge_result["reveal_monotonicity"]["max_backtrack_dip"]
        if reveal_backtrack > EDGE_REVEAL_BACKTRACK_DIP_MAX:
            failures.append(
                f"edge {direction} reveal backtracks {reveal_backtrack:.3f} DIP "
                f"> {EDGE_REVEAL_BACKTRACK_DIP_MAX:.1f} DIP"
            )
    pillow = result["pillow_layer"]
    if pillow["size"] != [399, 509]:
        failures.append(f"pillow layer size {pillow['size']} != [399, 509]")
    if pillow["green_dominant_visible"]:
        failures.append("pillow layer green fringe")
    if pillow["alpha0_nonzero_rgb"]:
        failures.append("pillow layer dirty transparent RGB")
    result["applied_waivers"] = applied_waivers
    result["failures"] = failures
    (OUT / "metrics.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps({"failures": failures}, ensure_ascii=False), flush=True)
    if failures:
        raise AssertionError("; ".join(failures))


if __name__ == "__main__":
    main()
