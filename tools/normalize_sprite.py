from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


CANVAS_SIZE = (900, 1100)
MAX_CHARACTER_SIZE = (860, 1060)
BOTTOM_MARGIN = 20


def normalize(source: Path, destination: Path) -> None:
    with Image.open(source) as opened:
        image = opened.convert("RGBA")

    alpha_box = image.getchannel("A").getbbox()
    if alpha_box is None:
        raise ValueError(f"No visible pixels in {source}")

    character = image.crop(alpha_box)
    scale = min(
        MAX_CHARACTER_SIZE[0] / character.width,
        MAX_CHARACTER_SIZE[1] / character.height,
    )
    target_size = (
        max(1, round(character.width * scale)),
        max(1, round(character.height * scale)),
    )
    character = character.resize(target_size, Image.Resampling.LANCZOS)

    canvas = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
    x = (CANVAS_SIZE[0] - character.width) // 2
    y = CANVAS_SIZE[1] - BOTTOM_MARGIN - character.height
    canvas.alpha_composite(character, (x, y))

    destination.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(destination, optimize=True)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Normalize a transparent desktop-pet sprite to a common canvas."
    )
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    normalize(args.source, args.destination)


if __name__ == "__main__":
    main()
