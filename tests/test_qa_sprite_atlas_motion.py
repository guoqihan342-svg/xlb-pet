from __future__ import annotations

from pathlib import Path
import re
import sys
from types import SimpleNamespace
import unittest
from unittest import mock


WORKSPACE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKSPACE / "tools"))

import qa_sprite_atlas_motion as motion_qa  # noqa: E402


class _FramePayload:
    def __init__(self, payload: bytes) -> None:
        self._payload = payload

    def tobytes(self) -> bytes:
        return self._payload


class _TypingLoopReader:
    def __init__(self, phase: str, payloads: list[bytes]) -> None:
        prefix = f"Assets/luban-work-{phase}"
        self.resources = [
            f"{prefix}-{frame_number:03d}.png"
            for frame_number in range(1, len(payloads) + 1)
        ]
        self._payloads = dict(zip(self.resources, payloads, strict=True))
        self.page_order = {
            f"work-{phase}": 0,
            f"work-{phase}-part-02": 1,
            f"work-{phase}-part-03": 2,
        }
        self.page_frame_order = {
            page_name: [] for page_name in self.page_order
        }
        self.locations: dict[str, SimpleNamespace] = {}
        for frame_number, resource in enumerate(self.resources, start=1):
            part_number = (frame_number - 1) // 32 + 1
            page_name = (
                f"work-{phase}"
                if part_number == 1
                else f"work-{phase}-part-{part_number:02d}"
            )
            self.locations[resource] = SimpleNamespace(page_name=page_name)
            self.page_frame_order[page_name].append(resource)

    def reconstruct(self, resource: str) -> _FramePayload:
        return _FramePayload(self._payloads[resource])


class WorkTypingLoopContractTests(unittest.TestCase):
    @staticmethod
    def _validate(
        phase: str,
        payloads: list[bytes],
    ) -> list[dict[str, object]]:
        reader = _TypingLoopReader(phase, payloads)
        sequence_name = f"work.{phase}"
        expression = re.compile(
            rf"^Assets/luban-work-{re.escape(phase)}-(\d{{3}})\.png$"
        )
        failures: list[dict[str, object]] = []
        with mock.patch.object(
            motion_qa,
            "SEQUENCE_EXPRESSIONS",
            {sequence_name: expression},
        ):
            motion_qa.validate_resource_contract(
                reader,
                {sequence_name: reader.resources},
                set(reader.resources),
                failures,
            )
        return failures

    @staticmethod
    def _unique_payloads(count: int) -> list[bytes]:
        return [frame_number.to_bytes(4, "little") for frame_number in range(count)]

    @staticmethod
    def _natural_loop_payloads(unique_pose_count: int = 56) -> list[bytes]:
        if unique_pose_count < 2:
            raise ValueError("natural loop needs neutral plus an articulated pose")
        seams = set(motion_qa.WORK_TYPING_NEUTRAL_SEAM_INDICES)
        payloads = [b"neutral"] * 96
        pose_number = 0
        for frame_index in range(96):
            if frame_index in seams:
                continue
            payloads[frame_index] = (
                f"pose-{pose_number % (unique_pose_count - 1):03d}".encode()
            )
            pose_number += 1
        return payloads

    def test_typing_loop_contract_allows_authored_neutral_repeats(self) -> None:
        self.assertEqual(96, motion_qa.WORK_PHASE_FRAME_COUNTS["loop"])
        self.assertEqual(96, motion_qa.WORK_PHASE_FRAME_COUNTS["serious-loop"])
        self.assertEqual(96, motion_qa.WORK_TYPING_LOOP_PERIOD_FRAMES)
        self.assertEqual(56, motion_qa.WORK_TYPING_LOOP_MIN_UNIQUE_POSES)
        self.assertEqual(
            (0, 10, 21, 33, 44, 56, 69, 81, 93),
            motion_qa.WORK_TYPING_NEUTRAL_SEAM_INDICES,
        )
        self.assertEqual(5, motion_qa.WORK_TYPING_MAX_IDENTICAL_RUN_FRAMES)

    def test_56_unique_poses_with_exact_neutral_seams_pass_both_loops(self) -> None:
        payloads = self._natural_loop_payloads(56)
        self.assertEqual(56, len(set(payloads)))
        for phase in ("loop", "serious-loop"):
            with self.subTest(phase=phase):
                self.assertEqual([], self._validate(phase, payloads))

    def test_old_48_frame_typing_loop_is_rejected(self) -> None:
        failures = self._validate("loop", self._unique_payloads(48))

        self.assertTrue(
            any(
                failure["code"] == "sequence.work_count"
                and failure["expected"] == 96
                and failure["actual"] == 48
                for failure in failures
            )
        )

    def test_old_72_frame_typing_loop_is_rejected(self) -> None:
        failures = self._validate("loop", self._unique_payloads(72))

        self.assertTrue(
            any(
                failure["code"] == "sequence.work_count"
                and failure["expected"] == 96
                and failure["actual"] == 72
                for failure in failures
            )
        )

    def test_fewer_than_56_distinct_articulated_poses_is_rejected(self) -> None:
        payloads = self._natural_loop_payloads(55)
        failures = self._validate("loop", payloads)

        self.assertTrue(
            any(
                failure["code"] == "sequence.work_cycle_unique"
                and failure["minimum"] == 56
                and failure["actual"] == 55
                for failure in failures
            )
        )

    def test_a_declared_neutral_seam_with_different_pixels_is_rejected(self) -> None:
        payloads = self._natural_loop_payloads()
        payloads[44] = b"not-neutral"
        failures = self._validate("serious-loop", payloads)

        self.assertTrue(
            any(
                failure["code"] == "sequence.work_neutral_seams"
                and 44 in failure["indices_0_based"]
                for failure in failures
            )
        )

    def test_more_than_five_identical_frames_in_a_row_is_rejected(self) -> None:
        payloads = self._natural_loop_payloads()
        payloads[25:31] = [b"long-static-hold"] * 6
        failures = self._validate("loop", payloads)

        self.assertTrue(
            any(
                failure["code"] == "sequence.work_still_run"
                and failure["maximum"] == 5
                and failure["actual"] == 6
                for failure in failures
            )
        )

    def test_five_frame_cyclic_neutral_rest_is_allowed(self) -> None:
        payloads = self._natural_loop_payloads()
        payloads[92:96] = [b"neutral"] * 4

        self.assertEqual([], self._validate("loop", payloads))


if __name__ == "__main__":
    unittest.main()
