from __future__ import annotations

import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
REPORT_PATH = ROOT / "tools" / "generated_sources" / "edge-side-arm-reveal-qa.json"

FRAME_COUNT = 48
CANVAS_SIZE = (450, 550)
MAX_SHIFT = 7.0
Y_FADE_IN_START = 345.0
Y_FADE_IN_END = 361.0
Y_FADE_OUT_START = 420.0
Y_FADE_OUT_END = 438.0
X_FADE_OUT_START = 95.0
X_FADE_OUT_END = 120.0

# The v1.0.57 release committed the post-processed frames but intentionally did
# not commit REPORT_PATH.  Keep decoded-pixel sequence fingerprints here so a
# clean checkout can distinguish the authored input from the already-fixed
# release output without applying the 7px warp a second time.
V1057_INPUT_SEQUENCE_SHA256 = (
    "8b058696f26164bbd5b0569dda03b29ace31a56d99f56f7528eb8cf8c762e520"
)
V1057_OUTPUT_SEQUENCE_SHA256 = (
    "cb5daacbaadd40e268d6fef9b9eed84797429fb2f2bb8002f79aa351cb737ee4"
)
V1057_MINIMUM_CHANGED_PIXELS = 7698
V1057_MAXIMUM_CHANGED_PIXELS = 8110


def smoothstep(value: np.ndarray) -> np.ndarray:
    value = np.clip(value, 0.0, 1.0)
    return value * value * (3.0 - 2.0 * value)


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


def build_displacement(height: int, width: int) -> np.ndarray:
    yy, xx = np.mgrid[0:height, 0:width]
    fade_in = smoothstep(
        (yy.astype(np.float32) - Y_FADE_IN_START)
        / (Y_FADE_IN_END - Y_FADE_IN_START)
    )
    fade_out = 1.0 - smoothstep(
        (yy.astype(np.float32) - Y_FADE_OUT_START)
        / (Y_FADE_OUT_END - Y_FADE_OUT_START)
    )
    horizontal_fade = 1.0 - smoothstep(
        (xx.astype(np.float32) - X_FADE_OUT_START)
        / (X_FADE_OUT_END - X_FADE_OUT_START)
    )
    return MAX_SHIFT * fade_in * fade_out * horizontal_fade


def reveal_lower_arm(pixels: np.ndarray, displacement: np.ndarray) -> np.ndarray:
    height, width = pixels.shape[:2]
    yy, xx = np.mgrid[0:height, 0:width]
    support = displacement > 1e-6
    source_x = np.clip(xx.astype(np.float32) - displacement, 0, width - 1)
    x0 = np.floor(source_x).astype(np.int32)
    x1 = np.minimum(x0 + 1, width - 1)
    fraction = (source_x - x0)[..., None]

    alpha = pixels[..., 3:4].astype(np.float32) / 255.0
    premultiplied = np.concatenate(
        (
            pixels[..., :3].astype(np.float32) * alpha,
            pixels[..., 3:4].astype(np.float32),
        ),
        axis=2,
    )
    sampled = (
        premultiplied[yy, x0] * (1.0 - fraction)
        + premultiplied[yy, x1] * fraction
    )

    output = pixels.copy()
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
    return output


def save_png_atomically(pixels: np.ndarray, destination: Path) -> None:
    temporary = destination.with_name(f".{destination.stem}.tmp.png")
    Image.fromarray(pixels, "RGBA").save(temporary, format="PNG", optimize=True)
    temporary.replace(destination)


def main() -> None:
    paths = [
        ASSETS / f"luban-edge-left-smooth-{index:03d}.png"
        for index in range(1, FRAME_COUNT + 1)
    ]
    missing = [path.name for path in paths if not path.is_file()]
    if missing:
        raise FileNotFoundError(f"Missing side-edge frames: {missing[:4]}")

    before = [load_pixels(path) for path in paths]
    before_hashes = [pixel_sha256(pixels) for pixels in before]
    before_sequence_hash = sequence_sha256(before)
    if REPORT_PATH.is_file():
        previous = json.loads(REPORT_PATH.read_text(encoding="utf-8"))
        previous_outputs = previous.get("outputPixelSha256", [])
        if before_hashes == previous_outputs:
            print(json.dumps(previous, ensure_ascii=False))
            return
        if before_hashes != previous.get("inputPixelSha256", []):
            raise RuntimeError(
                "Side-edge frames differ from both the recorded input and output; "
                "refusing a cumulative or ambiguous rewrite."
            )

    if before_sequence_hash == V1057_OUTPUT_SEQUENCE_SHA256:
        report = {
            "passed": True,
            "alreadyFixed": True,
            "contract": "v1.0.57-side-arm-reveal",
            "frameCount": FRAME_COUNT,
            "canvasSize": list(CANVAS_SIZE),
            "maximumRevealPixels": MAX_SHIFT,
            "minimumChangedPixels": V1057_MINIMUM_CHANGED_PIXELS,
            "maximumChangedPixels": V1057_MAXIMUM_CHANGED_PIXELS,
            "outsideMaskChangedPixels": 0,
            "uniqueOutputFrames": len(set(before_hashes)),
            "inputSequenceSha256": V1057_INPUT_SEQUENCE_SHA256,
            "outputSequenceSha256": before_sequence_hash,
            "inputPixelSha256": [],
            "outputPixelSha256": before_hashes,
        }
        REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
        REPORT_PATH.write_text(
            json.dumps(report, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(json.dumps(report, ensure_ascii=False))
        return

    displacement = build_displacement(CANVAS_SIZE[1], CANVAS_SIZE[0])
    support = displacement > 1e-6
    after = [reveal_lower_arm(pixels, displacement) for pixels in before]
    changed_counts = [
        int(np.count_nonzero(np.any(first != second, axis=2)))
        for first, second in zip(before, after)
    ]
    outside_changes = [
        int(np.count_nonzero(np.any(first[~support] != second[~support], axis=1)))
        for first, second in zip(before, after)
    ]
    if min(changed_counts) <= 0:
        raise RuntimeError("Side-edge arm reveal did not change every authored frame")
    if max(outside_changes) != 0:
        raise RuntimeError("Side-edge arm reveal modified pixels outside its lower-arm mask")
    if len({pixel_sha256(pixels) for pixels in after}) != FRAME_COUNT:
        raise RuntimeError("Side-edge arm sequence lost frame uniqueness")
    after_sequence_hash = sequence_sha256(after)
    if (
        before_sequence_hash == V1057_INPUT_SEQUENCE_SHA256
        and after_sequence_hash != V1057_OUTPUT_SEQUENCE_SHA256
    ):
        raise RuntimeError(
            "Side-edge arm reveal no longer reproduces the v1.0.57 output"
        )

    for path, pixels in zip(paths, after):
        save_png_atomically(pixels, path)

    report = {
        "passed": True,
        "frameCount": FRAME_COUNT,
        "canvasSize": list(CANVAS_SIZE),
        "maximumRevealPixels": MAX_SHIFT,
        "minimumChangedPixels": min(changed_counts),
        "maximumChangedPixels": max(changed_counts),
        "outsideMaskChangedPixels": max(outside_changes),
        "uniqueOutputFrames": len({pixel_sha256(pixels) for pixels in after}),
        "inputSequenceSha256": before_sequence_hash,
        "outputSequenceSha256": after_sequence_hash,
        "inputPixelSha256": before_hashes,
        "outputPixelSha256": [pixel_sha256(pixels) for pixels in after],
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
