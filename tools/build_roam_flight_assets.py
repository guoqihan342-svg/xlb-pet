from __future__ import annotations

import argparse
from collections import deque
import json
import math
from pathlib import Path
import shutil
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
DEFAULT_SOURCE_DIRECTORY = ROOT / "tools" / "generated_sources"
WORK_DIRECTORY = ROOT / ".codex_tmp" / "roam-flight"
RUNTIME_SIZE = (450, 550)
CELL_SIZE = (450, 450)
CELL_TOP = 50
BOARDING_REFINEMENT_ROUNDS = 3
MIN_BOARDING_FRAME_COUNT = 90

sys.path.insert(0, str(ROOT / "tools"))
import generate_dense_motion_assets as dense  # noqa: E402
import install_generated_motion_assets as installer  # noqa: E402


def clear_fully_transparent_rgb(image: Image.Image) -> Image.Image:
    """Clear hidden RGB without changing any visible or antialiased colour."""

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    rgba[rgba[..., 3] == 0] = 0
    return Image.fromarray(rgba, "RGBA")


def solidify_visible_interior_alpha(image: Image.Image) -> Image.Image:
    """Make sprite interiors opaque while preserving the antialiased outline."""

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    alpha = rgba[..., 3]
    visible = alpha > 8
    interior = np.zeros_like(visible)
    interior[1:-1, 1:-1] = visible[1:-1, 1:-1]
    for y_offset in (-1, 0, 1):
        for x_offset in (-1, 0, 1):
            if not y_offset and not x_offset:
                continue
            interior[1:-1, 1:-1] &= visible[
                1 + y_offset : visible.shape[0] - 1 + y_offset,
                1 + x_offset : visible.shape[1] - 1 + x_offset,
            ]
    rgba[..., 3][interior] = 255
    rgba[rgba[..., 3] == 0] = 0
    return Image.fromarray(rgba, "RGBA")


def save_png(image: Image.Image, destination: Path) -> None:
    """Atomically save RGBA without recolouring visible bamboo pixels."""

    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(f".{destination.stem}.tmp.png")
    clear_fully_transparent_rgb(image).save(
        temporary,
        format="PNG",
        optimize=True,
    )
    temporary.replace(destination)


def find_alpha_components(mask: np.ndarray) -> list[list[int]]:
    """Return 8-connected flat indexes for every visible alpha component."""

    height, width = mask.shape
    visited = np.zeros((height, width), dtype=np.bool_)
    components: list[list[int]] = []
    for start_y in range(height):
        for start_x in range(width):
            if not mask[start_y, start_x] or visited[start_y, start_x]:
                continue
            queue: deque[tuple[int, int]] = deque([(start_y, start_x)])
            visited[start_y, start_x] = True
            component: list[int] = []
            while queue:
                y, x = queue.popleft()
                component.append(y * width + x)
                for next_y in range(max(0, y - 1), min(height, y + 2)):
                    for next_x in range(max(0, x - 1), min(width, x + 2)):
                        if (
                            not visited[next_y, next_x]
                            and mask[next_y, next_x]
                        ):
                            visited[next_y, next_x] = True
                            queue.append((next_y, next_x))
            components.append(component)
    return components


def remove_small_alpha_components(
    image: Image.Image,
) -> tuple[Image.Image, dict[str, float | int]]:
    """Remove cross-cell fragments using an 8-connected alpha BFS."""

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    mask = rgba[..., 3] > 0
    components = find_alpha_components(mask)

    if not components:
        raise ValueError("cannot clean a fully transparent sprite cell")
    largest_area = max(len(component) for component in components)
    minimum_retained_area = largest_area * 0.10
    retained = [
        component
        for component in components
        if len(component) >= minimum_retained_area
    ]
    discarded = [
        component
        for component in components
        if len(component) < minimum_retained_area
    ]
    flattened = rgba.reshape((-1, 4))
    for component in discarded:
        flattened[np.asarray(component, dtype=np.int64)] = 0
    minimum_ratio = min(
        len(component) / largest_area for component in retained
    )
    return (
        clear_fully_transparent_rgb(Image.fromarray(rgba, "RGBA")),
        {
            "source_component_count": len(components),
            "retained_component_count": len(retained),
            "removed_component_count": len(discarded),
            "removed_pixel_count": sum(map(len, discarded)),
            "largest_component_area": largest_area,
            "minimum_retained_area_ratio": minimum_ratio,
        },
    )


def split_registered_sheet(
    source: Path,
    label: str,
) -> tuple[list[Path], list[dict[str, float | int | str]]]:
    if not source.is_file():
        raise FileNotFoundError(source)

    destination = WORK_DIRECTORY / "keys" / label
    destination.mkdir(parents=True, exist_ok=True)
    keys: list[Path] = []
    component_metrics: list[dict[str, float | int | str]] = []
    with Image.open(source) as opened:
        sheet = opened.convert("RGBA")
        # Some generated sheets are not divisible by four. Resizing each
        # rounded 313/314 px cell independently would feed an alternating
        # ~0.3% scale change into the character. Register the complete sheet
        # once, then crop sixteen exactly equal cells from one shared scale.
        registered_sheet = installer.resize_rgba_premultiplied(
            sheet,
            (CELL_SIZE[0] * 4, CELL_SIZE[1] * 4),
        )
        for row in range(4):
            for column in range(4):
                left = column * CELL_SIZE[0]
                top = row * CELL_SIZE[1]
                registered_cell = registered_sheet.crop(
                    (
                        left,
                        top,
                        left + CELL_SIZE[0],
                        top + CELL_SIZE[1],
                    )
                )
                canvas = Image.new("RGBA", RUNTIME_SIZE, (0, 0, 0, 0))
                canvas.alpha_composite(registered_cell, (0, CELL_TOP))
                frame_number = row * 4 + column + 1
                canvas, frame_component_metrics = (
                    remove_small_alpha_components(canvas)
                )
                destination_path = destination / f"{frame_number:03d}.png"
                save_png(canvas, destination_path)
                keys.append(destination_path)
                component_metrics.append(
                    {
                        "sequence": label,
                        "key": frame_number,
                        **frame_component_metrics,
                    }
                )

    if len(keys) != 16:
        raise AssertionError(f"{label} sheet emitted {len(keys)} keys")
    return keys, component_metrics


def brim_center_image(image: Image.Image) -> tuple[float, float]:
    box = installer.get_blue_brim_box(image.convert("RGBA"))
    return ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2)


def brim_center(path: Path) -> tuple[float, float]:
    with Image.open(path) as opened:
        return brim_center_image(opened)


def translate_without_scaling(
    image: Image.Image,
    dx: int,
    dy: int,
) -> tuple[Image.Image, int, int]:
    source = clear_fully_transparent_rgb(image)
    alpha_box = source.getchannel("A").getbbox()
    if alpha_box is None:
        raise ValueError("cannot register a fully transparent pose")

    dx = max(-alpha_box[0], min(dx, source.width - alpha_box[2]))
    dy = max(-alpha_box[1], min(dy, source.height - alpha_box[3]))
    translated = Image.new("RGBA", source.size, (0, 0, 0, 0))
    translated.alpha_composite(source, (dx, dy))
    source_alpha_sum = int(
        np.asarray(source.getchannel("A"), dtype=np.uint64).sum()
    )
    translated_alpha_sum = int(
        np.asarray(translated.getchannel("A"), dtype=np.uint64).sum()
    )
    if source_alpha_sum != translated_alpha_sum:
        raise AssertionError("pose registration clipped visible alpha")
    return clear_fully_transparent_rgb(translated), dx, dy


def large_alpha_component_images(image: Image.Image) -> list[Image.Image]:
    """Split the retained characters without resizing or recolouring them."""

    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    components = find_alpha_components(rgba[..., 3] > 0)
    if not components:
        raise ValueError("cannot split a fully transparent pose")
    largest_area = max(map(len, components))
    retained = [
        component
        for component in components
        if len(component) >= largest_area * 0.10
    ]
    flattened_source = rgba.reshape((-1, 4))
    layers: list[Image.Image] = []
    for component in retained:
        flattened_layer = np.zeros_like(flattened_source)
        indexes = np.asarray(component, dtype=np.int64)
        flattened_layer[indexes] = flattened_source[indexes]
        layers.append(
            clear_fully_transparent_rgb(
                Image.fromarray(
                    flattened_layer.reshape(rgba.shape),
                    "RGBA",
                )
            )
        )
    return layers


def register_pose_to_brim(
    source: Path,
    destination: Path,
    target: tuple[float, float],
) -> Path:
    """Translate one complete pose to the requested cap position."""

    with Image.open(source) as opened:
        pose = opened.convert("RGBA")
    current = brim_center_image(pose)
    requested_dx = round(target[0] - current[0])
    requested_dy = round(target[1] - current[1])
    translated, actual_dx, actual_dy = translate_without_scaling(
        pose,
        requested_dx,
        requested_dy,
    )
    if (actual_dx, actual_dy) != (requested_dx, requested_dy):
        raise AssertionError(
            f"{source.name} boarding registration clipped: "
            f"requested=({requested_dx},{requested_dy}), "
            f"actual=({actual_dx},{actual_dy})"
        )
    save_png(translated, destination)
    actual = brim_center(destination)
    if math.dist(actual, target) > 0.51:
        raise AssertionError(
            f"{source.name} boarding brim missed {target}: {actual}"
        )
    return destination


def compose_panda_entry_pose(
    source: Path,
    destination: Path,
    luban_target: tuple[float, float],
) -> Path:
    """Keep the entering panda in place while registering Luban separately."""

    with Image.open(source) as opened:
        panda, luban = split_panda_and_luban_layers(opened.convert("RGBA"))
    current = brim_center_image(luban)
    requested_dx = round(luban_target[0] - current[0])
    requested_dy = round(luban_target[1] - current[1])
    registered_luban, actual_dx, actual_dy = translate_without_scaling(
        luban,
        requested_dx,
        requested_dy,
    )
    if (actual_dx, actual_dy) != (requested_dx, requested_dy):
        raise AssertionError(
            f"{source.name} Luban entry registration clipped"
        )

    composite = Image.new("RGBA", RUNTIME_SIZE, (0, 0, 0, 0))
    composite.alpha_composite(panda)
    composite.alpha_composite(registered_luban)
    save_png(composite, destination)
    actual = brim_center(destination)
    if math.dist(actual, luban_target) > 0.51:
        raise AssertionError(
            f"{source.name} composed brim missed {luban_target}: {actual}"
        )
    return destination


def split_panda_and_luban_layers(
    image: Image.Image,
) -> tuple[Image.Image, Image.Image]:
    layers = large_alpha_component_images(image)
    if len(layers) != 2:
        raise AssertionError(
            "entry pose must contain panda and Luban, "
            f"found {len(layers)} retained components"
        )

    luban_candidates: list[tuple[Image.Image, tuple[float, float]]] = []
    panda_candidates: list[Image.Image] = []
    for layer in layers:
        try:
            luban_candidates.append((layer, brim_center_image(layer)))
        except ValueError:
            panda_candidates.append(layer)
    if len(luban_candidates) != 1 or len(panda_candidates) != 1:
        raise AssertionError("could not separate panda and Luban")
    return panda_candidates[0], luban_candidates[0][0]


def translate_allow_clipping(
    image: Image.Image,
    dx: int,
    dy: int,
) -> Image.Image:
    translated = Image.new("RGBA", image.size, (0, 0, 0, 0))
    translated.alpha_composite(
        clear_fully_transparent_rgb(image),
        (dx, dy),
    )
    return clear_fully_transparent_rgb(translated)


def extract_luban_layer(image: Image.Image) -> Image.Image:
    candidates: list[tuple[int, Image.Image]] = []
    for layer in large_alpha_component_images(image):
        try:
            brim_center_image(layer)
        except ValueError:
            continue
        alpha_area = int(
            (np.asarray(layer.getchannel("A"), dtype=np.uint8) > 0).sum()
        )
        candidates.append((alpha_area, layer))
    if not candidates:
        raise AssertionError("dense boarding frame lost Luban's cap")
    return max(candidates, key=lambda item: item[0])[1]


def repair_panda_entry_transition(
    sequence: list[Path],
    *,
    key_interval_index: int,
    base_segments_per_key: int,
    key_interval_count: int,
) -> list[Path]:
    """Replace RIFE's topology-change blob with a real sliding panda layer."""

    start = key_interval_index * base_segments_per_key
    end = start + key_interval_count * base_segments_per_key
    source_transition = sequence[start : end + 1]
    if len(source_transition) != (
        key_interval_count * base_segments_per_key + 1
    ):
        raise AssertionError("panda entry transition slice is incomplete")

    work = WORK_DIRECTORY / "refined" / "roam-boarding-entry-repair"
    work.mkdir(parents=True, exist_ok=True)
    clean_luban_layers: list[Image.Image] = []
    for interval_offset in range(key_interval_count):
        path = source_transition[interval_offset * base_segments_per_key]
        with Image.open(path) as opened:
            clean_luban_layers.append(
                extract_luban_layer(opened.convert("RGBA"))
            )
    with Image.open(source_transition[-1]) as opened:
        panda, end_luban = split_panda_and_luban_layers(opened.convert("RGBA"))
    clean_luban_layers.append(end_luban)

    clean_luban_keys: list[Path] = []
    for frame_number, luban in enumerate(clean_luban_layers, start=1):
        destination = work / "luban-keys" / f"{frame_number:03d}.png"
        save_png(luban, destination)
        clean_luban_keys.append(destination)

    # Interpolate only between three clean Luban key poses. The panda itself
    # never enters RIFE: one real opaque panda layer slides in from outside
    # the canvas, preventing topology-change black ghosts while keeping the
    # 121-frame runtime timing contract.
    refined_luban = clean_luban_keys
    for round_number in range(1, 4):
        refined_luban = refine_sequence(
            refined_luban,
            label="roam-boarding-entry-luban-span2",
            round_number=round_number,
        )
    refined_luban = solidify_interpolated_sequence(
        refined_luban,
        key_stride=8,
        label="roam-boarding-entry-luban-span2",
    )
    if len(refined_luban) != (
        key_interval_count * base_segments_per_key + 1
    ):
        raise AssertionError(
            f"clean Luban entry emitted {len(refined_luban)} frames"
        )
    panda_box = panda.getchannel("A").getbbox()
    if panda_box is None:
        raise AssertionError("panda entry endpoint lost its panda layer")
    hidden_dx = -panda_box[2]

    repaired: list[Path] = []
    last_index = len(refined_luban) - 1
    for index, luban_path in enumerate(refined_luban):
        progress = index / last_index
        panda_dx = round(hidden_dx * (1.0 - progress))
        with Image.open(luban_path) as opened:
            luban = opened.convert("RGBA")
        composite = Image.new("RGBA", RUNTIME_SIZE, (0, 0, 0, 0))
        composite.alpha_composite(
            translate_allow_clipping(panda, panda_dx, 0)
        )
        composite.alpha_composite(luban)
        destination = work / "composite" / f"{index + 1:03d}.png"
        save_png(composite, destination)
        repaired.append(destination)

    if pixel_bytes(repaired[0]) != pixel_bytes(source_transition[0]):
        raise AssertionError("panda entry repair changed its starting key")
    if pixel_bytes(repaired[-1]) != pixel_bytes(source_transition[-1]):
        raise AssertionError("panda entry repair changed its ending key")
    return [*sequence[:start], *repaired, *sequence[end + 1 :]]


def build_registered_boarding_keys(
    generated_keys: list[Path],
    flight_first: Path,
) -> tuple[list[Path], list[str]]:
    """Build a continuous idle -> panda entry -> mounted flight key path."""

    destination = WORK_DIRECTORY / "keys" / "boarding-continuous"
    destination.mkdir(parents=True, exist_ok=True)
    keys: list[Path] = [ASSETS / "luban-idle.png"]
    labels = ["idle"]

    complete_pose_targets = (
        (1, (195.5, 305.5)),
        (2, (218.0, 278.5)),
        (3, (241.0, 252.0)),
        (4, (264.5, 225.0)),
    )
    for source_number, target in complete_pose_targets:
        keys.append(
            register_pose_to_brim(
                generated_keys[source_number - 1],
                destination / f"{len(keys) + 1:03d}.png",
                target,
            )
        )
        labels.append(f"entry{source_number}")

    separated_entry_targets = (
        (5, (280.0, 225.5)),
        (6, (285.0, 226.0)),
        (7, (290.0, 227.5)),
        (8, (295.0, 228.5)),
    )
    for source_number, target in separated_entry_targets:
        keys.append(
            compose_panda_entry_pose(
                generated_keys[source_number - 1],
                destination / f"{len(keys) + 1:03d}.png",
                target,
            )
        )
        labels.append(f"panda{source_number}")

    mounted_pose_targets = (
        (9, (300.0, 229.0), "greet"),
        (10, (294.0, 213.5), "climb10a"),
        (10, (288.0, 199.5), "climb10b"),
        (11, (280.0, 184.5), "mount11a"),
        (11, (270.0, 169.5), "mount11b"),
    )
    for source_number, target, label in mounted_pose_targets:
        keys.append(
            register_pose_to_brim(
                generated_keys[source_number - 1],
                destination / f"{len(keys) + 1:03d}.png",
                target,
            )
        )
        labels.append(label)

    keys.append(
        register_pose_to_brim(
            flight_first,
            destination / f"{len(keys) + 1:03d}.png",
            (255.5, 154.5),
        )
    )
    labels.append("flight-approach")
    keys.append(flight_first)
    labels.append("flight")
    if len(keys) != 16:
        raise AssertionError(
            f"continuous boarding path must contain 16 keys, found {len(keys)}"
        )
    return keys, labels


def register_loop_key_sequences(
    primary_keys: list[Path],
    secondary_keys: list[Path] | None = None,
) -> dict[str, dict[str, float]]:
    """Remove 4x4-cell placement drift using translation only."""

    target_x, target_y = brim_center(primary_keys[0])
    metrics: dict[str, dict[str, float]] = {}
    groups = [("primary", primary_keys)]
    if secondary_keys:
        groups.append(("secondary", secondary_keys))
    for label, keys in groups:
        clamped_translations = 0
        for index, path in enumerate(keys):
            current_x, current_y = brim_center(path)
            bob = round(3 * math.sin(math.tau * index / len(keys)))
            requested_dx = round(target_x - current_x)
            requested_dy = round(target_y + bob - current_y)
            with Image.open(path) as opened:
                translated, actual_dx, actual_dy = translate_without_scaling(
                    opened.convert("RGBA"),
                    requested_dx,
                    requested_dy,
                )
            clamped_translations += int(
                actual_dx != requested_dx or actual_dy != requested_dy
            )
            save_png(translated, path)

        centers = [brim_center(path) for path in keys]
        x_errors = [abs(center[0] - target_x) for center in centers]
        y_errors = [
            abs(
                center[1]
                - (
                    target_y
                    + round(3 * math.sin(math.tau * index / len(keys)))
                )
            )
            for index, center in enumerate(centers)
        ]
        metrics[label] = {
            "target_x": target_x,
            "target_y": target_y,
            "maximum_x_error_px": max(x_errors),
            "maximum_bob_error_px": max(y_errors),
            "clamped_translations": float(clamped_translations),
        }
        if max(x_errors) > 0.5 or max(y_errors) > 0.5:
            raise AssertionError(
                f"{label} brim registration missed target: "
                f"x={max(x_errors):.3f}px, y={max(y_errors):.3f}px"
            )
        if clamped_translations:
            raise AssertionError(
                f"{label} needed {clamped_translations} clamped translations"
            )
    return metrics


def reconstruct_rife_midpoint(
    premultiplied_path: Path,
    alpha_path: Path,
    *,
    solidify_interior: bool,
) -> Image.Image:
    """Reconstruct one RIFE midpoint while preserving bamboo greens exactly."""

    with Image.open(premultiplied_path) as opened:
        premultiplied = np.asarray(opened.convert("RGB"), dtype=np.uint32)
    with Image.open(alpha_path) as opened:
        alpha_rgb = np.asarray(opened.convert("RGB"), dtype=np.uint16)

    channel_spread = int(
        (alpha_rgb.max(axis=2) - alpha_rgb.min(axis=2)).max(initial=0)
    )
    if channel_spread > 64:
        raise ValueError(
            f"RIFE alpha channels diverged by {channel_spread}"
        )

    alpha = np.rint(
        alpha_rgb.astype(np.float32).mean(axis=2)
    ).astype(np.uint32)
    rgb = np.zeros_like(premultiplied, dtype=np.uint32)
    visible = alpha > 0
    rgb[visible] = np.minimum(
        255,
        (
            premultiplied[visible] * 255
            + alpha[visible, None] // 2
        )
        // alpha[visible, None],
    )
    rgba = np.concatenate((rgb, alpha[..., None]), axis=2).astype(np.uint8)
    rgba[~visible] = 0
    reconstructed = clear_fully_transparent_rgb(
        Image.fromarray(rgba, "RGBA")
    )
    return (
        solidify_visible_interior_alpha(reconstructed)
        if solidify_interior
        else reconstructed
    )


def copy_png_atomically(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + ".tmp")
    shutil.copyfile(source, temporary)
    temporary.replace(destination)


def refine_sequence(
    keys: list[Path],
    *,
    label: str,
    round_number: int,
    solidify_midpoints: bool = False,
) -> list[Path]:
    """Insert one RIFE midpoint between every adjacent pair."""

    premultiplied, alpha = dense.run_rife_double(
        keys,
        f"roam-panda-{label}-round-{round_number}",
    )
    destination = (
        WORK_DIRECTORY
        / "refined"
        / label
        / f"round-{round_number}"
    )
    destination.mkdir(parents=True, exist_ok=True)
    refined: list[Path] = []
    frame_number = 1
    for index, key in enumerate(keys):
        key_destination = destination / f"{frame_number:03d}.png"
        copy_png_atomically(key, key_destination)
        refined.append(key_destination)
        frame_number += 1
        if index == len(keys) - 1:
            continue

        midpoint_destination = destination / f"{frame_number:03d}.png"
        save_png(
            reconstruct_rife_midpoint(
                premultiplied[index * 2 + 1],
                alpha[index * 2 + 1],
                solidify_interior=solidify_midpoints,
            ),
            midpoint_destination,
        )
        refined.append(midpoint_destination)
        frame_number += 1

    expected = (len(keys) - 1) * 2 + 1
    if len(refined) != expected:
        raise AssertionError(
            f"{label} refinement round {round_number} emitted "
            f"{len(refined)} frames, expected {expected}"
        )
    return refined


def solidify_interpolated_sequence(
    sequence: list[Path],
    *,
    key_stride: int,
    label: str,
) -> list[Path]:
    """Solidify only generated poses; authored endpoint bytes stay untouched."""

    destination = WORK_DIRECTORY / "refined" / label / "solidified"
    solidified: list[Path] = []
    for index, path in enumerate(sequence):
        if index % key_stride == 0:
            solidified.append(path)
            continue
        with Image.open(path) as opened:
            frame = solidify_visible_interior_alpha(opened.convert("RGBA"))
        output = destination / f"{index + 1:03d}.png"
        save_png(frame, output)
        solidified.append(output)
    return solidified


def emit_runtime_sequence(
    sequence: list[Path],
    *,
    asset_prefix: str,
) -> list[Path]:
    outputs: list[Path] = []
    for frame_number, source in enumerate(sequence, start=1):
        destination = ASSETS / f"{asset_prefix}-{frame_number:03d}.png"
        copy_png_atomically(source, destination)
        outputs.append(destination)

    resolved_assets = ASSETS.resolve()
    for stale in ASSETS.glob(f"{asset_prefix}-*.png"):
        if stale.resolve().parent != resolved_assets:
            raise RuntimeError(f"refusing to remove outside Assets: {stale}")
        suffix = stale.stem.rsplit("-", 1)[-1]
        if suffix.isdigit() and int(suffix) > len(outputs):
            stale.unlink()
    return outputs


def remove_runtime_sequence(asset_prefix: str) -> None:
    emit_runtime_sequence([], asset_prefix=asset_prefix)


def emit_dense_loop(
    keys: list[Path],
    *,
    label: str,
    asset_prefix: str,
) -> list[Path]:
    # Append the first key as a construction endpoint. Two recursive RIFE
    # subdivisions produce three real in-between poses for every authored
    # edge. Drop the repeated final endpoint so runtime frame 001 remains the
    # exact first authored pose; boarding can then hand off byte-for-byte.
    sequence = [*keys, keys[0]]
    for round_number in range(1, 3):
        sequence = refine_sequence(
            sequence,
            label=label,
            round_number=round_number,
        )
    sequence = solidify_interpolated_sequence(
        sequence,
        key_stride=4,
        label=label,
    )
    dense_sequence = sequence[:-1]
    if len(dense_sequence) != 64:
        raise AssertionError(
            f"{label} dense loop emitted {len(dense_sequence)} frames"
        )
    return emit_runtime_sequence(
        dense_sequence,
        asset_prefix=asset_prefix,
    )


def emit_dense_non_loop(
    keys: list[Path],
    *,
    label: str,
    asset_prefix: str,
    refinement_rounds: int,
    minimum_frame_count: int,
    panda_entry_key_interval: int | None = None,
    panda_entry_key_interval_count: int = 1,
) -> list[Path]:
    sequence = keys
    for round_number in range(1, refinement_rounds + 1):
        sequence = refine_sequence(
            sequence,
            label=label,
            round_number=round_number,
        )
    base_segments_per_key = 2 ** refinement_rounds
    sequence = solidify_interpolated_sequence(
        sequence,
        key_stride=base_segments_per_key,
        label=label,
    )
    expected_count = (len(keys) - 1) * base_segments_per_key + 1
    if len(sequence) != expected_count:
        raise AssertionError(
            f"{label} dense path emitted {len(sequence)} frames, "
            f"expected {expected_count}"
        )
    if len(sequence) < minimum_frame_count:
        raise AssertionError(
            f"{label} needs at least {minimum_frame_count} frames, "
            f"found {len(sequence)}"
        )
    if panda_entry_key_interval is not None:
        sequence = repair_panda_entry_transition(
            sequence,
            key_interval_index=panda_entry_key_interval,
            base_segments_per_key=base_segments_per_key,
            key_interval_count=panda_entry_key_interval_count,
        )
        if len(sequence) != expected_count:
            raise AssertionError(
                f"{label} panda entry repair emitted {len(sequence)} frames, "
                f"expected {expected_count}"
            )
    return emit_runtime_sequence(sequence, asset_prefix=asset_prefix)


def pixel_bytes(path: Path) -> bytes:
    with Image.open(path) as opened:
        return opened.convert("RGBA").tobytes()


def bamboo_green_metrics(paths: list[Path]) -> dict[str, int]:
    total_pixels = 0
    frames_with_green = 0
    unique_colours: set[tuple[int, int, int]] = set()
    for path in paths:
        with Image.open(path) as opened:
            rgba = np.asarray(opened.convert("RGBA"), dtype=np.uint8)
        red = rgba[..., 0].astype(np.int16)
        green = rgba[..., 1].astype(np.int16)
        blue = rgba[..., 2].astype(np.int16)
        mask = (
            (rgba[..., 3] >= 32)
            & (green >= 48)
            # The authored bamboo is an olive green: red and green are often
            # equal, while blue is distinctly lower. Requiring green > red
            # would incorrectly report that preserved bamboo as absent.
            & (np.abs(green - red) <= 36)
            & (green >= blue + 12)
        )
        count = int(mask.sum())
        total_pixels += count
        frames_with_green += int(count > 0)
        if count:
            colours = rgba[..., :3][mask]
            unique_colours.update(map(tuple, colours.tolist()))
    return {
        "green_pixels": total_pixels,
        "frames_with_green": frames_with_green,
        "unique_green_colours": len(unique_colours),
    }


def brim_motion_metrics(paths: list[Path], *, loop: bool) -> dict[str, float]:
    centers = [brim_center(path) for path in paths]
    adjacent = [
        math.hypot(
            centers[index + 1][0] - centers[index][0],
            centers[index + 1][1] - centers[index][1],
        )
        for index in range(len(centers) - 1)
    ]
    seam = (
        math.hypot(
            centers[0][0] - centers[-1][0],
            centers[0][1] - centers[-1][1],
        )
        if loop
        else 0.0
    )
    source_pixel_to_dip = 190 / RUNTIME_SIZE[0]
    return {
        "maximum_adjacent_px": max(adjacent, default=0.0),
        "maximum_adjacent_dip": max(adjacent, default=0.0)
        * source_pixel_to_dip,
        "seam_px": seam,
        "seam_dip": seam * source_pixel_to_dip,
    }


def alpha_iou(first: Path, second: Path) -> float:
    with Image.open(first) as opened:
        first_alpha = np.asarray(
            opened.convert("RGBA").getchannel("A"),
            dtype=np.uint8,
        ) > 8
    with Image.open(second) as opened:
        second_alpha = np.asarray(
            opened.convert("RGBA").getchannel("A"),
            dtype=np.uint8,
        ) > 8
    union = int(np.logical_or(first_alpha, second_alpha).sum())
    if not union:
        return 1.0
    return float(np.logical_and(first_alpha, second_alpha).sum()) / union


def alpha_centroid(path: Path) -> tuple[float, float]:
    with Image.open(path) as opened:
        alpha = np.asarray(
            opened.convert("RGBA").getchannel("A"),
            dtype=np.float64,
        )
    weight = float(alpha.sum())
    if weight <= 0:
        raise ValueError(f"{path.name} is fully transparent")
    y_indexes, x_indexes = np.indices(alpha.shape, dtype=np.float64)
    return (
        float((x_indexes * alpha).sum() / weight),
        float((y_indexes * alpha).sum() / weight),
    )


def interior_translucent_ratio(path: Path) -> float:
    """Measure soft-alpha pixels away from normal antialiased outer edges."""

    with Image.open(path) as opened:
        alpha = np.asarray(
            opened.convert("RGBA").getchannel("A"),
            dtype=np.uint8,
        )
    visible = alpha > 8
    interior = visible.copy()
    interior[0, :] = False
    interior[-1, :] = False
    interior[:, 0] = False
    interior[:, -1] = False
    for y_offset in (-1, 0, 1):
        for x_offset in (-1, 0, 1):
            if not y_offset and not x_offset:
                continue
            shifted = np.zeros_like(visible)
            source_y = slice(max(0, -y_offset), min(alpha.shape[0], alpha.shape[0] - y_offset))
            source_x = slice(max(0, -x_offset), min(alpha.shape[1], alpha.shape[1] - x_offset))
            target_y = slice(max(0, y_offset), min(alpha.shape[0], alpha.shape[0] + y_offset))
            target_x = slice(max(0, x_offset), min(alpha.shape[1], alpha.shape[1] + x_offset))
            shifted[target_y, target_x] = visible[source_y, source_x]
            interior &= shifted
    ghost = interior & (alpha >= 16) & (alpha <= 239)
    visible_count = int(visible.sum())
    return float(ghost.sum()) / visible_count if visible_count else 0.0


def opaque_black_metrics(paths: list[Path]) -> dict[str, float | int]:
    """Reject the opaque black rectangles produced by failed RIFE topology."""

    worst_count = 0
    worst_ratio = 0.0
    worst_frame = 0
    for frame_number, path in enumerate(paths, start=1):
        with Image.open(path) as opened:
            rgba = np.asarray(opened.convert("RGBA"), dtype=np.uint8)
        opaque_black = (
            (rgba[..., 3] >= 240)
            & (rgba[..., :3].max(axis=2) <= 5)
        )
        count = int(opaque_black.sum())
        ratio = count / opaque_black.size
        if count > worst_count:
            worst_count = count
            worst_ratio = ratio
            worst_frame = frame_number
        if count > 500 and ratio > 0.005:
            raise AssertionError(
                f"{path.name} contains an opaque-black RIFE block: "
                f"{count} px/{ratio:.4%}"
            )
    return {
        "maximum_count": worst_count,
        "maximum_ratio": worst_ratio,
        "worst_frame": worst_frame,
    }


def strong_brim_candidates(
    path: Path,
) -> list[tuple[float, tuple[float, float]]]:
    """Find high-alpha blue cap regions, excluding interpolation fragments."""

    with Image.open(path) as opened:
        rgba = np.asarray(opened.convert("RGBA"), dtype=np.uint8)
    red = rgba[..., 0].astype(np.float32)
    green = rgba[..., 1].astype(np.float32)
    blue = rgba[..., 2].astype(np.float32)
    mask = (
        (rgba[..., 3] >= 160)
        & (blue >= 95)
        & (green >= 55)
        & (blue >= red * 1.22)
        & (blue >= green * 1.08)
    )
    width = rgba.shape[1]
    useful: list[tuple[float, tuple[float, float]]] = []
    for component in find_alpha_components(mask):
        indexes = np.asarray(component, dtype=np.int64)
        y_indexes = indexes // width
        x_indexes = indexes % width
        left = int(x_indexes.min())
        right = int(x_indexes.max()) + 1
        top = int(y_indexes.min())
        bottom = int(y_indexes.max()) + 1
        if (
            len(component) >= 18
            and right - left >= 12
            and top < rgba.shape[0] * 0.58
        ):
            useful.append(
                (
                    len(component) * max(1, right - left),
                    ((left + right) / 2, (top + bottom) / 2),
                )
            )
    return useful


def tracked_strong_brim_centers(paths: list[Path]) -> list[tuple[float, float]]:
    centers: list[tuple[float, float]] = []
    for path in paths:
        candidates = strong_brim_candidates(path)
        if not candidates:
            center = brim_center(path)
        elif not centers:
            center = max(candidates, key=lambda item: item[0])[1]
        else:
            maximum_score = max(score for score, _ in candidates)
            credible = [
                (score, center)
                for score, center in candidates
                if score >= maximum_score * 0.15
            ]
            center = min(
                credible,
                key=lambda item: (
                    math.dist(centers[-1], item[1]),
                    -item[0],
                ),
            )[1]
        centers.append(center)
    return centers


def boarding_transition_metrics(
    paths: list[Path],
) -> tuple[dict[str, float], list[dict[str, float]]]:
    centers = tracked_strong_brim_centers(paths)
    centroids = [alpha_centroid(path) for path in paths]
    transitions = [
        {
            "head_step_px": math.dist(
                centers[index],
                centers[index + 1],
            ),
            "centroid_step_px": math.dist(
                centroids[index],
                centroids[index + 1],
            ),
            "alpha_iou": alpha_iou(paths[index], paths[index + 1]),
        }
        for index in range(len(paths) - 1)
    ]
    brim_steps = [transition["head_step_px"] for transition in transitions]
    centroid_steps = [
        transition["centroid_step_px"] for transition in transitions
    ]
    alpha_ious = [transition["alpha_iou"] for transition in transitions]
    return (
        {
            "maximum_brim_step_px": max(brim_steps, default=0.0),
            "mean_brim_step_px": (
                sum(brim_steps) / len(brim_steps) if brim_steps else 0.0
            ),
            "maximum_centroid_step_px": max(centroid_steps, default=0.0),
            "mean_centroid_step_px": (
                sum(centroid_steps) / len(centroid_steps)
                if centroid_steps
                else 0.0
            ),
            "minimum_alpha_iou": min(alpha_ious, default=1.0),
            "mean_alpha_iou": (
                sum(alpha_ious) / len(alpha_ious) if alpha_ious else 1.0
            ),
        },
        transitions,
    )


def checkerboard(size: tuple[int, int], cell: int = 12) -> Image.Image:
    width, height = size
    y_indexes, x_indexes = np.indices((height, width))
    squares = ((x_indexes // cell + y_indexes // cell) % 2).astype(np.uint8)
    light = np.full((height, width, 4), (246, 248, 252, 255), dtype=np.uint8)
    dark = np.full((height, width, 4), (222, 228, 238, 255), dtype=np.uint8)
    return Image.fromarray(np.where(squares[..., None] == 0, light, dark), "RGBA")


def write_boarding_contact_sheet(
    paths: list[Path],
    transitions: list[dict[str, float]],
    ghost_ratios: list[float],
) -> Path:
    """Write the twelve most suspicious adjacent frame pairs for review."""

    selected = sorted(
        range(len(transitions)),
        key=lambda index: (
            transitions[index]["head_step_px"]
            + transitions[index]["centroid_step_px"]
            + (1.0 - transitions[index]["alpha_iou"]) * 20.0
            + max(ghost_ratios[index], ghost_ratios[index + 1]) * 100.0
        ),
        reverse=True,
    )[:12]
    columns = 2
    tile_width = 590
    tile_height = 290
    rows = math.ceil(len(selected) / columns)
    sheet = Image.new(
        "RGBA",
        (columns * tile_width, rows * tile_height),
        (244, 247, 252, 255),
    )
    draw = ImageDraw.Draw(sheet)
    font_path = Path(r"C:\Windows\Fonts\msyh.ttc")
    font = (
        ImageFont.truetype(str(font_path), 14)
        if font_path.is_file()
        else ImageFont.load_default()
    )
    for cell_index, transition_index in enumerate(selected):
        x = (cell_index % columns) * tile_width
        y = (cell_index // columns) * tile_height
        background = checkerboard((370, 220))
        for side, path in enumerate(
            (paths[transition_index], paths[transition_index + 1])
        ):
            with Image.open(path) as opened:
                sprite = opened.convert("RGBA").resize(
                    (180, 220),
                    Image.Resampling.LANCZOS,
                )
            background.alpha_composite(sprite, (side * 190, 0))
        sheet.alpha_composite(background, (x + 10, y + 58))
        transition = transitions[transition_index]
        ghost = max(
            ghost_ratios[transition_index],
            ghost_ratios[transition_index + 1],
        )
        draw.text(
            (x + 10, y + 8),
            (
                f"{transition_index + 1:03d}->{transition_index + 2:03d}  "
                f"head {transition['head_step_px']:.2f}px  "
                f"centroid {transition['centroid_step_px']:.2f}px"
            ),
            fill=(25, 47, 79, 255),
            font=font,
        )
        draw.text(
            (x + 10, y + 30),
            f"Alpha IoU {transition['alpha_iou']:.3f}  "
            f"interior translucent {ghost:.2%}",
            fill=(25, 47, 79, 255),
            font=font,
        )
    destination = WORK_DIRECTORY / "boarding-contact.png"
    save_png(sheet, destination)
    return destination


def assert_runtime_assets(
    paths: list[Path],
    label: str,
    *,
    expected_count: int,
    keys: list[Path],
    loop: bool,
    preserve_every_key: bool = True,
) -> dict[str, int]:
    if len(paths) != expected_count:
        raise AssertionError(
            f"{label} emitted {len(paths)} frames, expected {expected_count}"
        )
    pixel_hashes: set[bytes] = set()
    for path in paths:
        with Image.open(path) as opened:
            frame = opened.convert("RGBA")
            if frame.size != RUNTIME_SIZE:
                raise AssertionError(f"{path.name} has size {frame.size}")
            alpha = frame.getchannel("A")
            if alpha.getbbox() is None:
                raise AssertionError(f"{path.name} is transparent")
            rgba = np.asarray(frame, dtype=np.uint8)
            if np.any(rgba[..., :3][rgba[..., 3] == 0]):
                raise AssertionError(
                    f"{path.name} retains RGB under fully transparent pixels"
                )
            pixel_hashes.add(rgba.tobytes())
    if len(pixel_hashes) != len(paths):
        raise AssertionError(
            f"{label} contains duplicate runtime frames: "
            f"{len(pixel_hashes)}/{len(paths)} unique"
        )

    key_pixels = {pixel_bytes(path) for path in keys}
    if preserve_every_key and not key_pixels.issubset(pixel_hashes):
        raise AssertionError(
            f"{label} did not preserve every real authored key pose"
        )
    if not loop and (
        pixel_bytes(paths[0]) != pixel_bytes(keys[0])
        or pixel_bytes(paths[-1]) != pixel_bytes(keys[-1])
    ):
        raise AssertionError(
            f"{label} non-loop endpoints are not the real authored poses"
        )
    if loop and pixel_bytes(paths[0]) != pixel_bytes(keys[0]):
        raise AssertionError(
            f"{label} loop does not start on its first authored pose"
        )
    return bamboo_green_metrics(paths)


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Build one 64-frame transparent panda roaming loop and one "
            "121-frame non-loop boarding path. The secondary wave loop is "
            "authoring-only unless --include-wave is passed."
        )
    )
    parser.add_argument(
        "--source-directory",
        type=Path,
        default=DEFAULT_SOURCE_DIRECTORY,
    )
    parser.add_argument(
        "--include-wave",
        action="store_true",
        help="also build the optional 64-frame secondary panda loop",
    )
    args = parser.parse_args()
    source_directory = args.source_directory.resolve()

    primary_keys, primary_component_metrics = split_registered_sheet(
        source_directory
        / "roam-panda-v2-luban-eyes-primary-16-alpha.png",
        "primary",
    )
    secondary_keys: list[Path] = []
    secondary_component_metrics: list[
        dict[str, float | int | str]
    ] = []
    if args.include_wave:
        secondary_keys, secondary_component_metrics = split_registered_sheet(
            source_directory
            / "roam-panda-v2-luban-eyes-secondary-16-alpha.png",
            "secondary",
        )
    registration_metrics = register_loop_key_sequences(
        primary_keys,
        secondary_keys or None,
    )
    generated_boarding_keys, boarding_component_metrics = split_registered_sheet(
        source_directory
        / "roam-panda-v2-luban-eyes-boarding-16-alpha.png",
        "boarding",
    )
    # Keys 5..8 contain two disconnected characters. Register Luban without
    # dragging the panda, so the panda can enter naturally from the left while
    # the cap follows one continuous path. Keys 9..11 and a translated copy of
    # flight key 1 then climb in small, scale-locked steps. Generated keys
    # 12..16 remain excluded because their caps cross the source-cell boundary.
    boarding_keys, boarding_key_labels = build_registered_boarding_keys(
        generated_boarding_keys,
        primary_keys[0],
    )
    flight = emit_dense_loop(
        primary_keys,
        label="roam-flight",
        asset_prefix="luban-roam-flight",
    )
    wave: list[Path] = []
    if args.include_wave:
        wave = emit_dense_loop(
            secondary_keys,
            label="roam-wave",
            asset_prefix="luban-roam-wave",
        )
    else:
        remove_runtime_sequence("luban-roam-wave")
    boarding = emit_dense_non_loop(
        boarding_keys,
        label="roam-boarding",
        asset_prefix="luban-roam-boarding",
        refinement_rounds=BOARDING_REFINEMENT_ROUNDS,
        minimum_frame_count=MIN_BOARDING_FRAME_COUNT,
        panda_entry_key_interval=3,
        panda_entry_key_interval_count=2,
    )
    expected_boarding_count = (
        (len(boarding_keys) - 1)
        * (2 ** BOARDING_REFINEMENT_ROUNDS)
        + 1
    )
    metrics: dict[str, dict[str, int]] = {
        "flight": assert_runtime_assets(
            flight,
            "roam-flight",
            expected_count=64,
            keys=primary_keys,
            loop=True,
        ),
        "boarding": assert_runtime_assets(
            boarding,
            "roam-boarding",
            expected_count=expected_boarding_count,
            keys=boarding_keys,
            loop=False,
            preserve_every_key=False,
        ),
    }
    if wave:
        metrics["wave"] = assert_runtime_assets(
            wave,
            "roam-wave",
            expected_count=64,
            keys=secondary_keys,
            loop=True,
        )
    brim_metrics = {
        "primary_keys": brim_motion_metrics(primary_keys, loop=True),
        "flight_64": brim_motion_metrics(flight, loop=True),
    }
    if wave:
        brim_metrics.update(
            {
                "secondary_keys": brim_motion_metrics(
                    secondary_keys, loop=True
                ),
                "wave_64": brim_motion_metrics(wave, loop=True),
            }
        )
    boarding_key_metrics, boarding_key_transitions = (
        boarding_transition_metrics(boarding_keys)
    )
    boarding_runtime_metrics, boarding_runtime_transitions = (
        boarding_transition_metrics(boarding)
    )
    boarding_ghost_ratios = [
        interior_translucent_ratio(path) for path in boarding
    ]
    boarding_ghost_metrics = {
        "maximum": max(boarding_ghost_ratios, default=0.0),
        "mean": (
            sum(boarding_ghost_ratios) / len(boarding_ghost_ratios)
            if boarding_ghost_ratios
            else 0.0
        ),
    }
    boarding_black_metrics = opaque_black_metrics(boarding)
    boarding_contact = write_boarding_contact_sheet(
        boarding,
        boarding_runtime_transitions,
        boarding_ghost_ratios,
    )
    if boarding_key_metrics["maximum_brim_step_px"] > 36.0:
        raise AssertionError(
            "boarding key registration still jumps: "
            f"{boarding_key_metrics['maximum_brim_step_px']:.3f}px"
        )
    idle_to_boarding_bytes = pixel_bytes(ASSETS / "luban-idle.png") == pixel_bytes(
        boarding[0]
    )
    boarding_to_flight_bytes = pixel_bytes(boarding[-1]) == pixel_bytes(flight[0])
    with Image.open(ASSETS / "luban-idle.png") as opened:
        idle_alpha = opened.convert("RGBA").getchannel("A").tobytes()
    with Image.open(boarding[0]) as opened:
        boarding_first_alpha = opened.convert("RGBA").getchannel("A").tobytes()
    with Image.open(boarding[-1]) as opened:
        boarding_last_alpha = opened.convert("RGBA").getchannel("A").tobytes()
    with Image.open(flight[0]) as opened:
        flight_first_alpha = opened.convert("RGBA").getchannel("A").tobytes()
    idle_to_boarding_alpha = idle_alpha == boarding_first_alpha
    boarding_to_flight_alpha = boarding_last_alpha == flight_first_alpha
    if not (
        idle_to_boarding_bytes
        and boarding_to_flight_bytes
        and idle_to_boarding_alpha
        and boarding_to_flight_alpha
    ):
        raise AssertionError(
            "idle/boarding/flight boundary frames are not byte-exact"
        )
    total_frames = len(flight) + len(boarding) + len(wave)
    output_ranges = [
        "Assets/luban-roam-flight-001..064.png",
        f"Assets/luban-roam-boarding-001..{len(boarding):03d}.png",
    ]
    if wave:
        output_ranges.append("Assets/luban-roam-wave-001..064.png")
    print(
        f"wrote {total_frames} unique panda roaming frames: "
        + " and ".join(output_ranges),
        flush=True,
    )
    print(
        "bamboo green preservation: "
        + ", ".join(
            f"{name}={values['green_pixels']} px/"
            f"{values['frames_with_green']} frames/"
            f"{values['unique_green_colours']} colours"
            for name, values in metrics.items()
        ),
        flush=True,
    )
    print(
        "boundary equality: "
        f"idle->boarding bytes={idle_to_boarding_bytes} "
        f"alpha={idle_to_boarding_alpha}; "
        f"boarding->flight bytes={boarding_to_flight_bytes} "
        f"alpha={boarding_to_flight_alpha}",
        flush=True,
    )
    print(
        "brim registration: "
        + ", ".join(
            f"{name}=xerr {values['maximum_x_error_px']:.3f}px/"
            f"boberr {values['maximum_bob_error_px']:.3f}px"
            for name, values in registration_metrics.items()
        ),
        flush=True,
    )
    print(
        "brim motion: "
        + ", ".join(
            f"{name}=max {values['maximum_adjacent_px']:.3f}px "
            f"({values['maximum_adjacent_dip']:.3f} DIP), "
            f"seam {values['seam_px']:.3f}px "
            f"({values['seam_dip']:.3f} DIP)"
            for name, values in brim_metrics.items()
        ),
        flush=True,
    )
    print(
        "boarding key continuity: "
        f"max/mean brim step "
        f"{boarding_key_metrics['maximum_brim_step_px']:.3f}/"
        f"{boarding_key_metrics['mean_brim_step_px']:.3f}px; "
        f"max/mean centroid step "
        f"{boarding_key_metrics['maximum_centroid_step_px']:.3f}/"
        f"{boarding_key_metrics['mean_centroid_step_px']:.3f}px; "
        f"min/mean Alpha IoU "
        f"{boarding_key_metrics['minimum_alpha_iou']:.4f}/"
        f"{boarding_key_metrics['mean_alpha_iou']:.4f}",
        flush=True,
    )
    print(
        "boarding runtime continuity: "
        f"max/mean brim step "
        f"{boarding_runtime_metrics['maximum_brim_step_px']:.3f}/"
        f"{boarding_runtime_metrics['mean_brim_step_px']:.3f}px; "
        f"max/mean centroid step "
        f"{boarding_runtime_metrics['maximum_centroid_step_px']:.3f}/"
        f"{boarding_runtime_metrics['mean_centroid_step_px']:.3f}px; "
        f"min/mean Alpha IoU "
        f"{boarding_runtime_metrics['minimum_alpha_iou']:.4f}/"
        f"{boarding_runtime_metrics['mean_alpha_iou']:.4f}",
        flush=True,
    )
    print(
        "boarding interior translucent ghost ratio: "
        f"max={boarding_ghost_metrics['maximum']:.4%}, "
        f"mean={boarding_ghost_metrics['mean']:.4%}; "
        f"contact={boarding_contact}",
        flush=True,
    )
    print(
        "boarding opaque-black regression gate: "
        f"max={int(boarding_black_metrics['maximum_count'])} px/"
        f"{float(boarding_black_metrics['maximum_ratio']):.4%} at frame "
        f"{int(boarding_black_metrics['worst_frame']):03d} "
        "(fails only when both 500 px and 0.5000% are exceeded)",
        flush=True,
    )
    print(
        "boarding key transitions: "
        + "; ".join(
            f"{boarding_key_labels[index]}->{boarding_key_labels[index + 1]}="
            f"{transition['head_step_px']:.3f}px/"
            f"{transition['centroid_step_px']:.3f}px/"
            f"{transition['alpha_iou']:.4f}"
            for index, transition in enumerate(boarding_key_transitions)
        ),
        flush=True,
    )
    component_metrics = [
        *primary_component_metrics,
        *secondary_component_metrics,
        *boarding_component_metrics,
    ]
    component_report = WORK_DIRECTORY / "component-cleanup.json"
    component_report.parent.mkdir(parents=True, exist_ok=True)
    component_report.write_text(
        json.dumps(component_metrics, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(
        "retained alpha components: "
        + "; ".join(
            f"{record['sequence']}{int(record['key']):02d}="
            f"{int(record['retained_component_count'])} components/"
            f"min {float(record['minimum_retained_area_ratio']):.4f}/"
            f"removed {int(record['removed_pixel_count'])} px"
            for record in component_metrics
        ),
        flush=True,
    )


if __name__ == "__main__":
    main()
