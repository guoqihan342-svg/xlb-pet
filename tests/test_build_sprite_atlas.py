from __future__ import annotations

from pathlib import Path
import sys
import tempfile
import unittest

from PIL import Image


WORKSPACE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKSPACE / "tools"))

import build_sprite_atlas as atlas  # noqa: E402
import fix_edge_side_arm_reveal as side_grip  # noqa: E402


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
    for action in atlas.SMOOTH_ACTION_NAMES:
        touch_sequence(
            assets,
            f"luban-{action}-smooth",
            atlas.ACTION_SMOOTH_FRAME_COUNTS.get(action, 8),
        )
    for action in atlas.LOOP_ACTION_NAMES:
        touch_sequence(
            assets,
            f"luban-{action}-loop",
            atlas.ACTION_LOOP_FRAME_COUNT,
        )

    touch_sequence(assets, "luban-work-enter", 65)
    touch_sequence(assets, "luban-work-loop", 96)
    touch_sequence(assets, "luban-work-serious-loop", 96)
    touch_sequence(assets, "luban-work-serious-exit", 24)


class ReactionActionRemovalTests(unittest.TestCase):
    def test_rocket_boarding_keeps_swap_frames_on_the_second_page(self) -> None:
        paths = [
            f"Assets/luban-roam-rocket-boarding-{number:03d}.png"
            for number in range(1, 65)
        ]
        partitions = atlas.partition_roam_flight_resource_paths(
            "rocket-boarding",
            paths,
        )
        self.assertEqual([30, 34], [len(part) for _, part in partitions])
        self.assertEqual(paths, [path for _, part in partitions for path in part])
        self.assertEqual(paths[30], partitions[1][1][0])

    def test_removed_reactions_are_not_click_actions(self) -> None:
        self.assertEqual(
            ("cry", "cute", "like", "eat"),
            atlas.ACTION_NAMES,
        )
        self.assertEqual(("cry", "like", "eat"), atlas.LOOP_ACTION_NAMES)
        self.assertEqual("think", atlas.TODO_POSE_NAME)
        self.assertEqual(
            (*atlas.ACTION_NAMES, atlas.TODO_POSE_NAME),
            atlas.SMOOTH_ACTION_NAMES,
        )
        self.assertNotIn("wave", atlas.ACTION_NAMES)
        self.assertNotIn("star-cuddle", atlas.ACTION_NAMES)
        self.assertNotIn("think", atlas.ACTION_NAMES)

    def test_retired_wish_star_overlay_is_absent(self) -> None:
        path = WORKSPACE / "Assets" / "luban-wish-star.png"
        self.assertFalse(path.exists())
        self.assertFalse(
            any(
                resource.endswith("luban-wish-star.png")
                for resource in atlas.resource_paths(WORKSPACE)
            )
        )

    def test_cute_is_a_formal_56_frame_non_loop_sequence(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)

            self.assertEqual(56, len(atlas.action_resource_paths(root, "cute")))
            pages = atlas.page_resource_paths(root)
            self.assertEqual(
                ["action-cute", "action-cute-part-02"],
                [name for name in pages if name.startswith("action-cute")],
            )
            self.assertEqual(
                [32, 24],
                [
                    len(paths)
                    for name, paths in pages.items()
                    if name.startswith("action-cute")
                ],
            )
            self.assertNotIn("loop-cute", pages)

    def test_cute_rejects_a_tail_beyond_56_frames(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            assets = root / "Assets"
            assets.mkdir()
            touch_sequence(assets, "luban-cute-smooth", 57)

            with self.assertRaisesRegex(RuntimeError, "exactly 56 frames"):
                atlas.action_resource_paths(root, "cute")

    def test_legacy_cute_loop_assets_are_not_collected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)
            touch_sequence(
                root / "Assets",
                "luban-cute-loop",
                atlas.ACTION_LOOP_FRAME_COUNT,
            )

            source_paths = atlas.resource_paths(root)
            pages = atlas.page_resource_paths(root)
            self.assertFalse(
                any(path.startswith("Assets/luban-cute-loop-") for path in source_paths)
            )
            self.assertNotIn("loop-cute", pages)

    def test_legacy_removed_action_assets_are_not_collected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)
            assets = root / "Assets"
            touch_sequence(assets, "luban-yawn-smooth", 84)
            touch_sequence(
                assets,
                "luban-yawn-loop",
                atlas.ACTION_LOOP_FRAME_COUNT,
            )
            touch_sequence(assets, "luban-star-cuddle-smooth", 144)

            source_paths = atlas.resource_paths(root)
            pages = atlas.page_resource_paths(root)
            self.assertFalse(any("yawn" in path for path in source_paths))
            self.assertFalse(any("yawn" in name for name in pages))
            self.assertFalse(any("star-cuddle" in path for path in source_paths))
            self.assertFalse(any("star-cuddle" in name for name in pages))

    def test_todo_think_smooth_is_collected_without_a_click_loop(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)

            source_paths = atlas.resource_paths(root)
            pages = atlas.page_resource_paths(root)

            self.assertTrue(
                any(
                    path.startswith("Assets/luban-think-smooth-")
                    for path in source_paths
                )
            )
            self.assertFalse(
                any(
                    path.startswith("Assets/luban-think-loop-")
                    for path in source_paths
                )
            )
            self.assertIn("action-think", pages)
            self.assertNotIn("loop-think", pages)

    def test_legacy_reaction_think_loop_assets_are_not_collected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)
            touch_sequence(
                root / "Assets",
                "luban-think-loop",
                atlas.ACTION_LOOP_FRAME_COUNT,
            )

            source_paths = atlas.resource_paths(root)
            pages = atlas.page_resource_paths(root)

            self.assertFalse(
                any(
                    path.startswith("Assets/luban-think-loop-")
                    for path in source_paths
                )
            )
            self.assertNotIn("loop-think", pages)

    def test_legacy_reaction_wave_assets_are_not_collected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)
            assets = root / "Assets"
            touch_sequence(assets, "luban-wave-smooth", 98)
            touch_sequence(
                assets,
                "luban-wave-loop",
                atlas.ACTION_LOOP_FRAME_COUNT,
            )

            source_paths = atlas.resource_paths(root)
            pages = atlas.page_resource_paths(root)

            self.assertFalse(
                any(path.startswith("Assets/luban-wave-") for path in source_paths)
            )
            self.assertFalse(
                any(
                    name == "action-wave"
                    or name.startswith("action-wave-part-")
                    or name == "loop-wave"
                    for name in pages
                )
            )

    def test_optional_roam_wave_contract_is_preserved(self) -> None:
        self.assertIn("wave", atlas.OPTIONAL_ROAM_SEQUENCES)
        self.assertIn("wave", atlas.ROAM_FLIGHT_SEQUENCES)
        roam_generator_source = (
            WORKSPACE / "tools" / "build_roam_flight_assets.py"
        ).read_text(encoding="utf-8")
        self.assertIn('"--include-wave"', roam_generator_source)
        self.assertIn("luban-roam-wave-001..064.png", roam_generator_source)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)
            touch_sequence(
                root / "Assets",
                "luban-roam-wave",
                atlas.MIN_ROAM_FRAME_COUNT,
            )

            self.assertIn("wave", atlas.runtime_roam_sequences(root))
            pages = atlas.page_resource_paths(root)
            self.assertEqual(
                [name for name in pages if name.startswith("roam-wave")],
                ["roam-wave", "roam-wave-part-02"],
            )

    def test_optional_rocket_roaming_assets_must_be_a_complete_pair(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            assets = root / "Assets"
            assets.mkdir()

            self.assertEqual(
                atlas.runtime_roam_sequences(root),
                atlas.REQUIRED_ROAM_SEQUENCES,
            )

            (assets / "luban-roam-rocket-boarding-001.png").touch()
            with self.assertRaisesRegex(
                RuntimeError,
                "must contain both boarding and flight",
            ):
                atlas.runtime_roam_sequences(root)

            (assets / "luban-roam-rocket-flight-001.png").touch()
            self.assertEqual(
                atlas.runtime_roam_sequences(root),
                (
                    *atlas.REQUIRED_ROAM_SEQUENCES,
                    "rocket-boarding",
                    "rocket-flight",
                ),
            )

    def test_reaction_wave_cli_and_generator_mappings_are_removed(self) -> None:
        generator_source = (
            WORKSPACE / "tools" / "generate_dense_motion_assets.py"
        ).read_text(encoding="utf-8")
        installer_source = (
            WORKSPACE / "tools" / "install_generated_motion_assets.py"
        ).read_text(encoding="utf-8")

        self.assertNotIn("wave", generator_source.lower())
        self.assertNotIn('"wave": "wave-v7-24-sheet-alpha.png"', installer_source)
        self.assertNotIn('"wave": "action-v10-wave-entry-rife-alpha.png"', installer_source)
        self.assertNotIn('"wave": 180', installer_source)
        for tool_name in (
            "build_sprite_atlas.py",
            "install_generated_motion_assets.py",
            "generate_dense_motion_assets.py",
            "qa_dense_motion_assets.py",
            "qa_sprite_atlas_motion.py",
        ):
            source = (WORKSPACE / "tools" / tool_name).read_text(encoding="utf-8")
            self.assertNotIn("yawn", source.lower(), tool_name)
            self.assertNotIn("butterfly", source.lower(), tool_name)
            action_declaration = next(
                line
                for line in source.splitlines()
                if line.startswith("ACTION_NAMES = ")
            )
            self.assertNotIn('"wave"', action_declaration, tool_name)
            self.assertNotIn('"think"', action_declaration, tool_name)
            smooth_declaration = next(
                line
                for line in source.splitlines()
                if line.startswith("SMOOTH_ACTION_NAMES = ")
            )
            self.assertIn("TODO_POSE_NAME", smooth_declaration, tool_name)
            loop_declaration = next(
                line
                for line in source.splitlines()
                if line.startswith("LOOP_ACTION_NAMES = ")
            )
            self.assertEqual(
                'LOOP_ACTION_NAMES = ("cry", "like", "eat")',
                loop_declaration,
                tool_name,
            )

    def test_removed_action_has_no_derived_repository_assets(self) -> None:
        tracked_locations = (WORKSPACE / "Assets", WORKSPACE / "tools" / "generated_sources")
        derived = [
            path
            for location in tracked_locations
            for path in location.glob("*yawn*")
        ]
        self.assertEqual([], [path.name for path in derived])
        self.assertTrue((WORKSPACE / "pic" / "小鲁班1.jpg").is_file())

    def test_repository_has_only_the_reachable_cute_runtime_assets(self) -> None:
        assets = WORKSPACE / "Assets"
        self.assertEqual([], [path.name for path in assets.glob("luban-cute-loop-*.png")])
        self.assertEqual(
            [],
            [
                path.name
                for path in assets.glob("luban-cute-smooth-*.png")
                if int(path.stem.rsplit("-", 1)[-1]) > 56
            ],
        )
    def test_retired_butterfly_is_absent_and_never_collected(self) -> None:
        path = WORKSPACE / "Assets" / "luban-butterfly.png"
        self.assertFalse(path.exists())
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)
            overlay = root / "Assets" / "luban-butterfly.png"
            overlay.touch()
            self.assertNotIn(
                "Assets/luban-butterfly.png",
                atlas.resource_paths(root),
            )
            self.assertFalse(
                any(
                    "butterfly" in resource
                    for resources in atlas.page_resource_paths(root).values()
                    for resource in resources
                )
            )

    def test_repository_has_no_generated_reaction_wave_pngs(self) -> None:
        assets = WORKSPACE / "Assets"
        paths = sorted(
            [*assets.glob("luban-wave*.png"), *assets.glob("luban-idle-to-wave*.png")]
        )
        self.assertEqual([], [path.name for path in paths])


class CompactSideGripTests(unittest.TestCase):
    def test_retired_seven_pixel_warp_cannot_run(self) -> None:
        source = (
            WORKSPACE / "tools" / "fix_edge_side_arm_reveal.py"
        ).read_text(encoding="utf-8")
        self.assertNotIn("def build_" + "displacement", source)
        self.assertNotIn("MAX_" + "SHIFT", source)
        self.assertNotIn("v1.0.57-side-arm-reveal", source)
        self.assertIn(side_grip.CONTRACT_NAME, source)

    def test_compact_curve_has_the_locked_pose_phases(self) -> None:
        self.assertLess(
            side_grip.side_grip_phase(1),
            side_grip.side_grip_phase(12),
        )
        self.assertAlmostEqual(
            side_grip.side_grip_phase(12),
            side_grip.side_grip_phase(36),
        )
        self.assertEqual(1.0, side_grip.side_grip_phase(24))
        self.assertLess(side_grip.side_grip_phase(48), 1e-12)
        self.assertLessEqual(
            max(
                side_grip.maximum_quantized_horizontal_run(frame_number)
                for frame_number in range(1, side_grip.FRAME_COUNT + 1)
            ),
            side_grip.MAX_HORIZONTAL_BOTTOM_RUN,
        )
        self.assertEqual(
            frozenset({1, 2, 3, 4, 5, 44, 45, 46, 47, 48}),
            side_grip.ENDPOINT_BRIDGE_FRAMES,
        )

    def test_formal_compact_sequence_passes_hard_continuity_qa(self) -> None:
        frames = [
            side_grip.load_pixels(
                WORKSPACE
                / "Assets"
                / f"luban-edge-left-smooth-{frame_number:03d}.png"
            )
            for frame_number in range(1, side_grip.FRAME_COUNT + 1)
        ]
        metrics = side_grip.analyze_sequence(frames)
        self.assertEqual([], metrics["failures"])
        self.assertEqual(side_grip.FRAME_COUNT, metrics["uniqueFrames"])
        self.assertEqual(
            "horizontal-mirror-of-left",
            metrics["rightEdgeRuntimeContract"],
        )
        self.assertGreaterEqual(
            metrics["minEndpointBridgeAlphaPixels"],
            side_grip.MIN_ENDPOINT_BRIDGE_ALPHA_PIXELS,
        )

    def test_each_quarter_frame_is_the_exact_compact_key(self) -> None:
        for key_number, frame_number in side_grip.KEY_PHASE_FRAMES.items():
            with self.subTest(key=key_number, frame=frame_number):
                key = side_grip.load_pixels(
                    WORKSPACE / "Assets" / f"luban-edge-left-{key_number:02d}.png"
                )
                smooth = side_grip.load_pixels(
                    WORKSPACE
                    / "Assets"
                    / f"luban-edge-left-smooth-{frame_number:03d}.png"
                )
                self.assertEqual(
                    side_grip.pixel_sha256(key),
                    side_grip.pixel_sha256(smooth),
                )

    def test_formal_side_grip_projection_is_idempotent_for_all_48_frames(self) -> None:
        for frame_number in range(1, side_grip.FRAME_COUNT + 1):
            with self.subTest(frame=frame_number):
                frame = side_grip.load_pixels(
                    WORKSPACE
                    / "Assets"
                    / f"luban-edge-left-smooth-{frame_number:03d}.png"
                )
                output, metrics = side_grip.reshape_side_grip_frame(
                    frame, frame_number
                )
                self.assertTrue(metrics["alreadyFinal"])
                self.assertEqual(
                    side_grip.pixel_sha256(frame),
                    side_grip.pixel_sha256(output),
                )


class WorkSequenceTests(unittest.TestCase):
    def test_work_generator_has_only_the_four_runtime_phases(self) -> None:
        generator_source = (
            WORKSPACE / "tools" / "build_work_animation.py"
        ).read_text(encoding="utf-8")
        self.assertNotIn("work-" + "tap", generator_source.lower())
        self.assertNotIn("tap_frames", generator_source)
        self.assertNotIn("TAP_HAND_PATH", generator_source)
        self.assertNotIn('write_sequence("tap"', generator_source)
        self.assertIn(
            "SERIOUS_ENTER_SOURCE_FRAME_INDICES = "
            "(23, 20, 16, 13, 10, 7, 3, 0)",
            generator_source,
        )
        self.assertIn("write_face_transition_contact(serious_exit_frames)", generator_source)
        for declaration in (
            "ENTER_FRAME_COUNT = 48",
            "LOOP_FRAME_COUNT = 96",
            "SERIOUS_LOOP_FRAME_COUNT = 96",
            "SERIOUS_EXIT_FRAME_COUNT = 24",
        ):
            self.assertIn(declaration, generator_source)
        self.assertEqual(264, sum(atlas.WORK_MIN_FRAME_COUNTS.values()))

    def test_work_phases_are_not_click_actions(self) -> None:
        self.assertNotIn("work", atlas.ACTION_NAMES)
        self.assertTrue(set(atlas.WORK_PHASES).isdisjoint(atlas.ACTION_NAMES))
        self.assertEqual(
            ("enter", "loop", "serious-loop", "serious-exit"),
            atlas.WORK_PHASES,
        )
        self.assertNotIn("tap", atlas.WORK_MIN_FRAME_COUNTS)

    def test_legacy_work_tap_files_are_not_part_of_the_atlas_contract(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            make_complete_path_fixture(root)
            touch_sequence(root / "Assets", "luban-work-tap", 33)

            with self.assertRaisesRegex(ValueError, "Unknown working-animation phase"):
                atlas.work_resource_paths(root, "tap")
            with self.assertRaisesRegex(ValueError, "Unknown working-animation phase"):
                atlas.partition_work_resource_paths(
                    "tap",
                    ["Assets/luban-work-tap-001.png"],
                )

            source_paths = atlas.resource_paths(root)
            pages = atlas.page_resource_paths(root)
            self.assertFalse(
                any(path.startswith("Assets/luban-work-tap-") for path in source_paths)
            )
            self.assertFalse(any(name.startswith("work-tap") for name in pages))

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
            self.assertEqual(len(work_paths), 65 + 96 + 96 + 24)
            self.assertEqual(
                [name for name in pages if name.startswith("work-")],
                [
                    "work-enter",
                    "work-enter-part-02",
                    "work-enter-part-03",
                    "work-loop",
                    "work-loop-part-02",
                    "work-loop-part-03",
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
