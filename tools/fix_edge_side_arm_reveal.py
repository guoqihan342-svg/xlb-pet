"""Retired side-edge migration entry point.

The old implementation warped the lower arm in-place by up to seven pixels.
Current edge frames use an authored fixed contact layer instead, so replaying
that migration would corrupt the production assets.  Keep this fail-closed
entry point for anyone following an older command, but never touch files.
"""

from __future__ import annotations

import sys


DEPRECATION_MESSAGE = (
    "fix_edge_side_arm_reveal.py is retired and made no changes. "
    "Rebuild side-edge frames with install_generated_motion_assets.py "
    "--edge-peek followed by generate_dense_motion_assets.py --edge-peek."
)


def main() -> int:
    print(DEPRECATION_MESSAGE, file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
