from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image

import install_generated_motion_assets as installer


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
GENERATED_SOURCES = ROOT / "tools" / "generated_sources"
DEFAULT_COVER_SOURCE = GENERATED_SOURCES / "hide-toy-box-v1-alpha.png"
RUNTIME_SIZE = (450, 550)
SMOOTH_FRAME_COUNT = 64
LOOP_FRAME_COUNT = 48
WAVE_ENTRY_FRAME_COUNT = 40
COVER_SIZE = (320, 196)
COVER_POSITION = (65, 354)
HIDDEN_CHARACTER_OFFSET_Y = 220
PEEK_CHARACTER_OFFSET_Y = 92
PEEK_CHARACTER_OFFSET_X = 120
CHARACTER_CLIP_BOTTOM = 470


def smoothstep(value: float) -> float:
    value = min(1.0, max(0.0, value))
    return value * value * (3.0 - 2.0 * value)


def clean_keyed_source(image: Image.Image) -> Image.Image:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    alpha = rgba[..., 3].astype(np.int32)

    # Image generation can leave an almost invisible matte across the chroma
    # background. Contract that matte before resizing so it never becomes a
    # wide colored halo in the runtime sprite pages.
    contracted = np.clip(
        (alpha - 40) * 255 // (255 - 40),
        0,
        255,
    ).astype(np.uint8)
    rgba[..., 3] = contracted
    rgba[contracted == 0, :3] = 0
    cleaned = Image.fromarray(rgba, "RGBA")
    box = cleaned.getchannel("A").getbbox()
    if box is None:
        raise ValueError("hide cover source became empty after matte cleanup")
    return cleaned.crop(box)


def prepare_cover(source_path: Path) -> Image.Image:
    with Image.open(source_path) as opened:
        source = clean_keyed_source(opened)
    cover = installer.resize_rgba_premultiplied(source, COVER_SIZE)
    cover = installer.neutralize_green_fringe(cover)
    return clear_transparent_rgb(cover)


def clear_transparent_rgb(image: Image.Image) -> Image.Image:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    rgba[rgba[..., 3] == 0, :3] = 0
    return Image.fromarray(rgba, "RGBA")


def load_wave_frame(frame_number: int) -> Image.Image:
    path = ASSETS / f"luban-wave-smooth-{frame_number:03d}.png"
    if not path.exists():
        raise FileNotFoundError(f"missing authored wave frame: {path}")
    with Image.open(path) as opened:
        frame = opened.convert("RGBA")
    if frame.size != RUNTIME_SIZE:
        raise ValueError(f"{path.name} must be {RUNTIME_SIZE}, got {frame.size}")
    return frame


def clip_character(image: Image.Image, bottom: int) -> Image.Image:
    if bottom >= image.height:
        return image
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    rgba[bottom:, :, :] = 0
    return Image.fromarray(rgba, "RGBA")


def composite_at(
    canvas: Image.Image,
    layer: Image.Image,
    position: tuple[int, int],
) -> None:
    x, y = position
    destination_left = max(0, x)
    destination_top = max(0, y)
    destination_right = min(canvas.width, x + layer.width)
    destination_bottom = min(canvas.height, y + layer.height)
    if destination_left >= destination_right or destination_top >= destination_bottom:
        return
    source_box = (
        destination_left - x,
        destination_top - y,
        destination_right - x,
        destination_bottom - y,
    )
    canvas.alpha_composite(
        layer.crop(source_box),
        (destination_left, destination_top),
    )


def render_frame(
    character: Image.Image,
    cover: Image.Image,
    *,
    character_offset_x: int = 0,
    character_offset_y: int = 0,
    cover_y: int = COVER_POSITION[1],
    clip_bottom: int | None = None,
) -> Image.Image:
    canvas = Image.new("RGBA", RUNTIME_SIZE, (0, 0, 0, 0))
    character_layer = (
        clip_character(
            character,
            max(0, clip_bottom - character_offset_y),
        )
        if clip_bottom is not None
        else character
    )
    composite_at(
        canvas,
        character_layer,
        (character_offset_x, character_offset_y),
    )
    composite_at(canvas, cover, (COVER_POSITION[0], cover_y))
    return clear_transparent_rgb(canvas)


def build_smooth_frames(cover: Image.Image) -> list[Image.Image]:
    wave_frames = [
        load_wave_frame(frame_number)
        for frame_number in range(1, WAVE_ENTRY_FRAME_COUNT + 1)
    ]
    outputs: list[Image.Image] = []

    # The existing wave transition already starts exactly after the shared
    # wake sequence. Raise the foreground cover while that authored transition
    # completes, so no generated character pixels or cross-fades are needed.
    cover_start_y = RUNTIME_SIZE[1] + 8
    for index, character in enumerate(wave_frames, start=1):
        progress = smoothstep(index / WAVE_ENTRY_FRAME_COUNT)
        cover_y = round(
            cover_start_y +
            (COVER_POSITION[1] - cover_start_y) * progress
        )
        outputs.append(
            render_frame(
                character,
                cover,
                cover_y=cover_y,
            )
        )

    # Duck behind the box. The final frames quietly move from wave frame 40 to
    # frame 33 while the face is already occluded, preparing a seamless loop.
    duck_frame_count = SMOOTH_FRAME_COUNT - WAVE_ENTRY_FRAME_COUNT
    for local_index in range(1, duck_frame_count + 1):
        progress = smoothstep(local_index / duck_frame_count)
        wave_number = round(
            WAVE_ENTRY_FRAME_COUNT -
            (WAVE_ENTRY_FRAME_COUNT - 33) * progress
        )
        character = wave_frames[wave_number - 1]
        outputs.append(
            render_frame(
                character,
                cover,
                character_offset_y=round(HIDDEN_CHARACTER_OFFSET_Y * progress),
                clip_bottom=CHARACTER_CLIP_BOTTOM,
            )
        )

    if len(outputs) != SMOOTH_FRAME_COUNT:
        raise AssertionError(f"expected {SMOOTH_FRAME_COUNT} smooth frames")
    return outputs


def peek_phase(local_index: int) -> tuple[float, int]:
    """Return reveal progress and a deliberate one-pixel return-path offset."""

    if not 0 <= local_index < 24:
        raise ValueError(f"invalid peek phase index: {local_index}")
    if local_index < 10:
        return smoothstep((local_index + 1) / 10), 0
    if local_index < 12:
        bob = (-1, -2)[local_index - 10]
        return 1.0, bob
    return smoothstep((23 - local_index) / 11), 2 if local_index < 23 else 0


def build_loop_frames(cover: Image.Image) -> list[Image.Image]:
    wave_frames = {
        frame_number: load_wave_frame(frame_number)
        for frame_number in range(33, WAVE_ENTRY_FRAME_COUNT + 1)
    }
    outputs: list[Image.Image] = []
    for frame_index in range(LOOP_FRAME_COUNT):
        side = -1 if frame_index < 24 else 1
        local_index = frame_index % 24
        progress, path_offset_y = peek_phase(local_index)
        wave_number = 33 + round(7 * progress)
        character_offset_x = round(side * PEEK_CHARACTER_OFFSET_X * progress)
        character_offset_y = round(
            HIDDEN_CHARACTER_OFFSET_Y +
            (PEEK_CHARACTER_OFFSET_Y - HIDDEN_CHARACTER_OFFSET_Y) * progress
        ) + path_offset_y
        if frame_index == 23:
            # Keep the midpoint rest visually hidden but byte-distinct from
            # the true loop endpoint used by smooth-in/smooth-out.
            character_offset_y += 1
        if local_index in (10, 11):
            # A tiny authored nod at the top of each peek reads as playful,
            # while staying below one displayed physical pixel after scaling.
            character_offset_y -= 1
        outputs.append(
            render_frame(
                wave_frames[wave_number],
                cover,
                character_offset_x=character_offset_x,
                character_offset_y=character_offset_y,
                clip_bottom=CHARACTER_CLIP_BOTTOM,
            )
        )

    if len(outputs) != LOOP_FRAME_COUNT:
        raise AssertionError(f"expected {LOOP_FRAME_COUNT} loop frames")
    return outputs


def save_sequence(images: list[Image.Image], prefix: str) -> list[Path]:
    paths: list[Path] = []
    for frame_number, image in enumerate(images, start=1):
        destination = ASSETS / f"{prefix}-{frame_number:03d}.png"
        installer.save_png_atomically(image, destination)
        paths.append(destination)

    stale_number = len(images) + 1
    while True:
        stale = ASSETS / f"{prefix}-{stale_number:03d}.png"
        if not stale.exists():
            break
        stale.unlink()
        stale_number += 1
    return paths


def alpha_iou(first: Image.Image, second: Image.Image) -> float:
    first_alpha = np.asarray(first.convert("RGBA"), dtype=np.uint8)[..., 3] >= 24
    second_alpha = np.asarray(second.convert("RGBA"), dtype=np.uint8)[..., 3] >= 24
    union = np.logical_or(first_alpha, second_alpha).sum()
    if union == 0:
        return 1.0
    return float(np.logical_and(first_alpha, second_alpha).sum() / union)


def frame_digest(image: Image.Image) -> str:
    return hashlib.sha256(image.tobytes()).hexdigest()


def build_qa_report(
    smooth: list[Image.Image],
    loop: list[Image.Image],
    source_path: Path,
) -> dict[str, object]:
    all_frames = smooth + loop
    dirty_transparent_rgb = 0
    for frame in all_frames:
        rgba = np.asarray(frame.convert("RGBA"), dtype=np.uint8)
        dirty_transparent_rgb += int(np.count_nonzero(
            (rgba[..., 3] == 0) & np.any(rgba[..., :3] != 0, axis=2)
        ))

    loop_ious = [
        alpha_iou(loop[index], loop[(index + 1) % len(loop)])
        for index in range(len(loop))
    ]
    smooth_ious = [
        alpha_iou(smooth[index], smooth[index + 1])
        for index in range(len(smooth) - 1)
    ]
    report = {
        "generator": Path(__file__).name,
        "coverSource": str(source_path.relative_to(ROOT)),
        "runtimeSize": list(RUNTIME_SIZE),
        "smoothFrameCount": len(smooth),
        "loopFrameCount": len(loop),
        "smoothUniqueFrameCount": len({frame_digest(frame) for frame in smooth}),
        "loopUniqueFrameCount": len({frame_digest(frame) for frame in loop}),
        "smoothMinimumAlphaIou": min(smooth_ious),
        "smoothMeanAlphaIou": float(np.mean(smooth_ious)),
        "loopMinimumAlphaIou": min(loop_ious),
        "loopMeanAlphaIou": float(np.mean(loop_ious)),
        "dirtyTransparentRgbPixels": dirty_transparent_rgb,
        "smoothEndMatchesLoopRest": (
            frame_digest(smooth[-1]) == frame_digest(loop[-1])
        ),
    }
    if dirty_transparent_rgb:
        raise AssertionError("hide frames contain RGB data under zero alpha")
    if not report["smoothEndMatchesLoopRest"]:
        raise AssertionError("hide smooth endpoint must equal the loop rest pose")
    if min(loop_ious) < 0.90:
        raise AssertionError(
            f"hide loop adjacent alpha IoU is too low: {min(loop_ious):.4f}"
        )
    return report


def build_preview(
    smooth: list[Image.Image],
    loop: list[Image.Image],
    destination: Path,
) -> None:
    selections = [
        smooth[0],
        smooth[19],
        smooth[39],
        smooth[-1],
        loop[9],
        loop[14],
        loop[33],
        loop[-1],
    ]
    cell_width, cell_height = RUNTIME_SIZE
    preview = Image.new(
        "RGBA",
        (cell_width * 4, cell_height * 2),
        (246, 250, 255, 255),
    )
    for index, frame in enumerate(selections):
        preview.alpha_composite(
            frame,
            ((index % 4) * cell_width, (index // 4) * cell_height),
        )
    installer.save_png_atomically(preview, destination)


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Build deterministic 60fps hide-and-seek action frames from the "
            "existing Luban character art and a generated foreground cover."
        )
    )
    parser.add_argument(
        "--cover-source",
        type=Path,
        default=DEFAULT_COVER_SOURCE,
    )
    parser.add_argument(
        "--preview",
        type=Path,
        default=GENERATED_SOURCES / "hide-action-v1-preview.png",
    )
    args = parser.parse_args()
    source_path = args.cover_source.resolve()
    if not source_path.exists():
        raise FileNotFoundError(source_path)

    cover = prepare_cover(source_path)
    smooth = build_smooth_frames(cover)
    loop = build_loop_frames(cover)
    smooth_paths = save_sequence(smooth, "luban-hide-smooth")
    loop_paths = save_sequence(loop, "luban-hide-loop")
    report = build_qa_report(smooth, loop, source_path)
    report_path = GENERATED_SOURCES / "hide-action-v1-qa.json"
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    build_preview(smooth, loop, args.preview)
    print(
        f"Wrote {len(smooth_paths)} smooth and {len(loop_paths)} loop frames; "
        f"QA: {report_path}; preview: {args.preview}",
        flush=True,
    )


if __name__ == "__main__":
    main()
