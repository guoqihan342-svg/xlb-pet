from __future__ import annotations

import hashlib
from pathlib import Path
import subprocess
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools" / "fix_edge_side_arm_reveal.py"
EDGE_FRAMES = tuple(
    ROOT / "Assets" / f"luban-edge-left-smooth-{index:03d}.png"
    for index in range(1, 49)
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class RetiredEdgeArmMigrationTests(unittest.TestCase):
    def test_legacy_command_fails_closed_without_rewriting_assets(self) -> None:
        before = tuple(sha256(path) for path in EDGE_FRAMES)

        completed = subprocess.run(
            [sys.executable, str(SCRIPT)],
            cwd=ROOT,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )

        after = tuple(sha256(path) for path in EDGE_FRAMES)
        self.assertEqual(2, completed.returncode)
        self.assertIn("retired and made no changes", completed.stderr)
        self.assertEqual(before, after)

    def test_retired_entry_point_has_no_asset_write_dependencies(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        for forbidden in (
            "from PIL import Image",
            "import numpy",
            "write_text(",
            ".replace(",
            ".save(",
        ):
            self.assertNotIn(forbidden, source)


if __name__ == "__main__":
    unittest.main()
