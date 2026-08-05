from __future__ import annotations

from pathlib import Path
import sys
import tempfile
import unittest


WORKSPACE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKSPACE / "tools"))

import build_sprite_atlas as atlas  # noqa: E402


def touch_sequence(assets: Path, prefix: str, frame_count: int) -> None:
    for frame_number in range(1, frame_count + 1):
        (assets / f"{prefix}-{frame_number:03d}.png").touch()


def make_complete_path_fixture(root: Path) -> None:
    """Create names only; resource/page enumeration never decodes these files."""

    assets = root / "Assets"
    assets.mkdir()
    (assets / "luban-idle.png").touch()
    touch_sequence(assets, "luban-wake-smooth", 8)
    for direction in atlas.RUNTIME_EDGE_DIRECTIONS:
        touch_sequence(
            assets,
            f"luban-edge-{direction}-smooth",
            atlas.EDGE_PEEK_FRAME_COUNT,
        )
    for sequence in atlas.REQUIRED_ROAM_SEQUENCES:
        touch_sequence(
            assets,
            f"luban-roam-{sequence}",
            atlas.MIN_ROAM_FRAME_COUNT,
        )
    for phase in atlas.REMINDER_PHASES:
        touch_sequence(assets, f"luban-reminder-{phase}", 1)
    for action in atlas.ACTION_NAMES:
        touch_sequence(assets, f"luban-{action}-smooth", 8)
        touch_sequence(
            assets,
            f"luban-{action}-loop",
            atlas.ACTION_LOOP_FRAME_COUNT,
        )

    touch_sequence(assets, "luban-work-enter", 65)
    touch_sequence(assets, "luban-work-loop", 96)
    touch_sequence(assets, "luban-work-tap", 33)
    touch_sequence(assets, "luban-work-serious-loop", 96)
    touch_sequence(assets, "luban-work-serious-exit", 24)


class WorkSequenceTests(unittest.TestCase):
    def test_work_phases_are_not_click_actions(self) -> None:
        self.assertNotIn("work", atlas.ACTION_NAMES)
        self.assertTrue(set(atlas.WORK_PHASES).isdisjoint(atlas.ACTION_NAMES))

    def test_typing_loops_require_the_complete_96_frame_cycle(self) -> None:
        self.assertEqual(96, atlas.WORK_MIN_FRAME_COUNTS["loop"])
        self.assertEqual(96, atlas.WORK_MIN_FRAME_COUNTS["serious-loop"])

    def test_each_work_sequence_rejects_too_few_frames(self) -> None:
        for phase, minimum in atlas.WORK_MIN_FRAME_COUNTS.items():
            with self.subTest(phase=phase), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                assets = root / "Assets"
                assets.mkdir()
                touch_sequence(assets, f"luban-work-{phase}", minimum - 1)

                with self.assertRaisesRegex(
                    RuntimeError,
                    rf"{phase} must contain at least {minimum} frames",
                ):
                    atlas.work_resource_paths(root, phase)

    def test_each_work_sequence_rejects_a_numbering_gap(self) -> None:
        for phase, minimum in atlas.WORK_MIN_FRAME_COUNTS.items():
            with self.subTest(phase=phase), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                assets = root / "Assets"
                assets.mkdir()
                for frame_number in range(1, minimum + 2):
                    if frame_number != 2:
                        (assets / f"luban-work-{phase}-{frame_number:03d}.png").touch()

                with self.assertRaisesRegex(RuntimeError, "contiguous from 001"):
                    atlas.work_resource_paths(root, phase)

    def test_each_work_sequence_rejects_a_malformed_number(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            assets = root / "Assets"
            assets.mkdir()
            touch_sequence(assets, "luban-work-enter", 24)
            (assets / "luban-work-enter-25.png").touch()

            with self.assertRaisesRegex(RuntimeError, "Malformed dense sprite name"):
                atlas.work_resource_paths(root, "enter")

    def test_work_pages_are_contiguous_and_capped_at_32_frames(self) -> None:
        cases = {
            "enter": (65, [32, 32, 1]),
            "loop": (96, [32, 32, 32]),
            "tap": (33, [32, 1]),
            "serious-loop": (96, [32, 32, 32]),
            "serious-exit": (24, [24]),
        }
        for phase, (frame_count, expected_sizes) in cases.items():
            with self.subTest(phase=phase):
                paths = [
                    f"Assets/luban-work-{phase}-{frame_number:03d}.png"
                    for frame_number in range(1, frame_count + 1)
                ]
                partitions = atlas.partition_work_resource_paths(phase, paths)
                expected_names = [
                    f"work-{phase}",
                    *[
                        f"work-{phase}-part-{part_number:02d}"
                        for part_number in range(2, len(expected_sizes) + 1)
                    ],
                ]

                self.assertEqual(
                    [page_name for page_name, _ in partitions],
                    expected_names,
                )
                self.assertEqual(
                    [len(page_paths) for _, page_paths in partitions],
                    expected_sizes,
                )
                self.assertTrue(
                    all(
                        0 < len(page_paths) <= atlas.WORK_PAGE_FRAME_LIMIT
                        for _, page_paths in partitions
                    )
                )
                self.assertEqual(
                    [path for _, page_paths in partitions for path in page_paths],
                    paths,
                )

    def test_page_and_source_unions_cover_work_sequences_once(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)

            source_paths = atlas.resource_paths(root)
            pages = atlas.page_resource_paths(root)
            page_paths = [path for paths in pages.values() for path in paths]
            work_paths = [
                path for path in source_paths if path.startswith("Assets/luban-work-")
            ]

            self.assertEqual(set(page_paths), set(source_paths))
            self.assertEqual(len(page_paths), len(source_paths))
            self.assertEqual(len(page_paths), len(set(page_paths)))
            self.assertEqual(len(work_paths), 65 + 96 + 33 + 96 + 24)
            self.assertEqual(
                [name for name in pages if name.startswith("work-")],
                [
                    "work-enter",
                    "work-enter-part-02",
                    "work-enter-part-03",
                    "work-loop",
                    "work-loop-part-02",
                    "work-loop-part-03",
                    "work-tap",
                    "work-tap-part-02",
                    "work-serious-loop",
                    "work-serious-loop-part-02",
                    "work-serious-loop-part-03",
                    "work-serious-exit",
                ],
            )
            self.assertFalse(any(name.startswith("action-work") for name in pages))
            self.assertTrue(all(page_paths.count(path) == 1 for path in work_paths))


if __name__ == "__main__":
    unittest.main()
