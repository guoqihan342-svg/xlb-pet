from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


PREVIEW_SIZE = (240, 293)
BACKGROUND = (242, 246, 252, 255)


def load_frame(path: Path) -> Image.Image:
    with Image.open(path) as opened:
        sprite = opened.convert("RGBA")
    sprite.thumbnail(PREVIEW_SIZE, Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", PREVIEW_SIZE, BACKGROUND)
    x = (PREVIEW_SIZE[0] - sprite.width) // 2
    y = PREVIEW_SIZE[1] - sprite.height
    canvas.alpha_composite(sprite, (x, y))
    return canvas.convert("RGB")


def build_preview(assets: Path, action: str, output: Path) -> None:
    timeline = [assets / "luban-idle.png"]
    timeline.extend(
        assets / f"luban-wake-{frame_number:02d}.png"
        for frame_number in range(1, 13)
    )
    timeline.extend(
        assets / f"luban-{action}-frame-{frame_number:02d}.png"
        for frame_number in range(1, 25)
    )

    missing = [path for path in timeline if not path.exists()]
    if missing:
        raise FileNotFoundError(f"Missing preview frame: {missing[0]}")

    action_loop = [
        assets / f"luban-{action}-frame-{frame_number:02d}.png"
        for _ in range(10)
        for frame_number in range(21, 25)
    ]
    playback = timeline + action_loop + list(reversed(timeline[:-1]))
    frames = [load_frame(path) for path in playback]
    durations = [500] + [50] * 36 + [150] * 40 + [50] * 36
    output.parent.mkdir(parents=True, exist_ok=True)
    frames[0].save(
        output,
        save_all=True,
        append_images=frames[1:],
        duration=durations,
        loop=0,
        optimize=True,
        disposal=2,
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build a GIF preview from one desktop-pet motion timeline."
    )
    parser.add_argument("action")
    parser.add_argument("output", type=Path)
    parser.add_argument("--assets", type=Path, default=Path("Assets"))
    args = parser.parse_args()
    build_preview(args.assets, args.action, args.output)


if __name__ == "__main__":
    main()
