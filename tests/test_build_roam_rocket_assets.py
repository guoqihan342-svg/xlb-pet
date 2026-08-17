from __future__ import annotations

import hashlib
import inspect
from pathlib import Path
import sys
import unittest

import numpy as np
from PIL import Image


WORKSPACE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKSPACE / "tools"))

import build_roam_rocket_assets as rocket  # noqa: E402


class RocketRoamingAssetContractTests(unittest.TestCase):
    @staticmethod
    def _sequence(name: str) -> list[Path]:
        return sorted(
            (WORKSPACE / "Assets").glob(f"luban-roam-rocket-{name}-*.png")
        )

    @staticmethod
    def _sha256(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def test_committed_sequences_are_complete_unique_and_seam_exact(self) -> None:
        boarding = self._sequence("boarding")
        flight = self._sequence("flight")

        self.assertEqual(rocket.BOARDING_FRAME_COUNT, len(boarding))
        self.assertEqual(rocket.FLIGHT_FRAME_COUNT, len(flight))
        self.assertEqual(len(boarding), len({self._sha256(path) for path in boarding}))
        self.assertEqual(len(flight), len({self._sha256(path) for path in flight}))
        self.assertEqual(
            self._sha256(WORKSPACE / "Assets" / "luban-idle.png"),
            self._sha256(boarding[0]),
        )
        self.assertEqual(self._sha256(boarding[-1]), self._sha256(flight[0]))
        self.assertNotEqual(self._sha256(flight[0]), self._sha256(flight[-1]))

        for path in (*boarding, *flight):
            with Image.open(path) as image:
                self.assertEqual(rocket.RUNTIME_SIZE, image.size)
                self.assertEqual("RGBA", image.mode)
                self.assertIsNotNone(image.getchannel("A").getbbox())

        for path in flight:
            with Image.open(path) as image:
                alpha = np.asarray(image.getchannel("A"), dtype=np.uint8)
            self.assertFalse(np.any(alpha[0, :]), path.name)
            self.assertFalse(np.any(alpha[-1, :]), path.name)
            self.assertFalse(np.any(alpha[:, 0]), path.name)
            self.assertFalse(np.any(alpha[:, -1]), path.name)

    def test_boarding_uses_one_character_layer_on_each_side_of_the_flash(self) -> None:
        source = inspect.getsource(rocket.build_boarding_frames)

        self.assertIn("if progress < 0.5:", source)
        self.assertIn("frame.alpha_composite(idle)", source)
        self.assertIn("frame.alpha_composite(rocket_layer)", source)
        self.assertNotIn("idle_layer = transformed(", source)
        self.assertNotIn("with_opacity(", source)
        sparkle_source = inspect.getsource(rocket.add_launch_sparkles)
        self.assertIn("if alpha < 160:", sparkle_source)
        flash_source = inspect.getsource(rocket.add_launch_flash)
        self.assertIn("range(29, 35)", flash_source)
        self.assertIn("(226, 220, 55)", flash_source)
        self.assertIn("(147, 220, 55)", flash_source)
        self.assertIn("(305, 220, 55)", flash_source)
        self.assertIn("fill=(204, 222, 236, 255)", flash_source)
        self.assertNotIn("outer_radius", flash_source)

    def test_selected_key_splits_into_one_rocket_and_three_equal_clouds(self) -> None:
        rocket_source, cloud_sources = rocket.split_generated_key(
            rocket.DEFAULT_SOURCE
        )
        clouds = rocket.normalize_cloud_keys(cloud_sources)
        normalized_rocket = rocket.normalize_rocket_key(rocket_source)

        self.assertGreater(
            rocket_source.width,
            max(cloud.width for cloud in cloud_sources),
        )
        self.assertEqual(rocket.CLOUD_COUNT, len(cloud_sources))
        self.assertEqual(
            [rocket.CLOUD_SIZE] * rocket.CLOUD_COUNT,
            [cloud.size for cloud in clouds],
        )
        for cloud in clouds:
            self.assertIsNotNone(cloud.getchannel("A").getbbox())
        rocket_bounds = normalized_rocket.getchannel("A").getbbox()
        self.assertIsNotNone(rocket_bounds)
        assert rocket_bounds is not None
        self.assertGreaterEqual(rocket_bounds[2] - rocket_bounds[0], 290)
        self.assertGreaterEqual(rocket_bounds[3] - rocket_bounds[1], 320)

    def test_three_clouds_move_quickly_without_overlapping(self) -> None:
        layouts = [
            rocket.cloud_layout_for_frame(index)
            for index in range(rocket.FLIGHT_FRAME_COUNT)
        ]

        for layout in layouts:
            self.assertEqual(rocket.CLOUD_COUNT, len(layout))
            ordered = sorted(layout)
            for x, y, opacity in ordered:
                self.assertGreaterEqual(x, 0)
                self.assertLessEqual(x + rocket.CLOUD_SIZE[0], rocket.RUNTIME_SIZE[0])
                self.assertGreaterEqual(y, 0)
                self.assertLessEqual(y + rocket.CLOUD_SIZE[1], rocket.RUNTIME_SIZE[1])
                self.assertGreaterEqual(opacity, 218)
                self.assertLessEqual(opacity, 255)
            for left, right in zip(ordered, ordered[1:]):
                self.assertGreaterEqual(
                    right[0] - left[0],
                    rocket.CLOUD_SIZE[0],
                )

            rocket_right = rocket.ROCKET_LEFT + rocket.ROCKET_MAXIMUM_SIZE[0]
            self.assertGreaterEqual(
                ordered[0][0] + rocket.CLOUD_SIZE[0] - rocket_right,
                12,
            )

        for cloud_index in range(rocket.CLOUD_COUNT):
            deltas = []
            for frame_index in range(rocket.FLIGHT_FRAME_COUNT):
                current_x = layouts[frame_index][cloud_index][0]
                next_x = layouts[
                    (frame_index + 1) % rocket.FLIGHT_FRAME_COUNT
                ][cloud_index][0]
                deltas.append(next_x - current_x)
            self.assertEqual({-7, 2, 3}, set(deltas))
            self.assertEqual(16, deltas.count(-7))


if __name__ == "__main__":
    unittest.main()
