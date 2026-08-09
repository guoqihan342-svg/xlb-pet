from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
import cv2
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
WORK_ROOT = ROOT / ".codex_tmp" / "dense-motion-work"
DEFAULT_RIFE_ROOT = (
    ROOT
    / ".codex_tmp"
    / "rife-tool"
    / "expanded"
    / "rife-ncnn-vulkan-20221029-windows"
)
RIFE_ROOT = Path(os.environ.get("XLB_RIFE_ROOT", str(DEFAULT_RIFE_ROOT))).expanduser()
RIFE_EXE = RIFE_ROOT / "rife-ncnn-vulkan.exe"
RIFE_MODEL = RIFE_ROOT / "rife-anime"
RIFE_JOBS = os.environ.get("XLB_RIFE_JOBS", "1:1:1")
if re.fullmatch(r"[1-9]\d*:[1-9]\d*:[1-9]\d*", RIFE_JOBS) is None:
    raise ValueError(f"invalid XLB_RIFE_JOBS={RIFE_JOBS!r}; expected load:proc:save")
RUNTIME_SIZE = (450, 550)
ATLAS_DISPLAY_SIZE = (399, 509)
FINAL_Y_DIP_PER_ATLAS_PX = 242 / ATLAS_DISPLAY_SIZE[1]
SOURCE_X_TO_FINAL_DIP = 190 / 450
# Atlas packing first fits the 450x550 source into a 399x509 display canvas;
# the sprite itself is 399x488 before the final 190x242 WPF presentation.
SOURCE_Y_TO_FINAL_DIP = (488 / 550) * (242 / 509)
SOURCE_TO_FINAL_DIP = (SOURCE_X_TO_FINAL_DIP * SOURCE_Y_TO_FINAL_DIP) ** 0.5
MAX_RUNTIME_SCALE = 1.4
MAX_DPI_SCALE = 1.5
BBOX_SCALE_STEP_LIMIT = 0.025
BASELINE_STEP_MAX_PHYSICAL_PX_LIMIT = 1.0
BRIM_STEP_DIP_LIMIT = 2.0
ACTION_NAMES = ("cry", "cute", "like", "eat")
LOOP_ACTION_NAMES = ("cry", "like", "eat")
TODO_POSE_NAME = "think"
SMOOTH_ACTION_NAMES = (*ACTION_NAMES, TODO_POSE_NAME)
# Butterfly reuses think.smooth and is composited from one independent 96x96
# PNG.  It is intentionally not generated as a full-size dense/atlas sequence.
LIGHTWEIGHT_OVERLAY_ASSET_NAMES = ("luban-butterfly.png",)
ACTION_KEY_FRAME_COUNTS = {"cute": 11}
ACTION_SMOOTH_FRAME_COUNTS = {"cute": 56}
EDGE_DIRECTIONS = ("left", "top", "bottom")
EDGE_PEEK_FRAME_COUNT = 48
EDGE_PEEK_PHASE_FRAME_COUNT = EDGE_PEEK_FRAME_COUNT // 4
REMINDER_KEY_COUNT = 8
REMINDER_ENTER_FRAME_COUNT = 33
REMINDER_HOLD_FRAME_COUNT = 48
INTERNAL_BRIDGES = {"cry": (3,), "think": (6,)}
SKIPPED_ACTION_KEY_FRAMES = {
    # cute frame05 is a redundant closed-mouth pose between the already
    # compatible frame04/frame06 silhouettes. Its two-frame budget is better
    # spent smoothing the single intentional crouch and recovery below.
    "cute": frozenset({5}),
}
NEUTRAL_SPECS = {
    "cute": (
        2,
        (("action-v11-cute-02-03-neutral-alpha.png", "single", 0.5),),
    ),
    "like": (
        3,
        (("action-v11-like-03-04-neutral-alpha.png", "single", 0.5),),
    ),
}
# First hard-QA pass mapped absolute contour jumps back to their authored key
# edge.  These are targeted refinements only; expressive extrema elsewhere are
# preserved.  Values are total substeps for the named zero-based key edge.
SUBSTEP_OVERRIDES: dict[str, dict[int, int]] = {
    "wake": {4: 8, 15: 4, 20: 8, 21: 4, 23: 4},
    "cry": {2: 4},
    "cute": {2: 4, 3: 8, 4: 8, 6: 4, 7: 8, 8: 8},
    "like": {0: 4, 5: 8, 6: 4, 8: 4, 9: 4},
    "eat": {0: 4, 1: 4, 7: 4, 10: 4},
}
BASELINE_STABILIZE_EDGES: dict[str, frozenset[int]] = {
    # RIFE's original half sample on wake15->16 lands the feet three final
    # pixels early while its hat is correctly registered.  Stabilize only the
    # generated lower body; authored endpoints remain byte-for-byte unchanged.
    "wake": frozenset({11, 12, 13, 14}),
    # like frame03->registered neutral has a single generated sample whose
    # lower silhouette reaches the endpoint one frame early.
    "like": frozenset({4}),
}

sys.path.insert(0, str(ROOT / "tools"))
import install_generated_motion_assets as installer  # noqa: E402


def load_rgba(path: Path) -> np.ndarray:
    with Image.open(path) as opened:
        image = opened.convert("RGBA")
        if image.size != RUNTIME_SIZE:
            raise ValueError(f"{path} is {image.size}; expected {RUNTIME_SIZE}")
        return np.asarray(image, dtype=np.uint8)


def canonical_digest(paths: list[Path], label: str) -> str:
    digest = hashlib.sha256()
    digest.update(b"dense-motion-v1\0")
    digest.update(label.encode("utf-8"))
    for path in paths:
        rgba = load_rgba(path)
        digest.update(path.name.encode("utf-8"))
        digest.update(rgba.tobytes())
    digest.update(RIFE_EXE.read_bytes())
    for model_file in sorted(RIFE_MODEL.iterdir()):
        if model_file.is_file():
            digest.update(model_file.name.encode("utf-8"))
            digest.update(model_file.read_bytes())
    return digest.hexdigest()[:20]


def save_channel_inputs(paths: list[Path], directory: Path, alpha_only: bool) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for index, path in enumerate(paths):
        rgba = load_rgba(path)
        alpha = rgba[..., 3:4].astype(np.uint16)
        if alpha_only:
            channel = np.repeat(alpha.astype(np.uint8), 3, axis=2)
        else:
            rgb = rgba[..., :3].astype(np.uint16)
            channel = ((rgb * alpha + 127) // 255).astype(np.uint8)
        Image.fromarray(channel, "RGB").save(
            directory / f"{index:08d}.png", optimize=False
        )


def sorted_pngs(directory: Path) -> list[Path]:
    return sorted(directory.glob("*.png"), key=lambda path: path.name)


def run_rife_double(paths: list[Path], label: str) -> tuple[list[Path], list[Path]]:
    if len(paths) < 2:
        raise ValueError("RIFE sequence needs at least two keys")
    digest = canonical_digest(paths, label)
    batch = WORK_ROOT / f"{label}-{digest}"
    premul_input = batch / "premul-input"
    alpha_input = batch / "alpha-input"
    premul_output = batch / "premul-output"
    alpha_output = batch / "alpha-output"
    expected = len(paths) * 2

    save_channel_inputs(paths, premul_input, alpha_only=False)
    save_channel_inputs(paths, alpha_input, alpha_only=True)

    for kind, input_directory, output_directory in (
        ("premul", premul_input, premul_output),
        ("alpha", alpha_input, alpha_output),
    ):
        output_directory.mkdir(parents=True, exist_ok=True)
        outputs = sorted_pngs(output_directory)
        if len(outputs) == expected:
            print(f"cache hit: {label} {kind} ({expected} files)", flush=True)
            continue
        for stale in outputs:
            stale.unlink()
        def invoke(jobs: str) -> None:
            command = [
                str(RIFE_EXE),
                "-i",
                str(input_directory),
                "-o",
                str(output_directory),
                "-m",
                str(RIFE_MODEL),
                "-g",
                "0",
                "-j",
                jobs,
                "-x",
                "-f",
                "%08d.png",
            ]
            subprocess.run(command, check=True)

        print(
            f"RIFE {label} {kind}: {len(paths) - 1} midpoints, jobs={RIFE_JOBS}",
            flush=True,
        )
        try:
            invoke(RIFE_JOBS)
        except (subprocess.CalledProcessError, OSError) as error:
            if RIFE_JOBS == "1:1:1":
                raise
            for stale in sorted_pngs(output_directory):
                stale.unlink()
            print(
                f"RIFE {label} {kind}: jobs={RIFE_JOBS} failed ({error}); "
                "retrying 1:1:1",
                flush=True,
            )
            invoke("1:1:1")
        outputs = sorted_pngs(output_directory)
        if len(outputs) != expected and RIFE_JOBS != "1:1:1":
            for stale in outputs:
                stale.unlink()
            print(
                f"RIFE {label} {kind}: jobs={RIFE_JOBS} emitted "
                f"{len(outputs)}/{expected}; retrying 1:1:1",
                flush=True,
            )
            invoke("1:1:1")
            outputs = sorted_pngs(output_directory)
        if len(outputs) != expected:
            raise RuntimeError(
                f"RIFE {label} {kind} emitted {len(outputs)}, expected {expected}"
            )
    return sorted_pngs(premul_output), sorted_pngs(alpha_output)


def reconstruct_midpoint(premul_path: Path, alpha_path: Path) -> Image.Image:
    with Image.open(premul_path) as opened:
        premul = np.asarray(opened.convert("RGB"), dtype=np.uint32)
    with Image.open(alpha_path) as opened:
        alpha_rgb = np.asarray(opened.convert("RGB"), dtype=np.uint16)
    channel_spread = int(
        (alpha_rgb.max(axis=2) - alpha_rgb.min(axis=2)).max(initial=0)
    )
    # ncnn's three nominally identical alpha channels can differ by a few
    # integer levels after convolution (observed worst case 25 on UHD 730).
    # Averaging them is the established RIFE-alpha prototype contract; a much
    # larger spread indicates a corrupt/channel-shifted inference.
    if channel_spread > 64:
        raise ValueError(f"RIFE alpha channels diverged by {channel_spread}")
    alpha = np.rint(alpha_rgb.astype(np.float32).mean(axis=2)).astype(np.uint32)
    rgb = np.zeros_like(premul, dtype=np.uint32)
    visible = alpha > 0
    rgb[visible] = np.minimum(
        255,
        (premul[visible] * 255 + alpha[visible, None] // 2)
        // alpha[visible, None],
    )
    rgba = np.concatenate((rgb, alpha[..., None]), axis=2).astype(np.uint8)
    rgba[~visible] = 0
    result = Image.fromarray(rgba, "RGBA")
    return suppress_motion_trails(installer.neutralize_green_fringe(result))


def suppress_motion_trails(image: Image.Image) -> Image.Image:
    """Remove only the wide, low-alpha RIFE trail outside the solid silhouette.

    RIFE can leave a 3-8px translucent copy of a moving arm or hat edge.  WPF's
    transparent-window compositor turns that copy into the colored "light
    ripple" reported by the user.  Normal 1-2px antialiasing is preserved; RGB
    is cleared whenever alpha is cleared so Pbgra32 packing cannot resurrect it.
    """

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    alpha = rgba[..., 3]
    core = alpha >= 160
    if not core.any():
        return image.convert("RGBA")
    distance = cv2.distanceTransform((~core).astype(np.uint8), cv2.DIST_L2, 3)
    trail = (alpha > 0) & (alpha < 160) & (distance > 2.0)
    rgba[trail] = 0
    rgba[alpha == 0] = 0
    return Image.fromarray(rgba, "RGBA")


def clean_motion_path(path: Path) -> bool:
    with Image.open(path) as opened:
        original = opened.convert("RGBA")
    cleaned = suppress_motion_trails(original)
    if np.array_equal(np.asarray(original), np.asarray(cleaned)):
        return False
    installer.save_png_atomically(cleaned, path)
    return True


def pixel_digest(path: Path) -> str:
    return hashlib.sha256(load_rgba(path).tobytes()).hexdigest()


def clean_existing_motion_assets() -> dict[str, int]:
    """Clean interpolated products while preserving every authored key exactly."""

    cleaned_counts: dict[str, int] = {}
    wake_keys = [ASSETS / f"luban-wake-{number:02d}.png" for number in range(1, 28)]
    wake_key_hashes = {pixel_digest(path) for path in wake_keys}
    wake_outputs = sorted(
        ASSETS.glob("luban-wake-smooth-*.png"), key=lambda path: path.name
    )
    cleaned_counts["wake"] = sum(
        clean_motion_path(path)
        for path in wake_outputs
        if pixel_digest(path) not in wake_key_hashes
    )

    action_keys, _ = build_action_key_sequences()
    for action, keys in action_keys.items():
        key_hashes = {pixel_digest(path) for path in keys}
        smooth = sorted(
            ASSETS.glob(f"luban-{action}-smooth-*.png"), key=lambda path: path.name
        )
        cleaned_counts[f"{action}-smooth"] = sum(
            clean_motion_path(path)
            for path in smooth
            if pixel_digest(path) not in key_hashes
        )

    for action in LOOP_ACTION_NAMES:
        loops = sorted(
            ASSETS.glob(f"luban-{action}-loop-*.png"), key=lambda path: path.name
        )
        cleaned_counts[f"{action}-loop"] = sum(
            clean_motion_path(path)
            for number, path in enumerate(loops, start=1)
            if number not in (12, 24, 36, 48)
        )

    for direction in EDGE_DIRECTIONS:
        smooth = sorted(
            ASSETS.glob(f"luban-edge-{direction}-smooth-*.png"),
            key=lambda path: path.name,
        )
        cleaned_counts[f"edge-{direction}"] = sum(
            clean_motion_path(path)
            for number, path in enumerate(smooth, start=1)
            if number not in (12, 24, 36, 48)
        )

    report = WORK_ROOT / "clean-motion-trails.json"
    report.write_text(
        json.dumps(cleaned_counts, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"cleaned RIFE motion trails: {cleaned_counts}", flush=True)
    return cleaned_counts


def atomic_copy_png(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + ".tmp")
    shutil.copyfile(source, temporary)
    os.replace(temporary, destination)


def remove_stale(prefix: str, keep_count: int) -> None:
    resolved_assets = ASSETS.resolve()
    for path in ASSETS.glob(f"{prefix}-*.png"):
        if path.resolve().parent != resolved_assets:
            raise RuntimeError(f"Refusing to delete outside Assets: {path}")
        suffix = path.stem.rsplit("-", 1)[-1]
        if suffix.isdigit() and int(suffix) > keep_count:
            path.unlink()


def emit_doubled_sequence(
    keys: list[Path],
    label: str,
    prefix: str,
    discard_first_key: bool = False,
) -> list[Path]:
    premul_outputs, alpha_outputs = run_rife_double(keys, label)
    frames: list[tuple[str, Path | tuple[Path, Path]]] = []
    for index, key in enumerate(keys):
        frames.append(("key", key))
        if index < len(keys) - 1:
            output_index = index * 2 + 1
            frames.append(
                ("mid", (premul_outputs[output_index], alpha_outputs[output_index]))
            )
    if discard_first_key:
        frames = frames[1:]

    destinations: list[Path] = []
    for number, (kind, value) in enumerate(frames, start=1):
        destination = ASSETS / f"{prefix}-{number:03d}.png"
        if kind == "key":
            assert isinstance(value, Path)
            atomic_copy_png(value, destination)
        else:
            assert isinstance(value, tuple)
            midpoint = reconstruct_midpoint(value[0], value[1])
            installer.save_png_atomically(midpoint, destination)
        destinations.append(destination)
    remove_stale(prefix, len(destinations))
    return destinations


def build_wake() -> list[Path]:
    keys = [ASSETS / f"luban-wake-{number:02d}.png" for number in range(1, 28)]
    outputs = emit_doubled_sequence(
        keys,
        label="wake-27-to-53",
        prefix="luban-wake-smooth",
    )
    if len(outputs) != 53:
        raise AssertionError(f"wake dense count is {len(outputs)}, expected 53")
    print("wrote wake smooth 001..053", flush=True)
    return outputs


def alpha_bbox(frame: Image.Image) -> tuple[int, int, int, int]:
    box = frame.convert("RGBA").getchannel("A").getbbox()
    if box is None:
        raise ValueError("neutral pose is empty")
    return box


def brim_geometry(frame: Image.Image) -> tuple[float, float, float]:
    box = installer.get_blue_brim_box(frame.convert("RGBA"))
    return (
        (box[0] + box[2]) / 2.0,
        (box[1] + box[3]) / 2.0,
        float(box[2] - box[0]),
    )


def edge_hat_anchor_geometry(frame: Image.Image) -> tuple[float, float, float]:
    """Locate the blue hat band for edge sprites, including bottom crops."""

    image = frame.convert("RGBA")
    components = installer.get_mask_components(
        image,
        lambda red, green, blue: (
            blue >= 95
            and green >= 55
            and blue >= red * 1.22
            and blue >= green * 1.08
        ),
    )
    useful = [
        (count, box)
        for count, box in components
        if count >= 18 and box[2] - box[0] >= 12
    ]
    if not useful:
        raise ValueError("edge frame does not contain a detectable blue hat band")
    _, box = max(
        useful,
        key=lambda item: item[0] * max(1, item[1][2] - item[1][0]),
    )
    return (
        (box[0] + box[2]) / 2.0,
        (box[1] + box[3]) / 2.0,
        float(box[2] - box[0]),
    )


def weighted_hat_anchor_geometry(frame: Image.Image) -> tuple[float, float, float]:
    """Return a subpixel-stable upper-head anchor from weighted alpha.

    Connected colour boxes move by whole pixels when an antialiased edge
    crosses a threshold, while expressions also alter blue/red energy inside
    an otherwise stationary hat.  The fixed upper-silhouette ROI uses alpha
    weights instead: its centroid and second moment vary continuously and it
    excludes eyes, reaching arms, and standing-body clothing.
    """

    rgba = np.asarray(frame.convert("RGBA"), dtype=np.float64)
    alpha = rgba[..., 3]
    visible_y, _ = np.nonzero(alpha >= 24)
    if not len(visible_y):
        raise ValueError("empty frame has no weighted hat anchor")
    top = int(visible_y.min())
    bottom = int(visible_y.max() + 1)
    upper_limit = min(
        rgba.shape[0], top + max(1, round((bottom - top) * 0.34))
    )
    weights = alpha.copy()
    weights[:top] = 0.0
    weights[upper_limit:] = 0.0
    weight_sum = float(weights.sum())
    if weight_sum < 100.0:
        raise ValueError("frame has insufficient upper alpha for hat anchor")
    grid_y, grid_x = np.indices(alpha.shape, dtype=np.float64)
    center_x = float((grid_x * weights).sum() / weight_sum)
    center_y = float((grid_y * weights).sum() / weight_sum)
    variance_x = float((((grid_x - center_x) ** 2) * weights).sum() / weight_sum)
    effective_width = 4.0 * math.sqrt(max(variance_x, 0.0))
    return center_x, center_y, effective_width


def load_neutral_source(action: str, source_name: str, source_kind: str) -> Image.Image:
    source = ROOT / "tools" / "generated_sources" / source_name
    if source_kind.startswith("cell-"):
        cells, _ = installer.load_cells(
            source,
            columns=3,
            rows=1,
            snap_to_transparent_gaps=True,
        )
        if len(cells) != 3:
            raise ValueError(f"{source_name} must contain three cells")
        cell_number = int(source_kind.removeprefix("cell-"))
        if not 1 <= cell_number <= 3:
            raise ValueError(f"invalid cell selector {source_kind}")
        return cells[cell_number - 1]
    with Image.open(source) as opened:
        return opened.convert("RGBA").copy()


def register_neutral_pose(
    action: str,
    source_name: str,
    source_kind: str,
    first_path: Path,
    second_path: Path,
    target_fraction: float,
    output_suffix: str = "",
) -> tuple[Path, dict[str, object]]:
    first = Image.open(first_path).convert("RGBA")
    second = Image.open(second_path).convert("RGBA")
    first_brim = brim_geometry(first)
    second_brim = brim_geometry(second)
    target_brim = tuple(
        a * (1.0 - target_fraction) + b * target_fraction
        for a, b in zip(first_brim, second_brim)
    )
    target_baseline = (
        alpha_bbox(first)[3] * (1.0 - target_fraction)
        + alpha_bbox(second)[3] * target_fraction
    )

    source = installer.neutralize_green_fringe(
        installer.crop_visible(load_neutral_source(action, source_name, source_kind))
    )
    source_brim = brim_geometry(source)
    source = installer.resize_rgba_premultiplied(
        source,
        (
            max(1, round(source.width * target_brim[2] / source_brim[2])),
            max(1, round(source.height * target_brim[2] / source_brim[2])),
        ),
    )
    source = installer.crop_visible(source)

    # Preserve the requested brim width, then make the brim-to-foot distance
    # match the endpoint midpoint.  The curated neutral sheets are already
    # proportion matched, so this correction is normally well below 2%.
    scaled_brim = brim_geometry(source)
    scaled_baseline = alpha_bbox(source)[3]
    source_distance = scaled_baseline - scaled_brim[1]
    target_distance = target_baseline - target_brim[1]
    vertical_factor = target_distance / source_distance
    if not 0.94 <= vertical_factor <= 1.06:
        raise ValueError(
            f"{action} neutral vertical registration needs {vertical_factor:.3f}x"
        )
    if abs(vertical_factor - 1.0) > 0.001:
        source = installer.resize_rgba_premultiplied(
            source,
            (source.width, max(1, round(source.height * vertical_factor))),
        )
        source = installer.crop_visible(source)

    scaled_brim = brim_geometry(source)
    x = round(target_brim[0] - scaled_brim[0])
    y = round(target_brim[1] - scaled_brim[1])
    if x < 0 or y < 0 or x + source.width > RUNTIME_SIZE[0] or y + source.height > RUNTIME_SIZE[1]:
        raise ValueError(f"{action} neutral does not fit runtime canvas at {(x, y)}")
    canvas = Image.new("RGBA", RUNTIME_SIZE, (0, 0, 0, 0))
    canvas.alpha_composite(source, (x, y))
    canvas = installer.neutralize_green_fringe(canvas)

    actual_brim = brim_geometry(canvas)
    actual_baseline = alpha_bbox(canvas)[3]
    center_error = float(
        ((actual_brim[0] - target_brim[0]) ** 2 +
         (actual_brim[1] - target_brim[1]) ** 2) ** 0.5
    )
    if center_error > 1.0 or abs(actual_brim[2] - target_brim[2]) > 1.0:
        raise AssertionError(f"{action} neutral brim registration missed target")
    if abs(actual_baseline - target_baseline) > 1.5:
        raise AssertionError(f"{action} neutral baseline registration missed target")

    destination = (
        WORK_ROOT
        / "registered-neutrals"
        / f"{action}-neutral{output_suffix}.png"
    )
    installer.save_png_atomically(canvas, destination)
    metrics = {
        "action": action,
        "source": source_name,
        "source_kind": source_kind,
        "target_fraction": target_fraction,
        "target_brim": target_brim,
        "actual_brim": actual_brim,
        "brim_center_error": center_error,
        "target_baseline": target_baseline,
        "actual_baseline": actual_baseline,
        "vertical_factor": vertical_factor,
        "output": str(destination),
    }
    return destination, metrics


def build_action_key_sequences(
    actions: tuple[str, ...] = SMOOTH_ACTION_NAMES,
) -> tuple[dict[str, list[Path]], list[dict[str, object]]]:
    sequences: dict[str, list[Path]] = {}
    neutral_metrics: list[dict[str, object]] = []
    wake_end = ASSETS / "luban-wake-27.png"
    for action in actions:
        keys = [wake_end, ASSETS / f"luban-{action}-entry-bridge.png"]
        neutral_paths: list[Path] = []
        neutral_after = -1
        if action in NEUTRAL_SPECS:
            neutral_after, poses = NEUTRAL_SPECS[action]
            for pose_index, (source_name, source_kind, target_fraction) in enumerate(
                poses, start=1
            ):
                neutral_path, metrics = register_neutral_pose(
                    action,
                    source_name,
                    source_kind,
                    ASSETS / f"luban-{action}-frame-{neutral_after:02d}.png",
                    ASSETS / f"luban-{action}-frame-{neutral_after + 1:02d}.png",
                    target_fraction,
                    output_suffix=(f"-{pose_index}" if len(poses) > 1 else ""),
                )
                neutral_paths.append(neutral_path)
                neutral_metrics.append(metrics)
        for number in range(1, ACTION_KEY_FRAME_COUNTS.get(action, 24) + 1):
            if number in SKIPPED_ACTION_KEY_FRAMES.get(action, frozenset()):
                continue
            keys.append(ASSETS / f"luban-{action}-frame-{number:02d}.png")
            if number in INTERNAL_BRIDGES.get(action, ()):
                keys.append(
                    ASSETS
                    / f"luban-{action}-bridge-{number:02d}-{number + 1:02d}.png"
                )
            if neutral_paths and number == neutral_after:
                keys.extend(neutral_paths)
        sequences[action] = keys
    return sequences, neutral_metrics


def build_actions(
    actions: tuple[str, ...] = SMOOTH_ACTION_NAMES,
) -> dict[str, list[Path]]:
    sequences, neutral_metrics = build_action_key_sequences(actions)
    packed: list[Path] = []
    starts: dict[str, int] = {}
    for action, keys in sequences.items():
        starts[action] = len(packed)
        packed.extend(keys)
    label = (
        "actions-packed"
        if actions == SMOOTH_ACTION_NAMES
        else f"actions-packed-{'-'.join(actions)}"
    )
    premul_outputs, alpha_outputs = run_rife_double(packed, label)

    all_outputs: dict[str, list[Path]] = {}
    for action, keys in sequences.items():
        start = starts[action]
        frames: list[tuple[str, Path | tuple[Path, Path]]] = []
        # Discard shared wake key, but keep its midpoint to entry as smooth001.
        for local_index in range(len(keys) - 1):
            global_index = start + local_index
            frames.append(
                (
                    "mid",
                    (
                        premul_outputs[global_index * 2 + 1],
                        alpha_outputs[global_index * 2 + 1],
                    ),
                )
            )
            frames.append(("key", keys[local_index + 1]))

        outputs: list[Path] = []
        prefix = f"luban-{action}-smooth"
        for number, (kind, value) in enumerate(frames, start=1):
            destination = ASSETS / f"{prefix}-{number:03d}.png"
            if kind == "key":
                assert isinstance(value, Path)
                atomic_copy_png(value, destination)
            else:
                assert isinstance(value, tuple)
                installer.save_png_atomically(
                    reconstruct_midpoint(value[0], value[1]), destination
                )
            outputs.append(destination)
        remove_stale(prefix, len(outputs))
        all_outputs[action] = outputs
        print(f"wrote {action} smooth 001..{len(outputs):03d}", flush=True)

    metrics_path = WORK_ROOT / "registered-neutrals" / "metrics.json"
    metrics_path.parent.mkdir(parents=True, exist_ok=True)
    metrics_path.write_text(
        json.dumps(neutral_metrics, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return all_outputs


def rendered_alpha_geometry_image(source: Image.Image) -> dict[str, int]:
    source = source.convert("RGBA")
    resized = installer.resize_rgba_premultiplied(
        source,
        (
            ATLAS_DISPLAY_SIZE[0],
            round(source.height * ATLAS_DISPLAY_SIZE[0] / source.width),
        ),
    )
    display = Image.new("RGBA", ATLAS_DISPLAY_SIZE, (0, 0, 0, 0))
    display.alpha_composite(
        resized, (0, ATLAS_DISPLAY_SIZE[1] - resized.height)
    )
    alpha = np.asarray(display, dtype=np.uint8)[..., 3]
    ys, xs = np.nonzero(alpha >= 24)
    if not len(xs):
        raise ValueError("empty rendered frame")
    return {
        "width": int(xs.max() + 1 - xs.min()),
        "height": int(ys.max() + 1 - ys.min()),
        "baseline": int(ys.max() + 1),
    }


def rendered_alpha_geometry(path: Path) -> dict[str, int]:
    with Image.open(path) as opened:
        source = opened.convert("RGBA")
    return rendered_alpha_geometry_image(source)


def warp_lower_body_vertical(source: Image.Image, factor: float) -> Image.Image:
    """Scale only the body below the hat brim, in premultiplied RGBA.

    The wake15->16 RIFE samples keep the hat correctly registered but shorten
    the torso/legs.  Moving the complete bitmap would re-introduce visible head
    jitter, so the rows through the blue brim remain byte-identical and only
    the lower body is stretched or compressed.
    """

    if not 0.94 <= factor <= 1.06:
        raise ValueError(f"unsafe lower-body scale {factor:.5f}")
    frame = source.convert("RGBA")
    rgba = np.asarray(frame, dtype=np.uint8)
    brim_box = installer.get_blue_brim_box(frame)
    pivot_y = min(frame.height - 2, brim_box[3] + 2)

    alpha = rgba[..., 3:4].astype(np.float32) / 255.0
    premultiplied = np.concatenate(
        (rgba[..., :3].astype(np.float32) * alpha, alpha * 255.0), axis=2
    )
    map_x = np.broadcast_to(
        np.arange(frame.width, dtype=np.float32), (frame.height, frame.width)
    ).copy()
    destination_y = np.arange(frame.height, dtype=np.float32)
    source_y = destination_y.copy()
    lower = destination_y > pivot_y
    source_y[lower] = pivot_y + (destination_y[lower] - pivot_y) / factor
    map_y = np.broadcast_to(source_y[:, None], (frame.height, frame.width)).copy()
    warped = cv2.remap(
        premultiplied,
        map_x,
        map_y,
        interpolation=cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0, 0),
    )

    warped_alpha = np.clip(np.rint(warped[..., 3]), 0, 255).astype(np.uint8)
    warped_rgb = np.zeros_like(rgba[..., :3])
    visible = warped_alpha > 0
    warped_rgb[visible] = np.clip(
        np.rint(
            warped[visible, :3]
            * 255.0
            / warped_alpha[visible, None].astype(np.float32)
        ),
        0,
        255,
    ).astype(np.uint8)
    result = np.dstack((warped_rgb, warped_alpha))
    # Guarantee that the hat and brim are not even numerically resampled.
    result[: pivot_y + 1] = rgba[: pivot_y + 1]
    result[warped_alpha == 0] = 0
    return suppress_motion_trails(
        installer.neutralize_green_fringe(Image.fromarray(result, "RGBA"))
    )


def stabilize_lower_body_baseline(
    source_path: Path, target_rendered_baseline: int
) -> tuple[Image.Image, dict[str, float | int | str]]:
    """Hit an exact atlas baseline without moving the already-stable hat."""

    with Image.open(source_path) as opened:
        source = opened.convert("RGBA")
    before = rendered_alpha_geometry_image(source)["baseline"]
    original_brim = brim_geometry(source)
    if before == target_rendered_baseline:
        result = suppress_motion_trails(source)
        return result, {
            "source": source_path.name,
            "before": before,
            "target": target_rendered_baseline,
            "after": before,
            "factor": 1.0,
        }

    brim_box = installer.get_blue_brim_box(source)
    pivot_y = min(source.height - 2, brim_box[3] + 2)
    source_bottom = alpha_bbox(source)[3]
    body_height = max(1.0, float(source_bottom - pivot_y))
    atlas_y_scale = ATLAS_DISPLAY_SIZE[0] / RUNTIME_SIZE[0]
    ideal = 1.0 + (
        (target_rendered_baseline - before) / atlas_y_scale / body_height
    )
    if not 0.94 <= ideal <= 1.06:
        raise ValueError(
            f"{source_path.name} needs unsafe baseline scale {ideal:.5f}"
        )

    # Integer atlas baselines have quantization plateaus.  Probe a narrow band
    # around the analytic estimate and select the least invasive exact hit.
    factors = sorted(
        {
            max(0.94, min(1.06, ideal + offset))
            for offset in np.linspace(-0.004, 0.004, 33)
        }
        | {1.0, ideal}
    )
    candidates: list[tuple[tuple[float, float], float, Image.Image, int]] = []
    for factor in factors:
        candidate = warp_lower_body_vertical(source, factor)
        after = rendered_alpha_geometry_image(candidate)["baseline"]
        score = (abs(after - target_rendered_baseline), abs(factor - 1.0))
        candidates.append((score, factor, candidate, after))
    _, factor, result, after = min(candidates, key=lambda item: item[0])
    if after != target_rendered_baseline:
        raise ValueError(
            f"{source_path.name} baseline {before}->{after}, "
            f"could not hit {target_rendered_baseline}"
        )
    corrected_brim = brim_geometry(result)
    if any(abs(a - b) > 0.01 for a, b in zip(original_brim, corrected_brim)):
        raise ValueError(
            f"{source_path.name} lower-body correction moved brim "
            f"{original_brim}->{corrected_brim}"
        )
    return result, {
        "source": source_path.name,
        "before": before,
        "target": target_rendered_baseline,
        "after": after,
            "factor": float(factor),
    }


def visual_edge_requirements(first_path: Path, second_path: Path) -> dict[str, float | int]:
    with Image.open(first_path) as opened:
        first_brim = brim_geometry(opened.convert("RGBA"))
    with Image.open(second_path) as opened:
        second_brim = brim_geometry(opened.convert("RGBA"))
    first_rendered = rendered_alpha_geometry(first_path)
    second_rendered = rendered_alpha_geometry(second_path)

    brim_distance_dip = (
        ((second_brim[0] - first_brim[0]) ** 2 +
         (second_brim[1] - first_brim[1]) ** 2) ** 0.5
        * SOURCE_TO_FINAL_DIP
    )
    first_width = first_rendered["width"]
    second_width = second_rendered["width"]
    first_height = first_rendered["height"]
    second_height = second_rendered["height"]
    baseline_distance_physical_px = (
        abs(second_rendered["baseline"] - first_rendered["baseline"])
        * FINAL_Y_DIP_PER_ATLAS_PX
        * MAX_RUNTIME_SCALE
        * MAX_DPI_SCALE
    )
    width_change_ratio = abs(second_width - first_width) / max(
        (first_width + second_width) / 2.0, 1.0
    )
    height_change_ratio = abs(second_height - first_height) / max(
        (first_height + second_height) / 2.0, 1.0
    )
    minimum = max(
        2,
        math.ceil(brim_distance_dip / BRIM_STEP_DIP_LIMIT),
        math.ceil(
            baseline_distance_physical_px / BASELINE_STEP_MAX_PHYSICAL_PX_LIMIT
        ),
        math.ceil(width_change_ratio / BBOX_SCALE_STEP_LIMIT),
        math.ceil(height_change_ratio / BBOX_SCALE_STEP_LIMIT),
    )
    substeps = 1 << (minimum - 1).bit_length()
    if substeps > 32:
        raise ValueError(
            f"edge requires unsupported {substeps} substeps: "
            f"{first_path.name} -> {second_path.name}"
        )
    return {
        "substeps": substeps,
        "brim_distance_dip": brim_distance_dip,
        "baseline_distance_max_physical_px": baseline_distance_physical_px,
        "bbox_width_change_ratio": width_change_ratio,
        "bbox_height_change_ratio": height_change_ratio,
    }


def sequence_edge_requirements(
    sequence_name: str,
    pair_index: int,
    first_path: Path,
    second_path: Path,
) -> dict[str, float | int]:
    requirements = visual_edge_requirements(first_path, second_path)
    override = SUBSTEP_OVERRIDES.get(sequence_name, {}).get(pair_index, 2)
    requirements["substeps"] = max(int(requirements["substeps"]), override)
    return requirements


def sequence_edge_substeps(
    sequence_name: str,
    pair_index: int,
    first_path: Path,
    second_path: Path,
) -> tuple[int, float]:
    requirements = sequence_edge_requirements(
        sequence_name, pair_index, first_path, second_path
    )
    return int(requirements["substeps"]), float(requirements["brim_distance_dip"])


def build_refinement_round(
    sequences: dict[str, list[Path]], label: str, stage_name: str
) -> dict[str, list[Path]]:
    packed: list[Path] = []
    starts: dict[str, int] = {}
    for segment_name, keys in sequences.items():
        starts[segment_name] = len(packed)
        packed.extend(keys)
    premul_outputs, alpha_outputs = run_rife_double(packed, label)
    result: dict[str, list[Path]] = {}
    for segment_name, keys in sequences.items():
        start = starts[segment_name]
        destination_directory = WORK_ROOT / stage_name / segment_name
        destination_directory.mkdir(parents=True, exist_ok=True)
        staged: list[Path] = []
        output_number = 1
        for local_index, key in enumerate(keys):
            key_destination = destination_directory / f"{output_number:03d}.png"
            atomic_copy_png(key, key_destination)
            staged.append(key_destination)
            output_number += 1
            if local_index == len(keys) - 1:
                continue
            global_index = start + local_index
            midpoint_destination = destination_directory / f"{output_number:03d}.png"
            installer.save_png_atomically(
                reconstruct_midpoint(
                    premul_outputs[global_index * 2 + 1],
                    alpha_outputs[global_index * 2 + 1],
                ),
                midpoint_destination,
            )
            staged.append(midpoint_destination)
            output_number += 1
        result[segment_name] = staged
    return result


def refine_edges_to_targets(
    base_edges: dict[str, list[Path]],
    targets: dict[str, int],
    *,
    label_prefix: str,
    stage_prefix: str,
) -> dict[str, list[Path]]:
    resolved = {
        segment: frames for segment, frames in base_edges.items() if targets[segment] == 2
    }
    active = {
        segment: frames for segment, frames in base_edges.items() if targets[segment] > 2
    }
    substeps = 2
    round_number = 1
    while active:
        active = build_refinement_round(
            active,
            f"{label_prefix}-round-{round_number}",
            f"{stage_prefix}/round-{round_number}",
        )
        substeps *= 2
        for segment, frames in active.items():
            if targets[segment] == substeps:
                resolved[segment] = frames
        active = {
            segment: frames
            for segment, frames in active.items()
            if targets[segment] > substeps
        }
        round_number += 1
    if set(resolved) != set(base_edges):
        missing = sorted(set(base_edges) - set(resolved))
        raise AssertionError(f"adaptive refinement did not resolve {missing}")
    return resolved


def build_adaptive_actions(
    actions: tuple[str, ...] = SMOOTH_ACTION_NAMES,
) -> dict[str, list[Path]]:
    key_sequences, _ = build_action_key_sequences(actions)
    base_edges: dict[tuple[str, int], list[Path]] = {}
    targets: dict[tuple[str, int], int] = {}
    refinement_inputs: dict[str, list[Path]] = {}
    target_by_segment: dict[str, int] = {}
    plan: list[dict[str, object]] = []

    for action, keys in key_sequences.items():
        current_smooth = sorted(
            ASSETS.glob(f"luban-{action}-smooth-*.png"), key=lambda path: path.name
        )
        expected = (len(keys) - 1) * 2
        if len(current_smooth) < expected:
            raise ValueError(
                f"{action} base smooth count {len(current_smooth)} is not usable"
            )
        # If a final adaptive sequence already exists, the canonical base
        # midpoint cache below still preserves the prior 2x frame from the
        # first run.  Otherwise snapshot the odd (midpoint) samples now.
        base_directory = WORK_ROOT / "adaptive-base" / action
        base_directory.mkdir(parents=True, exist_ok=True)
        for pair_index, (first, second) in enumerate(zip(keys, keys[1:])):
            midpoint_snapshot = base_directory / f"edge-{pair_index:03d}-mid.png"
            if not midpoint_snapshot.exists():
                if len(current_smooth) != expected:
                    raise ValueError(
                        f"{action} needs its {expected}-frame base sequence before "
                        "creating adaptive midpoint snapshots"
                    )
                midpoint_number = pair_index * 2 + 1
                source_midpoint = ASSETS / f"luban-{action}-smooth-{midpoint_number:03d}.png"
                if not source_midpoint.exists():
                    raise FileNotFoundError(source_midpoint)
                atomic_copy_png(source_midpoint, midpoint_snapshot)
            clean_motion_path(midpoint_snapshot)
            edge = [first, midpoint_snapshot, second]
            base_edges[(action, pair_index)] = edge
            requirements = sequence_edge_requirements(
                action, pair_index, first, second
            )
            substeps = int(requirements["substeps"])
            targets[(action, pair_index)] = substeps
            segment = f"{action}-edge-{pair_index:03d}"
            target_by_segment[segment] = substeps
            plan.append(
                {
                    "action": action,
                    "edge_index_0_based": pair_index,
                    "from": first.name,
                    "to": second.name,
                    **requirements,
                }
            )
            if substeps > 2:
                refinement_inputs[segment] = edge
                print(
                    f"adaptive {segment}: "
                    f"brim={requirements['brim_distance_dip']:.3f} DIP, "
                    f"baseline={requirements['baseline_distance_max_physical_px']:.3f}px, "
                    f"bbox={requirements['bbox_width_change_ratio']:.3%}/"
                    f"{requirements['bbox_height_change_ratio']:.3%} "
                    f"-> {substeps} substeps",
                    flush=True,
                )

    refined = refine_edges_to_targets(
        {
            f"{action}-edge-{pair_index:03d}": edge
            for (action, pair_index), edge in base_edges.items()
        },
        target_by_segment,
        label_prefix="adaptive-actions",
        stage_prefix="adaptive-stages",
    )
    plan_path = WORK_ROOT / "adaptive-plan.json"
    plan_path.write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding="utf-8")

    outputs_by_action: dict[str, list[Path]] = {}
    stabilization_metrics: list[dict[str, float | int | str]] = []
    stabilized_root = WORK_ROOT / "stabilized-baselines" / "actions"
    for action, keys in key_sequences.items():
        staged_output_directory = WORK_ROOT / "adaptive-final" / action
        staged_output_directory.mkdir(parents=True, exist_ok=True)
        staged_outputs: list[Path] = []
        output_number = 1
        for pair_index in range(len(keys) - 1):
            segment = f"{action}-edge-{pair_index:03d}"
            target = targets[(action, pair_index)]
            edge_frames = refined[segment]
            if len(edge_frames) != target + 1:
                raise AssertionError(f"{segment} has invalid refined length")
            first_baseline = rendered_alpha_geometry(keys[pair_index])["baseline"]
            second_baseline = rendered_alpha_geometry(keys[pair_index + 1])[
                "baseline"
            ]
            for step, source in enumerate(edge_frames[1:], start=1):
                if (
                    pair_index
                    in BASELINE_STABILIZE_EDGES.get(action, frozenset())
                    and step < target
                ):
                    expected_baseline = round(
                        first_baseline
                        + (second_baseline - first_baseline) * step / target
                    )
                    stabilized, metrics = stabilize_lower_body_baseline(
                        source, expected_baseline
                    )
                    stabilized_source = (
                        stabilized_root
                        / action
                        / f"edge-{pair_index:03d}-step-{step:03d}.png"
                    )
                    installer.save_png_atomically(stabilized, stabilized_source)
                    source = stabilized_source
                    metrics.update(
                        {
                            "action": action,
                            "edge_index_0_based": pair_index,
                            "step": step,
                            "substeps": target,
                        }
                    )
                    stabilization_metrics.append(metrics)
                destination = staged_output_directory / f"{output_number:03d}.png"
                atomic_copy_png(source, destination)
                staged_outputs.append(destination)
                output_number += 1

        final_outputs: list[Path] = []
        prefix = f"luban-{action}-smooth"
        for number, source in enumerate(staged_outputs, start=1):
            destination = ASSETS / f"{prefix}-{number:03d}.png"
            atomic_copy_png(source, destination)
            final_outputs.append(destination)
        expected_count = ACTION_SMOOTH_FRAME_COUNTS.get(action)
        if expected_count is not None and len(final_outputs) != expected_count:
            raise AssertionError(
                f"{action} smooth count {len(final_outputs)} != {expected_count}"
            )
        remove_stale(prefix, len(final_outputs))
        outputs_by_action[action] = final_outputs
        print(
            f"wrote adaptive {action} smooth 001..{len(final_outputs):03d}",
            flush=True,
        )
    stabilization_path = WORK_ROOT / "action-baseline-stabilization.json"
    stabilization_path.write_text(
        json.dumps(stabilization_metrics, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return outputs_by_action


def build_adaptive_wake() -> list[Path]:
    keys = [ASSETS / f"luban-wake-{number:02d}.png" for number in range(1, 28)]
    current = sorted(
        ASSETS.glob("luban-wake-smooth-*.png"), key=lambda path: path.name
    )
    base_count = (len(keys) - 1) * 2 + 1
    if len(current) < base_count:
        raise ValueError(f"wake base smooth count {len(current)} is not usable")

    base_edges: dict[str, list[Path]] = {}
    targets: dict[str, int] = {}
    plan: list[dict[str, object]] = []
    base_directory = WORK_ROOT / "adaptive-base" / "wake"
    base_directory.mkdir(parents=True, exist_ok=True)
    for pair_index, (first, second) in enumerate(zip(keys, keys[1:])):
        midpoint_snapshot = base_directory / f"edge-{pair_index:03d}-mid.png"
        fingerprint_path = (
            base_directory / f"edge-{pair_index:03d}-mid.sha256"
        )
        expected_fingerprint = canonical_digest(
            [first, second],
            f"adaptive-wake-midpoint-{pair_index:03d}",
        )
        actual_fingerprint = (
            fingerprint_path.read_text(encoding="ascii").strip()
            if fingerprint_path.exists()
            else ""
        )
        snapshot_is_current = (
            midpoint_snapshot.exists()
            and actual_fingerprint == expected_fingerprint
        )
        if len(current) == base_count and not snapshot_is_current:
            atomic_copy_png(current[pair_index * 2 + 1], midpoint_snapshot)
            fingerprint_path.write_text(
                expected_fingerprint + "\n",
                encoding="ascii",
            )
        elif not snapshot_is_current:
            raise ValueError(
                "wake midpoint cache does not match its authored endpoints; "
                "rerun with --wake to rebuild the 53-frame base first"
            )

        clean_motion_path(midpoint_snapshot)
        segment = f"wake-edge-{pair_index:03d}"
        base_edges[segment] = [first, midpoint_snapshot, second]
        requirements = sequence_edge_requirements(
            "wake", pair_index, first, second
        )
        targets[segment] = int(requirements["substeps"])
        plan.append(
            {
                "edge_index_0_based": pair_index,
                "from": first.name,
                "to": second.name,
                **requirements,
            }
        )
        if targets[segment] > 2:
            print(
                f"adaptive {segment}: brim={requirements['brim_distance_dip']:.3f} DIP, "
                f"baseline={requirements['baseline_distance_max_physical_px']:.3f}px, "
                f"bbox={requirements['bbox_width_change_ratio']:.3%}/"
                f"{requirements['bbox_height_change_ratio']:.3%} "
                f"-> {targets[segment]} substeps",
                flush=True,
            )

    refined = refine_edges_to_targets(
        base_edges,
        targets,
        label_prefix="adaptive-wake",
        stage_prefix="adaptive-wake-stages",
    )
    staged_directory = WORK_ROOT / "adaptive-final" / "wake"
    staged_directory.mkdir(parents=True, exist_ok=True)
    staged: list[Path] = []
    first_destination = staged_directory / "001.png"
    atomic_copy_png(keys[0], first_destination)
    staged.append(first_destination)
    output_number = 2
    stabilization_metrics: list[dict[str, float | int | str]] = []
    stabilized_directory = WORK_ROOT / "stabilized-baselines" / "wake"
    stabilized_directory.mkdir(parents=True, exist_ok=True)
    for pair_index in range(len(keys) - 1):
        segment = f"wake-edge-{pair_index:03d}"
        edge_frames = refined[segment]
        target = targets[segment]
        if len(edge_frames) != target + 1:
            raise AssertionError(f"{segment} has invalid refined length")
        first_baseline = rendered_alpha_geometry(keys[pair_index])["baseline"]
        second_baseline = rendered_alpha_geometry(keys[pair_index + 1])["baseline"]
        for step, source in enumerate(edge_frames[1:], start=1):
            if (
                pair_index in BASELINE_STABILIZE_EDGES.get("wake", frozenset())
                and step < target
            ):
                expected_baseline = round(
                    first_baseline
                    + (second_baseline - first_baseline) * step / target
                )
                stabilized, metrics = stabilize_lower_body_baseline(
                    source, expected_baseline
                )
                stabilized_source = (
                    stabilized_directory
                    / f"edge-{pair_index:03d}-step-{step:03d}.png"
                )
                installer.save_png_atomically(stabilized, stabilized_source)
                source = stabilized_source
                metrics.update(
                    {
                        "edge_index_0_based": pair_index,
                        "step": step,
                        "substeps": target,
                    }
                )
                stabilization_metrics.append(metrics)
            destination = staged_directory / f"{output_number:03d}.png"
            atomic_copy_png(source, destination)
            staged.append(destination)
            output_number += 1

    outputs: list[Path] = []
    prefix = "luban-wake-smooth"
    for number, source in enumerate(staged, start=1):
        destination = ASSETS / f"{prefix}-{number:03d}.png"
        atomic_copy_png(source, destination)
        outputs.append(destination)
    remove_stale(prefix, len(outputs))
    plan_path = WORK_ROOT / "adaptive-wake-plan.json"
    plan_path.write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding="utf-8")
    stabilization_path = WORK_ROOT / "wake-baseline-stabilization.json"
    stabilization_path.write_text(
        json.dumps(stabilization_metrics, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(f"wrote adaptive wake smooth 001..{len(outputs):03d}", flush=True)
    return outputs


def build_loop_round(
    sequences: dict[str, list[Path]], round_number: int
) -> dict[str, list[Path]]:
    packed: list[Path] = []
    starts: dict[str, int] = {}
    for action, keys in sequences.items():
        starts[action] = len(packed)
        packed.extend(keys)
    premul_outputs, alpha_outputs = run_rife_double(
        packed, f"loops-round-{round_number}-packed"
    )
    result: dict[str, list[Path]] = {}
    for action, keys in sequences.items():
        start = starts[action]
        staged: list[Path] = []
        destination_directory = WORK_ROOT / "loop-stages" / f"round-{round_number}" / action
        destination_directory.mkdir(parents=True, exist_ok=True)
        frame_number = 1
        for local_index, key in enumerate(keys):
            key_destination = destination_directory / f"{frame_number:03d}.png"
            atomic_copy_png(key, key_destination)
            staged.append(key_destination)
            frame_number += 1
            if local_index == len(keys) - 1:
                continue
            global_index = start + local_index
            midpoint_destination = destination_directory / f"{frame_number:03d}.png"
            installer.save_png_atomically(
                reconstruct_midpoint(
                    premul_outputs[global_index * 2 + 1],
                    alpha_outputs[global_index * 2 + 1],
                ),
                midpoint_destination,
            )
            staged.append(midpoint_destination)
            frame_number += 1
        expected = (len(keys) - 1) * 2 + 1
        if len(staged) != expected:
            raise AssertionError(
                f"{action} loop round {round_number}: {len(staged)} != {expected}"
            )
        result[action] = staged
    return result


def premultiplied_resample(
    first_path: Path, second_path: Path, numerator: int, denominator: int
) -> Image.Image:
    first = load_rgba(first_path).astype(np.uint32)
    second = load_rgba(second_path).astype(np.uint32)
    first_alpha = first[..., 3:4]
    second_alpha = second[..., 3:4]
    first_pm = (first[..., :3] * first_alpha + 127) // 255
    second_pm = (second[..., :3] * second_alpha + 127) // 255
    inverse = denominator - numerator
    alpha = (
        first_alpha * inverse + second_alpha * numerator + denominator // 2
    ) // denominator
    premul = (
        first_pm * inverse + second_pm * numerator + denominator // 2
    ) // denominator
    rgb = np.zeros_like(premul)
    visible = alpha[..., 0] > 0
    rgb[visible] = np.minimum(
        255,
        (premul[visible] * 255 + alpha[visible] // 2) // alpha[visible],
    )
    rgba = np.concatenate((rgb, alpha), axis=2).astype(np.uint8)
    rgba[~visible] = 0
    return suppress_motion_trails(
        installer.neutralize_green_fringe(Image.fromarray(rgba, "RGBA"))
    )


def pixel_equal(first: Path, second: Path) -> bool:
    return np.array_equal(load_rgba(first), load_rgba(second))


def registered_edge_sources() -> dict[str, list[Path]]:
    """Rebuild full edge keys before their final boundary clipping."""

    destination_root = WORK_ROOT / "edge-registered"
    registered: dict[str, list[Path]] = {}
    metrics: list[dict[str, object]] = []
    unshifted_by_direction = installer.build_edge_peek_unshifted_frames(
        ROOT / "tools" / "generated_sources"
    )
    for direction in EDGE_DIRECTIONS:
        originals = [
            ASSETS / f"luban-edge-{direction}-{number:02d}.png"
            for number in range(1, 5)
        ]
        direction_outputs: list[Path] = []
        for number, (original_path, unshifted) in enumerate(
            zip(originals, unshifted_by_direction[direction]),
            start=1,
        ):
            destination = destination_root / direction / f"{number:02d}.png"
            destination.parent.mkdir(parents=True, exist_ok=True)
            with Image.open(original_path) as opened:
                original = opened.convert("RGBA")
            before_box = alpha_bbox(original)
            reconstructed_runtime = installer.translate_edge_peek_frame(
                unshifted,
                direction,
                installer.EDGE_PEEK_REVEAL_OFFSETS[direction][number - 1],
            )
            if not np.array_equal(
                np.asarray(reconstructed_runtime, dtype=np.uint8),
                np.asarray(original, dtype=np.uint8),
            ):
                raise AssertionError(
                    f"edge {direction} key {number:02d} does not match "
                    "the tracked source and reveal-offset contract"
                )
            installer.save_png_atomically(unshifted, destination)
            with Image.open(destination) as opened:
                after = opened.convert("RGBA")
            after_box = alpha_bbox(after)
            metrics.append(
                {
                    "direction": direction,
                    "frame": number,
                    "source": original_path.name,
                    "registered": str(destination.relative_to(ROOT)),
                    "runtime_clipped_bbox": list(before_box),
                    "rife_input_unshifted_bbox": list(after_box),
                    "unshifted_to_clipped_visible_width_ratio": (
                        (after_box[2] - after_box[0])
                        / max(before_box[2] - before_box[0], 1)
                    ),
                }
            )
            direction_outputs.append(destination)
        registered[direction] = direction_outputs

    report = destination_root / "metrics.json"
    report.write_text(
        json.dumps(metrics, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return registered


def build_loops() -> dict[str, list[Path]]:
    # Four closed segments.  The last key is repeated only as a construction
    # endpoint; products sample t=1/12..1 for each edge.  This avoids repeating
    # forward's frame24 while making loop048 the exact return endpoint.
    sequences = {
        action: [
            ASSETS / f"luban-{action}-frame-24.png",
            ASSETS / f"luban-{action}-frame-21.png",
            ASSETS / f"luban-{action}-frame-22.png",
            ASSETS / f"luban-{action}-frame-23.png",
            ASSETS / f"luban-{action}-frame-24.png",
        ]
        for action in LOOP_ACTION_NAMES
    }
    for round_number in range(1, 5):
        sequences = build_loop_round(sequences, round_number)
    if any(len(frames) != 65 for frames in sequences.values()):
        raise AssertionError("loop recursive RIFE must finish with 65 samples")

    outputs_by_action: dict[str, list[Path]] = {}
    for action, dense65 in sequences.items():
        outputs: list[Path] = []
        prefix = f"luban-{action}-loop"
        for sample in range(1, 49):
            # Dense source has 16 substeps per key edge.  Target has 12, so
            # source coordinate is (16/12)*sample == 4*sample/3.
            source_numerator = 4 * sample
            lower, remainder = divmod(source_numerator, 3)
            destination = ASSETS / f"{prefix}-{sample:03d}.png"
            if remainder == 0:
                atomic_copy_png(dense65[lower], destination)
            else:
                installer.save_png_atomically(
                    premultiplied_resample(
                        dense65[lower], dense65[lower + 1], remainder, 3
                    ),
                    destination,
                )
            outputs.append(destination)
        remove_stale(prefix, len(outputs))

        expected_keys = {
            12: ASSETS / f"luban-{action}-frame-21.png",
            24: ASSETS / f"luban-{action}-frame-22.png",
            36: ASSETS / f"luban-{action}-frame-23.png",
            48: ASSETS / f"luban-{action}-frame-24.png",
        }
        for output_number, expected_path in expected_keys.items():
            if not pixel_equal(outputs[output_number - 1], expected_path):
                raise AssertionError(
                    f"{action} loop phase {output_number:03d} is not exact key"
                )
        outputs_by_action[action] = outputs
        print(f"wrote {action} loop 001..048", flush=True)
    return outputs_by_action


def edge_reveal_offset_for_sample(direction: str, sample: int) -> int:
    """Interpolate one crisp source-pixel reveal offset around the edge loop."""

    if direction not in installer.EDGE_PEEK_REVEAL_OFFSETS:
        raise ValueError(f"Unsupported edge direction: {direction}")
    if sample < 1 or sample > EDGE_PEEK_FRAME_COUNT:
        raise ValueError(f"Invalid edge sample: {sample}")
    offsets = installer.EDGE_PEEK_REVEAL_OFFSETS[direction]
    segment, zero_based_step = divmod(
        sample - 1,
        EDGE_PEEK_PHASE_FRAME_COUNT,
    )
    step = zero_based_step + 1
    start = offsets[segment]
    end = offsets[(segment + 1) % len(offsets)]
    return (
        start * (EDGE_PEEK_PHASE_FRAME_COUNT - step) + end * step +
        EDGE_PEEK_PHASE_FRAME_COUNT // 2
    ) // EDGE_PEEK_PHASE_FRAME_COUNT


BOTTOM_BLINK_CENTERS = {
    7: ((185, 524), (265, 523)),
    8: ((185, 522), (265, 521)),
    9: ((185, 520), (265, 518)),
    10: ((185, 517), (265, 515)),
}
BOTTOM_BLINK_SOURCE_CENTERS = {
    6: ((185, 527), (265, 526)),
    11: ((186, 515), (265, 513)),
}
BOTTOM_BLINK_CLOSURE = {7: 0.50, 8: 0.90, 9: 1.00, 10: 0.55}


def antialiased_polyline_mask(
    width: int,
    height: int,
    points: list[tuple[float, float]],
) -> np.ndarray:
    """Render one crisp eyelid curve without resampling the underlying eye."""

    scale = 4
    canvas = Image.new("L", (width * scale, height * scale), 0)
    draw = ImageDraw.Draw(canvas)
    draw.line(
        [(round(x * scale), round(y * scale)) for x, y in points],
        fill=255,
        width=10,
        joint="curve",
    )
    resized = canvas.resize((width, height), Image.Resampling.LANCZOS)
    return np.asarray(resized, dtype=np.float32) / 255.0


def bottom_blink_line_color(source_frames: list[np.ndarray]) -> np.ndarray:
    """Sample the character's existing dark-brown ink instead of inventing a color."""

    samples: list[np.ndarray] = []
    yy, xx = np.mgrid[: RUNTIME_SIZE[1], : RUNTIME_SIZE[0]]
    for source_number, source in zip((6, 11), source_frames):
        for center_x, center_y in BOTTOM_BLINK_SOURCE_CENTERS[source_number]:
            ellipse = (
                ((xx - center_x) / 30.0) ** 2
                + ((yy - center_y) / 25.0) ** 2
                <= 1.0
            )
            red = source[..., 0]
            green = source[..., 1]
            blue = source[..., 2]
            ink = (
                ellipse
                & (source[..., 3] >= 254)
                & (red < 165)
                & (green < 95)
                & (blue < 90)
            )
            if ink.any():
                samples.append(source[..., :3][ink])
    if not samples:
        raise AssertionError("bottom blink could not sample the existing eye-line color")
    return np.rint(np.median(np.concatenate(samples), axis=0)).astype(np.uint8)


def repair_bottom_blink_eye(
    target: np.ndarray,
    source: np.ndarray,
    destination_center: tuple[int, int],
    source_center: tuple[int, int],
    closure: float,
    line_color: np.ndarray,
    *,
    is_left_eye: bool,
) -> np.ndarray:
    """Replace a melted RIFE eye with one rigid eye and a real eyelid mask."""

    result = target.copy()
    original_alpha = result[..., 3].copy()
    height, width = original_alpha.shape
    yy, xx = np.mgrid[:height, :width]
    center_x, center_y = destination_center
    erase = (
        ((xx - center_x) / 31.0) ** 2
        + ((yy - center_y) / 26.0) ** 2
        <= 1.0
    ) & (original_alpha >= 254)
    hand = (
        (xx < center_x - 14)
        if is_left_eye
        else (xx > center_x + 14)
    ) & (yy > center_y + 10)
    erase &= ~hand

    rgb = cv2.inpaint(
        np.ascontiguousarray(result[..., :3]),
        erase.astype(np.uint8) * 255,
        5,
        cv2.INPAINT_TELEA,
    )
    relative_x = (xx - center_x) / 29.0
    inside = np.abs(relative_x) <= 1.0
    arc = np.sqrt(np.clip(1.0 - relative_x * relative_x, 0.0, 1.0))
    closed_line = center_y + 4.0 - 3.0 * (1.0 - relative_x * relative_x)
    top = (
        (1.0 - closure) * (center_y - 24.0 * arc)
        + closure * closed_line
    )
    bottom = (
        (1.0 - closure) * (center_y + 24.0 * arc)
        + closure * closed_line
    )

    if closure < 1.0:
        delta_x = center_x - source_center[0]
        delta_y = center_y - source_center[1]
        aligned = np.asarray(
            installer.translate_rgba_without_wrap(
                Image.fromarray(source, "RGBA"),
                x=delta_x,
                y=delta_y,
            ),
            dtype=np.uint8,
        )
        visible = (
            inside
            & (yy >= top)
            & (yy <= bottom)
            & erase
            & (aligned[..., 3] >= 254)
        )
        rgb[visible] = aligned[..., :3][visible]

    curve_y = top if closure < 1.0 else closed_line
    points = [
        (float(x), float(curve_y[center_y, x]))
        for x in range(max(0, center_x - 29), min(width, center_x + 30))
    ]
    lid = antialiased_polyline_mask(width, height, points)
    lid *= erase.astype(np.float32)
    rgb_float = rgb.astype(np.float32)
    rgb = np.clip(
        np.rint(
            rgb_float * (1.0 - lid[..., None])
            + line_color.astype(np.float32) * lid[..., None]
        ),
        0,
        255,
    ).astype(np.uint8)
    result[..., :3] = rgb
    result[..., 3] = original_alpha
    if not np.array_equal(result[..., 3], original_alpha):
        raise AssertionError("bottom blink repair changed alpha")
    return result


def repair_bottom_blink_frames(paths: list[Path]) -> None:
    """Turn the soft RIFE eye collapse at frames 7..10 into one clean blink."""

    source_frames: dict[int, np.ndarray] = {}
    for source_number in (6, 11):
        source_frames[source_number] = load_rgba(paths[source_number - 1])
    line_color = bottom_blink_line_color(
        [source_frames[6], source_frames[11]]
    )

    for frame_number in range(7, 11):
        before = load_rgba(paths[frame_number - 1])
        original_alpha = before[..., 3].copy()
        source_number = 6 if frame_number in (7, 8) else 11
        repaired = before
        for eye_index in (0, 1):
            repaired = repair_bottom_blink_eye(
                repaired,
                source_frames[source_number],
                BOTTOM_BLINK_CENTERS[frame_number][eye_index],
                BOTTOM_BLINK_SOURCE_CENTERS[source_number][eye_index],
                BOTTOM_BLINK_CLOSURE[frame_number],
                line_color,
                is_left_eye=eye_index == 0,
            )
        if not np.array_equal(repaired[..., 3], original_alpha):
            raise AssertionError(
                f"bottom blink frame {frame_number:03d} changed alpha"
            )
        installer.save_png_atomically(
            Image.fromarray(repaired, "RGBA"),
            paths[frame_number - 1],
        )


def build_edge_peek_sequences() -> dict[str, list[Path]]:
    """Create a 48-frame closed peek loop for each supported screen edge.

    The four registered poses are K1 rest, K2 curious, K3 full-cute, and K4
    shy-retreat. They remain exact keys at frames 48, 12, 24, and 36.
    RIFE first produces eight substeps per authored edge while the complete
    silhouettes are still on-canvas. A premultiplied transparent resample
    expands that dense path to twelve display substeps, then the interpolated
    reveal offset performs the final Windows-boundary clip. This prevents hands
    from popping at the crop line and keeps the exact authored keys at frames
    12/24/36/48 without another RIFE pass.
    """

    registered = registered_edge_sources()
    sequences = {
        direction: [*keys, keys[0]]
        for direction, keys in registered.items()
    }
    for round_number in range(1, 4):
        sequences = build_loop_round(sequences, round_number)
    if any(len(frames) != 33 for frames in sequences.values()):
        raise AssertionError("edge peek recursive RIFE must finish with 33 samples")

    outputs_by_direction: dict[str, list[Path]] = {}
    for direction, dense33 in sequences.items():
        outputs: list[Path] = []
        prefix = f"luban-edge-{direction}-smooth"
        for sample in range(1, EDGE_PEEK_FRAME_COUNT + 1):
            # Dense source has 8 substeps per authored edge; target has 12.
            source_numerator = 2 * sample
            lower, remainder = divmod(source_numerator, 3)
            destination = ASSETS / f"{prefix}-{sample:03d}.png"
            if remainder == 0:
                with Image.open(dense33[lower]) as opened:
                    sampled = opened.convert("RGBA")
            else:
                sampled = premultiplied_resample(
                    dense33[lower], dense33[lower + 1], remainder, 3
                )
            if (
                direction == "bottom"
                and sample == EDGE_PEEK_PHASE_FRAME_COUNT * 2 - 1
            ):
                # RIFE briefly produced two hat/face outlines immediately
                # before the full-cute key. Use that one clean authored pose
                # with the still-interpolated reveal offset instead.
                with Image.open(registered[direction][2]) as opened:
                    sampled = opened.convert("RGBA")
            sampled = installer.translate_edge_peek_frame(
                sampled,
                direction,
                edge_reveal_offset_for_sample(direction, sample),
            )
            # Clipping at the Windows boundary can separate a formerly valid
            # antialiased edge from its opaque core. Clean once more after the
            # translation so the cropped fragment cannot become a detached
            # low-alpha light trail in WPF's transparent compositor.
            if sample % EDGE_PEEK_PHASE_FRAME_COUNT != 0:
                sampled = suppress_motion_trails(sampled)
            installer.save_png_atomically(sampled, destination)
            outputs.append(destination)
        remove_stale(prefix, len(outputs))
        if direction == "bottom":
            repair_bottom_blink_frames(outputs)

        quarter = EDGE_PEEK_PHASE_FRAME_COUNT
        expected_keys = {
            quarter: ASSETS / f"luban-edge-{direction}-02.png",
            quarter * 2: ASSETS / f"luban-edge-{direction}-03.png",
            quarter * 3: ASSETS / f"luban-edge-{direction}-04.png",
            quarter * 4: ASSETS / f"luban-edge-{direction}-01.png",
        }
        for output_number, expected_path in expected_keys.items():
            if not pixel_equal(outputs[output_number - 1], expected_path):
                raise AssertionError(
                    f"edge {direction} phase {output_number:03d} is not exact key"
                )
        outputs_by_direction[direction] = outputs
        print(
            f"wrote edge {direction} smooth 001..{EDGE_PEEK_FRAME_COUNT:03d}",
            flush=True,
        )
    return outputs_by_direction


def emit_sequence(paths: list[Path], prefix: str) -> list[Path]:
    """Copy an already-dense path to a contiguous runtime sequence."""

    outputs: list[Path] = []
    for frame_number, source in enumerate(paths, start=1):
        destination = ASSETS / f"{prefix}-{frame_number:03d}.png"
        atomic_copy_png(source, destination)
        outputs.append(destination)
    remove_stale(prefix, len(outputs))
    return outputs


def reminder_sources() -> tuple[list[Path], list[Path]]:
    keys = [
        ASSETS / f"luban-reminder-key-{number:02d}.png"
        for number in range(1, REMINDER_KEY_COUNT + 1)
    ]
    bridges = [
        ASSETS / f"luban-reminder-bridge-{number:02d}.png"
        for number in range(1, REMINDER_KEY_COUNT + 1)
    ]
    missing = [str(path) for path in [*keys, *bridges] if not path.is_file()]
    if missing:
        raise FileNotFoundError(
            "Install reminder keys first with "
            "tools/install_generated_motion_assets.py --reminder; missing "
            + ", ".join(missing)
        )
    return keys, bridges


def reconstruct_warped_rgba(
    premultiplied: np.ndarray,
    alpha: np.ndarray,
) -> Image.Image:
    """Return straight RGBA after matching premultiplied/alpha transforms."""

    alpha_u16 = alpha.astype(np.uint16)
    premul_u32 = premultiplied.astype(np.uint32)
    rgb = np.zeros_like(premul_u32, dtype=np.uint32)
    visible = alpha_u16 > 0
    rgb[visible] = np.minimum(
        255,
        (premul_u32[visible] * 255 + alpha_u16[visible, None] // 2)
        // alpha_u16[visible, None],
    )
    rgba = np.concatenate(
        (rgb.astype(np.uint8), alpha[..., None].astype(np.uint8)),
        axis=2,
    )
    rgba[~visible] = 0
    return Image.fromarray(rgba, "RGBA")


def shift_rgba_without_wrap(image: Image.Image, offset_y: int) -> Image.Image:
    if offset_y == 0:
        return image.copy()

    source = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    shifted = np.zeros_like(source)
    if offset_y > 0:
        shifted[offset_y:] = source[:-offset_y]
    else:
        shifted[:offset_y] = source[-offset_y:]
    return Image.fromarray(shifted, "RGBA")


def render_reminder_rigid_pose(source: Image.Image, angle_degrees: float) -> Image.Image:
    """Rotate one complete authored pose without blending two silhouettes."""

    rgba = np.asarray(source.convert("RGBA"), dtype=np.uint8)
    alpha = rgba[..., 3]
    premultiplied = (
        (rgba[..., :3].astype(np.uint16) * alpha[..., None].astype(np.uint16) + 127)
        // 255
    ).astype(np.uint8)
    transform = cv2.getRotationMatrix2D((225.0, 539.0), angle_degrees, 1.0)
    warped_premultiplied = cv2.warpAffine(
        premultiplied,
        transform,
        RUNTIME_SIZE,
        flags=cv2.INTER_CUBIC,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0),
    )
    warped_alpha = cv2.warpAffine(
        alpha,
        transform,
        RUNTIME_SIZE,
        flags=cv2.INTER_CUBIC,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=0,
    )
    rendered = reconstruct_warped_rgba(warped_premultiplied, warped_alpha)
    rendered_alpha = np.asarray(rendered, dtype=np.uint8)[..., 3]
    visible_rows = np.flatnonzero(np.any(rendered_alpha >= 24, axis=1))
    if visible_rows.size == 0:
        raise AssertionError("Rigid reminder pose became fully transparent")
    rendered = shift_rgba_without_wrap(rendered, 539 - int(visible_rows[-1]))
    result = np.asarray(rendered, dtype=np.uint8).copy()
    result[result[..., 3] == 0] = 0
    return Image.fromarray(result, "RGBA")


def emit_rigid_reminder_sequence(
    source_path: Path,
    phase: str,
    angles: list[float],
) -> list[Path]:
    source = load_rgba(source_path)
    stage = WORK_ROOT / "reminder-rigid" / phase
    stage.mkdir(parents=True, exist_ok=True)
    staged: list[Path] = []
    for frame_number, angle in enumerate(angles, start=1):
        destination = stage / f"luban-reminder-{phase}-{frame_number:03d}.png"
        installer.save_png_atomically(
            render_reminder_rigid_pose(Image.fromarray(source, "RGBA"), angle),
            destination,
        )
        staged.append(destination)
    return emit_sequence(staged, f"luban-reminder-{phase}")


def build_reminder_enter_sequence(source_path: Path) -> list[Path]:
    angles = []
    for frame_index in range(REMINDER_ENTER_FRAME_COUNT):
        progress = frame_index / (REMINDER_ENTER_FRAME_COUNT - 1)
        angle = -1.0 * (1 - progress) ** 2 * math.cos(2.75 * math.pi * progress)
        angles.append(0.0 if frame_index == REMINDER_ENTER_FRAME_COUNT - 1 else angle)
    return emit_rigid_reminder_sequence(source_path, "enter", angles)


def build_reminder_hold_sequence(source_path: Path) -> list[Path]:
    # Sample 1..48 so the last frame is the exact central pose.  The entry
    # seam, the next queued reminder, and the runtime-reversed exit therefore
    # all begin with only one normal small sway step.
    angles = [
        1.45 * math.sin(2 * math.pi * frame_number / REMINDER_HOLD_FRAME_COUNT)
        for frame_number in range(1, REMINDER_HOLD_FRAME_COUNT + 1)
    ]
    angles[-1] = 0.0
    return emit_rigid_reminder_sequence(source_path, "hold", angles)


def build_reminder_exit_qa(enter: list[Path]) -> list[Path]:
    """Materialize but do not package the exact reverse-entry exit contract."""

    exit_directory = WORK_ROOT / "reminder-exit-qa"
    exit_directory.mkdir(parents=True, exist_ok=True)
    exit_paths: list[Path] = []
    for frame_number, source in enumerate(reversed(enter), start=1):
        destination = exit_directory / f"luban-reminder-exit-{frame_number:03d}.png"
        atomic_copy_png(source, destination)
        exit_paths.append(destination)
    for stale in exit_directory.glob("luban-reminder-exit-*.png"):
        suffix = stale.stem.rsplit("-", 1)[-1]
        if suffix.isdigit() and int(suffix) > len(exit_paths):
            stale.unlink()

    for index, exit_path in enumerate(exit_paths):
        if not pixel_equal(exit_path, enter[-1 - index]):
            raise AssertionError("Reminder exit is not an exact enter reversal")
    return exit_paths


def build_reminder_sequences() -> dict[str, list[Path]]:
    """Build single-silhouette reminder sways and a temporary reverse exit."""

    _, bridges = reminder_sources()
    source_path = bridges[7]
    enter = build_reminder_enter_sequence(source_path)
    hold = build_reminder_hold_sequence(source_path)
    exit_paths = build_reminder_exit_qa(enter)
    return {"enter": enter, "hold": hold, "exit-qa": exit_paths}


def basic_qa(
    paths: list[Path], report_path: Path, *, require_all_unique: bool = True
) -> None:
    records: list[dict[str, object]] = []
    pixel_hashes: set[str] = set()
    for path in paths:
        rgba = load_rgba(path)
        alpha = rgba[..., 3]
        red = rgba[..., 0].astype(np.int16)
        green = rgba[..., 1].astype(np.int16)
        blue = rgba[..., 2].astype(np.int16)
        pixel_hash = hashlib.sha256(rgba.tobytes()).hexdigest()
        pixel_hashes.add(pixel_hash)
        records.append(
            {
                "name": path.name,
                "sha256_pixels": pixel_hash,
                "green_dominant_visible": int(
                    ((alpha > 0) & (green > red + 8) & (green > blue + 8)).sum()
                ),
                "alpha0_nonzero_rgb": int(
                    ((alpha == 0) & np.any(rgba[..., :3] != 0, axis=2)).sum()
                ),
                "visible_pixels_alpha24": int((alpha >= 24).sum()),
            }
        )
    report = {
        "frame_count": len(paths),
        "unique_pixel_frames": len(pixel_hashes),
        "size": list(RUNTIME_SIZE),
        "max_green_dominant_visible": max(
            int(record["green_dominant_visible"]) for record in records
        ),
        "max_alpha0_nonzero_rgb": max(
            int(record["alpha0_nonzero_rgb"]) for record in records
        ),
        "frames": records,
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if require_all_unique and report["unique_pixel_frames"] != len(paths):
        raise AssertionError("dense sequence contains duplicate pixel frames")
    if report["max_green_dominant_visible"] != 0:
        raise AssertionError("dense sequence contains visible green fringe")
    if report["max_alpha0_nonzero_rgb"] != 0:
        raise AssertionError("dense sequence contains dirty transparent RGB")


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate dense 60fps motion PNGs")
    parser.add_argument(
        "--wake",
        action="store_true",
        help="Generate and adaptively refine the wake sequence",
    )
    parser.add_argument(
        "--adaptive-wake",
        action="store_true",
        help="Refine the current 53-frame wake base against visual motion caps",
    )
    parser.add_argument("--actions", action="store_true", help="Generate action smooth sets")
    parser.add_argument("--cute", action="store_true", help="Regenerate only cute smooth")
    parser.add_argument(
        "--adaptive-actions",
        action="store_true",
        help="Refine action edges against cap, baseline, and scale motion limits",
    )
    parser.add_argument(
        "--loops",
        action="store_true",
        help="Generate 48-frame cry/like/eat action loops",
    )
    parser.add_argument(
        "--edge-peek",
        action="store_true",
        help=(
            "Generate 48-frame closed left/top/bottom edge-peek sequences "
            "from the cached dense RIFE path"
        ),
    )
    parser.add_argument(
        "--reminder",
        action="store_true",
        help=(
            "Generate a 33-frame reminder entry and seamless 48-frame "
            "megaphone hold loop; exit is validated as the exact reverse."
        ),
    )
    parser.add_argument(
        "--clean-existing",
        action="store_true",
        help="Remove wide low-alpha RIFE trails without changing authored keys",
    )
    args = parser.parse_args()
    if not RIFE_EXE.exists() or not RIFE_MODEL.exists():
        raise FileNotFoundError(
            "RIFE tool/model is missing. Set XLB_RIFE_ROOT to the extracted "
            "rife-ncnn-vulkan directory that contains rife-ncnn-vulkan.exe "
            "and the rife-anime model folder. Current path: "
            f"{RIFE_ROOT}"
        )
    if (
        not args.wake
        and not args.adaptive_wake
        and not args.actions
        and not args.cute
        and not args.adaptive_actions
        and not args.loops
        and not args.edge_peek
        and not args.reminder
        and not args.clean_existing
    ):
        parser.error(
            "select --wake, --adaptive-wake, --actions, --cute, "
            "--adaptive-actions, --loops, --edge-peek, --reminder, and/or "
            "--clean-existing"
        )
    if args.wake:
        outputs = build_wake()
        basic_qa(outputs, WORK_ROOT / "qa-wake-base.json")
        outputs = build_adaptive_wake()
        basic_qa(outputs, WORK_ROOT / "qa-wake.json")
        print(f"QA: {WORK_ROOT / 'qa-wake.json'}", flush=True)
    if args.adaptive_wake:
        outputs = build_adaptive_wake()
        basic_qa(outputs, WORK_ROOT / "qa-wake.json")
        print(f"QA: {WORK_ROOT / 'qa-wake.json'}", flush=True)
    if args.actions:
        action_outputs = build_actions()
        for action, outputs in action_outputs.items():
            basic_qa(
                outputs,
                WORK_ROOT / f"qa-action-{action}.json",
                require_all_unique=False,
            )
        adaptive_outputs = build_adaptive_actions()
        for action, outputs in adaptive_outputs.items():
            basic_qa(
                outputs,
                WORK_ROOT / f"qa-action-{action}.json",
                require_all_unique=False,
            )
        print(f"QA: {WORK_ROOT / 'qa-action-*.json'}", flush=True)
    if args.cute:
        cute_outputs = build_actions(("cute",))["cute"]
        basic_qa(
            cute_outputs,
            WORK_ROOT / "qa-action-cute.json",
            require_all_unique=False,
        )
        cute_outputs = build_adaptive_actions(("cute",))["cute"]
        basic_qa(
            cute_outputs,
            WORK_ROOT / "qa-action-cute.json",
            require_all_unique=False,
        )
        print(f"QA: {WORK_ROOT / 'qa-action-cute.json'}", flush=True)
    if args.adaptive_actions:
        adaptive_outputs = build_adaptive_actions()
        for action, outputs in adaptive_outputs.items():
            basic_qa(
                outputs,
                WORK_ROOT / f"qa-action-{action}.json",
                require_all_unique=False,
            )
        print(f"QA: {WORK_ROOT / 'qa-action-*.json'}", flush=True)
    if args.loops:
        loop_outputs = build_loops()
        for action, outputs in loop_outputs.items():
            basic_qa(
                outputs,
                WORK_ROOT / f"qa-loop-{action}.json",
                require_all_unique=False,
            )
        print(f"QA: {WORK_ROOT / 'qa-loop-*.json'}", flush=True)
    if args.edge_peek:
        edge_outputs = build_edge_peek_sequences()
        for direction, outputs in edge_outputs.items():
            basic_qa(outputs, WORK_ROOT / f"qa-edge-{direction}.json")
        print(f"QA: {WORK_ROOT / 'qa-edge-*.json'}", flush=True)
    if args.reminder:
        reminder_outputs = build_reminder_sequences()
        basic_qa(
            reminder_outputs["enter"],
            WORK_ROOT / "qa-reminder-enter.json",
            require_all_unique=False,
        )
        basic_qa(
            reminder_outputs["hold"],
            WORK_ROOT / "qa-reminder-hold.json",
            require_all_unique=False,
        )
        basic_qa(
            reminder_outputs["exit-qa"],
            WORK_ROOT / "qa-reminder-exit.json",
            require_all_unique=False,
        )
        print(f"QA: {WORK_ROOT / 'qa-reminder-*.json'}", flush=True)
    if (
        args.wake
        or args.adaptive_wake
        or args.actions
        or args.cute
        or args.adaptive_actions
        or args.loops
        or args.edge_peek
        or args.clean_existing
    ):
        clean_existing_motion_assets()


if __name__ == "__main__":
    main()
