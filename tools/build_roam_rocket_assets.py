from __future__ import annotations

import argparse
from collections import deque
import hashlib
import math
from pathlib import Path
import shutil

import numpy as np
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
DEFAULT_SOURCE = (
    ROOT
    / "tools"
    / "generated_sources"
    / "roam-rocket-luban-cloud-key-v2-alpha.png"
)
RUNTIME_SIZE = (450, 550)
BOARDING_FRAME_COUNT = 64
FLIGHT_FRAME_COUNT = 64
VISIBLE_ALPHA_THRESHOLD = 4
ROCKET_MAXIMUM_SIZE = (300, 335)
ROCKET_HORIZONTAL_STRETCH = 1.18
ROCKET_LEFT = 5
ROCKET_BOTTOM = 478
CLOUD_COUNT = 3
CLOUD_SIZE = (64, 64)
CLOUD_TRACK_ORIGIN_X = 253
CLOUD_TRACK_ORIGIN_Y = 365
CLOUD_TRACK_SPACING = 65
CLOUD_BURST_FRAME_COUNT = 4
CLOUD_REARWARD_BURST_PIXELS = 2


def clear_transparent_rgb(image: Image.Image) -> Image.Image:
    pixels = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    alpha = pixels[..., 3]
    pixels[alpha < VISIBLE_ALPHA_THRESHOLD] = 0
    return Image.fromarray(pixels, "RGBA")


def resize_premultiplied(
    image: Image.Image,
    size: tuple[int, int],
) -> Image.Image:
    pixels = np.asarray(image.convert("RGBA"), dtype=np.uint16)
    alpha = pixels[..., 3:4]
    premultiplied = np.concatenate(
        ((pixels[..., :3] * alpha + 127) // 255, alpha),
        axis=2,
    ).astype(np.uint8)
    resized = Image.fromarray(premultiplied, "RGBA").resize(
        size,
        Image.Resampling.LANCZOS,
    )
    resized_pixels = np.asarray(resized, dtype=np.uint16)
    resized_alpha = resized_pixels[..., 3:4]
    straight = np.zeros_like(resized_pixels, dtype=np.uint16)
    straight[..., 3:4] = resized_alpha
    visible = resized_alpha[..., 0] > 0
    straight[..., :3][visible] = np.minimum(
        255,
        (
            resized_pixels[..., :3][visible] * 255
            + resized_alpha[visible] // 2
        )
        // resized_alpha[visible],
    )
    return clear_transparent_rgb(
        Image.fromarray(straight.astype(np.uint8), "RGBA")
    )


def _label_visible_components(
    visible: np.ndarray,
) -> tuple[np.ndarray, list[tuple[int, int, int, int, int, int]]]:
    height, width = visible.shape
    remaining = visible.copy()
    labels = np.zeros((height, width), dtype=np.uint16)
    components: list[tuple[int, int, int, int, int, int]] = []
    next_label = 1
    while np.any(remaining):
        seed = int(np.flatnonzero(remaining)[0])
        seed_y, seed_x = divmod(seed, width)
        pending: deque[tuple[int, int]] = deque(((seed_x, seed_y),))
        remaining[seed_y, seed_x] = False
        labels[seed_y, seed_x] = next_label
        left = right = seed_x
        top = bottom = seed_y
        area = 0

        while pending:
            x, y = pending.pop()
            area += 1
            left = min(left, x)
            right = max(right, x)
            top = min(top, y)
            bottom = max(bottom, y)
            for neighbor_y in range(max(0, y - 1), min(height, y + 2)):
                for neighbor_x in range(max(0, x - 1), min(width, x + 2)):
                    if not remaining[neighbor_y, neighbor_x]:
                        continue
                    remaining[neighbor_y, neighbor_x] = False
                    labels[neighbor_y, neighbor_x] = next_label
                    pending.append((neighbor_x, neighbor_y))

        if area >= 100:
            components.append(
                (next_label, left, top, right + 1, bottom + 1, area)
            )
        next_label += 1
    return labels, components


def split_generated_key(
    source: Path,
) -> tuple[Image.Image, tuple[Image.Image, Image.Image, Image.Image]]:
    with Image.open(source) as opened:
        generated = clear_transparent_rgb(opened)

    pixels = np.asarray(generated, dtype=np.uint8)
    visible = pixels[..., 3] >= VISIBLE_ALPHA_THRESHOLD
    labels, component_metadata = _label_visible_components(visible)
    component_metadata.sort(key=lambda item: item[1])
    if len(component_metadata) != CLOUD_COUNT + 1:
        raise ValueError(
            "Rocket source must contain one rocket followed by exactly three "
            "separate cloud puffs; found "
            f"{len(component_metadata)} components: {source}"
        )

    components: list[Image.Image] = []
    for label, left, top, right, bottom, _ in component_metadata:
        component_pixels = pixels[top:bottom, left:right].copy()
        component_pixels[labels[top:bottom, left:right] != label] = 0
        components.append(
            Image.fromarray(component_pixels, "RGBA")
        )

    rocket_source, *cloud_sources = components
    if rocket_source.width <= max(cloud.width for cloud in cloud_sources):
        raise ValueError(
            "The leftmost source component must be the rocket and rider."
        )
    return rocket_source, tuple(cloud_sources)  # type: ignore[return-value]


def normalize_rocket_key(rocket_source: Image.Image) -> Image.Image:
    bounds = rocket_source.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("Rocket source component contains no visible pixels.")
    cropped = rocket_source.crop(bounds)
    scale = min(
        ROCKET_MAXIMUM_SIZE[0]
        / (cropped.width * ROCKET_HORIZONTAL_STRETCH),
        ROCKET_MAXIMUM_SIZE[1] / cropped.height,
    )
    size = (
        max(1, round(cropped.width * scale * ROCKET_HORIZONTAL_STRETCH)),
        max(1, round(cropped.height * scale)),
    )
    resized = resize_premultiplied(cropped, size)
    canvas = Image.new("RGBA", RUNTIME_SIZE, (0, 0, 0, 0))
    destination_x = ROCKET_LEFT + (ROCKET_MAXIMUM_SIZE[0] - resized.width) // 2
    destination_y = ROCKET_BOTTOM - resized.height
    canvas.alpha_composite(resized, (destination_x, destination_y))
    return clear_transparent_rgb(canvas)


def normalize_cloud_keys(
    cloud_sources: tuple[Image.Image, Image.Image, Image.Image],
) -> tuple[Image.Image, Image.Image, Image.Image]:
    normalized = tuple(
        resize_premultiplied(cloud, CLOUD_SIZE) for cloud in cloud_sources
    )
    return normalized  # type: ignore[return-value]


def transformed(
    image: Image.Image,
    *,
    scale: float = 1.0,
    angle: float = 0.0,
    dx: int = 0,
    dy: int = 0,
) -> Image.Image:
    if not math.isfinite(scale) or scale <= 0:
        raise ValueError(f"Invalid sprite scale: {scale}")

    width = max(1, round(image.width * scale))
    height = max(1, round(image.height * scale))
    scaled = resize_premultiplied(image, (width, height))
    if abs(angle) > 0.000001:
        scaled = clear_transparent_rgb(
            scaled.rotate(
                angle,
                resample=Image.Resampling.BICUBIC,
                expand=False,
            )
        )

    result = Image.new("RGBA", image.size, (0, 0, 0, 0))
    result.alpha_composite(
        scaled,
        (
            (image.width - width) // 2 + dx,
            (image.height - height) // 2 + dy,
        ),
    )
    return clear_transparent_rgb(result)


def smoothstep(value: float) -> float:
    value = min(1.0, max(0.0, value))
    return value * value * (3.0 - 2.0 * value)


def with_opacity(image: Image.Image, opacity: int) -> Image.Image:
    if not 0 <= opacity <= 255:
        raise ValueError(f"Invalid opacity: {opacity}")
    pixels = np.asarray(image.convert("RGBA"), dtype=np.uint16).copy()
    pixels[..., 3] = (pixels[..., 3] * opacity + 127) // 255
    return clear_transparent_rgb(
        Image.fromarray(pixels.astype(np.uint8), "RGBA")
    )


def cloud_layout_for_frame(
    frame_index: int,
) -> tuple[tuple[int, int, int], tuple[int, int, int], tuple[int, int, int]]:
    if not 0 <= frame_index < FLIGHT_FRAME_COUNT:
        raise ValueError(f"Invalid flight frame index: {frame_index}")
    burst_progress = (
        frame_index % CLOUD_BURST_FRAME_COUNT
    ) / (CLOUD_BURST_FRAME_COUNT - 1)
    rearward_burst = round(
        CLOUD_REARWARD_BURST_PIXELS * burst_progress
    )
    states: list[tuple[int, int, int]] = []
    for cloud_index in range(CLOUD_COUNT):
        x = (
            CLOUD_TRACK_ORIGIN_X
            + cloud_index * CLOUD_TRACK_SPACING
            + rearward_burst
        )
        fast_flutter = round(
            2.0
            * math.sin(
                math.tau * frame_index / 8.0
                + cloud_index * math.tau / CLOUD_COUNT
            )
        )
        y = CLOUD_TRACK_ORIGIN_Y + fast_flutter
        opacity = round(
            218
            + 37
            * (
                0.5
                + 0.5
                * math.sin(
                    math.tau * frame_index / CLOUD_BURST_FRAME_COUNT
                    - cloud_index * math.tau / CLOUD_COUNT
                )
            )
        )
        states.append((x, y, opacity))
    return tuple(states)  # type: ignore[return-value]


def add_launch_sparkles(image: Image.Image, progress: float) -> None:
    envelope = math.sin(math.pi * min(1.0, max(0.0, progress)))
    if envelope <= 0:
        return

    draw = ImageDraw.Draw(image, "RGBA")
    sparkles = (
        (84, 358, 4, 0.00),
        (112, 305, 3, 0.18),
        (338, 390, 4, 0.31),
        (370, 330, 3, 0.47),
        (286, 160, 3, 0.62),
        (160, 190, 2, 0.78),
    )
    for x, y, radius, phase in sparkles:
        pulse = max(0.0, math.sin(math.pi * (progress + phase) % math.pi))
        alpha = round(210 * envelope * pulse)
        # Faint isolated dots read as motion trails at large desktop scaling.
        # Emit only crisp launch sparkles; the opaque flash owns the soft glow.
        if alpha < 160:
            continue
        draw.ellipse(
            (x - radius, y - radius, x + radius, y + radius),
            fill=(255, 210, 74, alpha),
        )

    # One crisp orbiting spark keeps every authored boarding pose distinct
    # without reintroducing low-alpha halos around the character contour.
    authored_index = round(progress * (BOARDING_FRAME_COUNT - 1))
    angle = math.tau * authored_index / BOARDING_FRAME_COUNT
    marker_x = round(226 + 176 * math.cos(angle))
    marker_y = round(336 + 142 * math.sin(angle))
    draw.ellipse(
        (marker_x - 2, marker_y - 2, marker_x + 2, marker_y + 2),
        fill=(255, 224, 92, 210),
    )


def add_launch_flash(image: Image.Image, progress: float) -> None:
    authored_index = round(progress * (BOARDING_FRAME_COUNT - 1))
    if authored_index not in range(29, 35):
        return

    # A short opaque cloud burst hides the single-frame vehicle swap without
    # covering the desktop in the old, oversized circular flash.  Drawing the
    # pale outline union first keeps one crisp scalloped silhouette.
    layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer, "RGBA")
    puffs = (
        (226, 340, 78),
        (145, 340, 65),
        (307, 340, 65),
        (92, 370, 58),
        (360, 370, 58),
        (175, 275, 60),
        (277, 275, 60),
        (147, 220, 55),
        (226, 220, 55),
        (305, 220, 55),
        (175, 425, 60),
        (277, 425, 60),
    )
    for center_x, center_y, radius in puffs:
        draw.ellipse(
            (
                center_x - radius - 5,
                center_y - radius - 5,
                center_x + radius + 5,
                center_y + radius + 5,
            ),
            fill=(204, 222, 236, 255),
        )
    for center_x, center_y, radius in puffs:
        draw.ellipse(
            (
                center_x - radius,
                center_y - radius,
                center_x + radius,
                center_y + radius,
            ),
            fill=(255, 251, 241, 255),
        )
    image.alpha_composite(layer)


def build_flight_frames(
    rocket: Image.Image,
    clouds: tuple[Image.Image, Image.Image, Image.Image],
) -> list[Image.Image]:
    frames: list[Image.Image] = []
    for index in range(FLIGHT_FRAME_COUNT):
        phase = math.tau * index / FLIGHT_FRAME_COUNT
        frame = Image.new("RGBA", RUNTIME_SIZE, (0, 0, 0, 0))
        for cloud, (x, y, opacity) in zip(
            clouds,
            cloud_layout_for_frame(index),
            strict=True,
        ):
            frame.alpha_composite(with_opacity(cloud, opacity), (x, y))

        bob = round(2.0 * math.sin(phase))
        sway = 0.65 * math.sin(phase + math.pi / 2)
        pulse = 1.0 + 0.003 * math.sin(phase * 2)
        frame.alpha_composite(
            transformed(
                rocket,
                scale=pulse,
                angle=sway,
                dx=round(math.sin(phase * 2)),
                dy=bob,
            )
        )
        frames.append(clear_transparent_rgb(frame))
    return frames


def build_boarding_frames(
    idle: Image.Image,
    rocket: Image.Image,
) -> list[Image.Image]:
    frames: list[Image.Image] = []
    for index in range(BOARDING_FRAME_COUNT):
        progress = index / (BOARDING_FRAME_COUNT - 1)
        if index == 0:
            frames.append(idle.copy())
            continue
        if index == BOARDING_FRAME_COUNT - 1:
            frames.append(rocket.copy())
            continue

        frame = Image.new("RGBA", RUNTIME_SIZE, (0, 0, 0, 0))
        if progress < 0.5:
            # Keep the authored idle contour pixel-sharp until the opaque launch
            # flash covers the vehicle swap.  Resampling a slowly shrinking idle
            # sprite creates a wide translucent halo at 140% / 125% DPI.
            frame.alpha_composite(idle)
        else:
            arrival = smoothstep((progress - 0.5) / 0.5)
            rocket_layer = transformed(
                rocket,
                scale=0.88 + 0.12 * arrival,
                angle=3.0 * (1.0 - arrival),
                dx=round(58 * (1.0 - arrival)),
                dy=round(24 * (1.0 - arrival)),
            )
            frame.alpha_composite(rocket_layer)
        add_launch_flash(frame, progress)
        add_launch_sparkles(frame, progress)
        frames.append(clear_transparent_rgb(frame))
    return frames


def save_png_atomically(image: Image.Image, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + ".tmp")
    image.save(temporary, format="PNG", optimize=True)
    temporary.replace(destination)


def copy_file_atomically(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + ".tmp")
    shutil.copyfile(source, temporary)
    temporary.replace(destination)


def emit_sequence(prefix: str, frames: list[Image.Image]) -> list[Path]:
    outputs: list[Path] = []
    for number, frame in enumerate(frames, start=1):
        destination = ASSETS / f"{prefix}-{number:03d}.png"
        save_png_atomically(frame, destination)
        outputs.append(destination)

    for stale in ASSETS.glob(f"{prefix}-*.png"):
        suffix = stale.stem.rsplit("-", 1)[-1]
        if suffix.isdigit() and int(suffix) > len(outputs):
            stale.unlink()
    return outputs


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_outputs(boarding: list[Path], flight: list[Path]) -> None:
    if len(boarding) != BOARDING_FRAME_COUNT or len(flight) != FLIGHT_FRAME_COUNT:
        raise AssertionError("Rocket sequence frame count is inconsistent.")
    if sha256(ASSETS / "luban-idle.png") != sha256(boarding[0]):
        raise AssertionError("Idle -> rocket boarding seam is not byte exact.")
    if sha256(boarding[-1]) != sha256(flight[0]):
        raise AssertionError("Rocket boarding -> flight seam is not byte exact.")

    for label, paths in (("boarding", boarding), ("flight", flight)):
        fingerprints: set[str] = set()
        for path in paths:
            with Image.open(path) as opened:
                if opened.size != RUNTIME_SIZE or opened.mode != "RGBA":
                    raise AssertionError(
                        f"Invalid rocket {label} frame {path.name}: "
                        f"size={opened.size}, mode={opened.mode}"
                    )
                if opened.getchannel("A").getbbox() is None:
                    raise AssertionError(f"Rocket frame is empty: {path.name}")
                if label == "flight":
                    alpha = np.asarray(opened.getchannel("A"), dtype=np.uint8)
                    if (
                        np.any(alpha[0, :])
                        or np.any(alpha[-1, :])
                        or np.any(alpha[:, 0])
                        or np.any(alpha[:, -1])
                    ):
                        raise AssertionError(
                            "Rocket flight artwork must retain a transparent "
                            f"rotation-safe border: {path.name}"
                        )
                fingerprints.add(hashlib.sha256(opened.tobytes()).hexdigest())
        if len(fingerprints) != len(paths):
            raise AssertionError(
                f"Rocket {label} sequence must keep every pose unique: "
                f"{len(fingerprints)}/{len(paths)}"
            )


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Build deterministic transparent rocket boarding and flight "
            "sequences for the built-in Luban skin."
        )
    )
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    args = parser.parse_args()
    source = args.source.resolve()
    if not source.is_file():
        raise FileNotFoundError(source)

    rocket_source, cloud_sources = split_generated_key(source)
    rocket = normalize_rocket_key(rocket_source)
    clouds = normalize_cloud_keys(cloud_sources)
    with Image.open(ASSETS / "luban-idle.png") as opened:
        idle = clear_transparent_rgb(opened)
    if idle.size != RUNTIME_SIZE:
        raise ValueError(f"Unexpected idle size: {idle.size}")

    flight_frames = build_flight_frames(rocket, clouds)
    boarding_frames = build_boarding_frames(idle, flight_frames[0])
    boarding = emit_sequence("luban-roam-rocket-boarding", boarding_frames)
    flight = emit_sequence("luban-roam-rocket-flight", flight_frames)
    copy_file_atomically(ASSETS / "luban-idle.png", boarding[0])
    copy_file_atomically(flight[0], boarding[-1])
    validate_outputs(boarding, flight)
    print(
        "Built rocket assets: "
        f"boarding={len(boarding)}, flight={len(flight)}, "
        f"source={source}"
    )


if __name__ == "__main__":
    main()
