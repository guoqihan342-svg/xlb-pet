from __future__ import annotations

import argparse
import brotli
from collections import OrderedDict, defaultdict
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import sys
from typing import Any, Iterable

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MANIFEST = ROOT / "Assets" / "luban-sprite-pages.json"
DEFAULT_OUTPUT = ROOT / ".codex_tmp" / "sprite-atlas-motion-qa"

DISPLAY_WIDTH = 399
DISPLAY_HEIGHT = 509
ATLAS_MANIFEST_VERSION = 4
ATLAS_COMPRESSION = "brotli"
DIRECT_PAGE_ENCODING = "pbgra32"
DELTA_SUB_PAGE_ENCODING = "pbgra32-delta-sub-v1"
SUPPORTED_PAGE_ENCODINGS = frozenset(
    (DIRECT_PAGE_ENCODING, DELTA_SUB_PAGE_ENCODING)
)
DELTA_SUB_HEADER = struct.Struct("<4H")
MAX_DECODED_PAGE_BYTES = 24 * 1024 * 1024
MAX_PAGE_PAYLOAD_BYTES = 32 * 1024 * 1024
FRAME_DESCRIPTOR_KEYS = (
    "x",
    "y",
    "width",
    "height",
    "destinationX",
    "destinationY",
)
PET_WIDTH_DIP = 190.0
PET_HEIGHT_DIP = 242.0
ACTIONS = ("yawn", "cry", "cute", "like", "eat", "wave", "think")
RUNTIME_EDGE_DIRECTIONS = ("left", "bottom")
ROAM_LOOP_SEQUENCES = ("flight", "wave")
ROAM_NON_LOOP_SEQUENCES = ("boarding",)
ROAM_FLIGHT_SEQUENCES = (*ROAM_LOOP_SEQUENCES, *ROAM_NON_LOOP_SEQUENCES)
ROAM_BOARDING_SEQUENCE = "roam.boarding"
OPTIONAL_SEQUENCE_NAMES = frozenset(("roam.wave",))
MIN_ROAM_FRAME_COUNT = 48
EDGE_PEEK_FRAME_COUNT = 48
REMINDER_PHASE_FRAME_COUNTS = {"enter": 33, "hold": 48}
WORK_PHASE_FRAME_COUNTS = {
    "enter": 48,
    "loop": 96,
    "tap": 48,
    "serious-loop": 96,
    "serious-exit": 24,
}
WORK_TYPING_LOOP_PHASES = frozenset(("loop", "serious-loop"))
WORK_TYPING_LOOP_PERIOD_FRAMES = 96
WORK_TYPING_LOOP_MIN_UNIQUE_POSES = 56
WORK_TYPING_NEUTRAL_SEAM_INDICES = (0, 10, 21, 33, 44, 56, 69, 81, 93)
WORK_TYPING_MAX_IDENTICAL_RUN_FRAMES = 5
WORK_TRANSITION_MIN_UNIQUE_POSES = {
    "enter": 48,
    # Tap deliberately has byte-identical normal-neutral endpoints.
    "tap": 36,
    "serious-exit": 20,
}
# Enter uses an opaque cartoon cloud to cover a deliberate scene swap and tap
# introduces an external hand from outside the canvas. Their transition-specific
# invariants are enforced by build_work_animation.py; the generic whole-silhouette
# motion gate remains applicable to the steady work loop.
MOTION_ANALYSIS_EXCLUDED_SEQUENCES = frozenset(("work.enter", "work.tap"))
USER_SCALES = (0.75, 1.0, 1.4)
DPI_SCALES = (1.0, 1.25, 1.5)
MAX_USER_DPI_SCALE = max(USER_SCALES) * max(DPI_SCALES)

# These are visual, not source-authoring, limits.  Physical-size checks add at
# most one output-pixel of quantisation allowance; the native 399x509 surface
# keeps the exact ratio limits.
MIN_ALPHA_IOU = 0.92
MIN_MEAN_ALPHA_IOU = 0.95
MAX_HEAD_CENTER_STEP_DIP = 2.0
MAX_HAT_SCALE_STEP = 0.025
MAX_TORSO_SCALE_STEP = 0.035
MAX_BASELINE_STEP_PHYSICAL_PX = 1.15
MAX_CENTROID_SECOND_DIFFERENCE_DIP = 1.0
MAX_HEAD_SECOND_DIFFERENCE_DIP = 1.0
MAX_TRANSIENT_EDGE_RATIO = 0.010
MAX_WIDE_TRANSLUCENT_TRAIL_RATIO = 0.0015
RUNTIME_STABLE_ACTION_PREFIXES = {
    # The regenerated 56-frame prefix contains one deliberately oversampled
    # crouch/recovery and ends at the complete raised-hands/open-smile pose.
    "cute.runtime": ("cute.smooth", 56),
}
MAX_RUNTIME_CENTROID_SECOND_DIFFERENCE_DIP = 0.75
# The blue-brim component box is quantised to whole source pixels, so a
# sub-pixel-smooth path can report a one-pixel direction flip. Old visible cute
# bobs measured 1.35/2.15 DIP; keep the runtime failure threshold below those
# while allowing the regenerated path's 0.75-DIP quantisation-only findings.
MAX_RUNTIME_HEAD_MICRO_ROUNDTRIP_DIP = 1.0
# Boarding is an authored non-loop transition from the prone idle silhouette to
# the mounted flight silhouette.  It may legitimately translate the character
# and change the occupied outline, so it has a displacement/trail profile
# instead of the steady-loop IoU and fixed-silhouette scale profile above.
MAX_BOARDING_CENTROID_STEP_DIP = 10.0
MAX_BOARDING_HEAD_CENTER_STEP_DIP = 12.0
MAX_BOARDING_WIDE_TRANSLUCENT_TRAIL_RATIO = 0.010
MAX_EDGE_CONTACT_ERROR_PX = 1.0
MAX_EDGE_CONTACT_STEP_PX = 1.0
FLOAT_COMPARISON_EPSILON = 1e-9

# A waiver must identify one sequence, one metric, and one exact transition.
# There are intentionally no blanket action/sequence waivers.
EXACT_PAIR_WAIVERS: dict[tuple[str, str, int, int], str] = {}
EXACT_CENTER_WAIVERS: dict[tuple[str, str, int], str] = {}

SEQUENCE_EXPRESSIONS = {
    "wake.smooth": re.compile(r"^Assets/luban-wake-smooth-(\d{3})\.png$"),
    **{
        f"edge.{direction}": re.compile(
            rf"^Assets/luban-edge-{re.escape(direction)}-smooth-(\d{{3}})\.png$"
        )
        for direction in RUNTIME_EDGE_DIRECTIONS
    },
    **{
        f"roam.{sequence}": re.compile(
            rf"^Assets/luban-roam-{re.escape(sequence)}-(\d{{3}})\.png$"
        )
        for sequence in ROAM_FLIGHT_SEQUENCES
    },
    **{
        f"{action}.smooth": re.compile(
            rf"^Assets/luban-{re.escape(action)}-smooth-(\d{{3}})\.png$"
        )
        for action in ACTIONS
    },
    **{
        f"{action}.loop": re.compile(
            rf"^Assets/luban-{re.escape(action)}-loop-(\d{{3}})\.png$"
        )
        for action in ACTIONS
    },
    **{
        f"reminder.{phase}": re.compile(
            rf"^Assets/luban-reminder-{re.escape(phase)}-(\d{{3}})\.png$"
        )
        for phase in REMINDER_PHASE_FRAME_COUNTS
    },
    **{
        f"work.{phase}": re.compile(
            rf"^Assets/luban-work-{re.escape(phase)}-(\d{{3}})\.png$"
        )
        for phase in WORK_PHASE_FRAME_COUNTS
    },
}


@dataclass(frozen=True)
class FrameLocation:
    page_name: str
    descriptor: dict[str, int]


@dataclass(frozen=True)
class Surface:
    name: str
    width: int
    height: int
    x_to_dip: float
    y_to_dip: float
    physical_scale: float | None


def sha256_bytes(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def add_failure(
    failures: list[dict[str, Any]],
    code: str,
    message: str,
    **context: Any,
) -> None:
    failures.append({"code": code, "message": message, **context})


def normalized_resource_path(value: str) -> str:
    return value.replace("\\", "/")


def is_manifest_integer(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def is_canonical_sha256(value: Any) -> bool:
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def decompress_brotli_exact(content: bytes, expected_length: int) -> bytes:
    """Decode one Brotli stream and reject truncation, trailing data, and expansion."""

    if expected_length <= 0 or expected_length > MAX_PAGE_PAYLOAD_BYTES:
        raise ValueError(
            f"invalid Brotli payload declaration: {expected_length} bytes"
        )
    if not content:
        raise ValueError("Brotli page payload is empty")

    decoder = brotli.Decompressor()
    chunks: list[bytes] = []
    decoded_length = 0
    input_chunk_size = 64 * 1024
    for offset in range(0, len(content), input_chunk_size):
        if decoder.is_finished():
            raise ValueError("Brotli page contains trailing compressed bytes")
        try:
            chunk = decoder.process(content[offset : offset + input_chunk_size])
        except brotli.error as error:
            raise ValueError(f"invalid Brotli page payload: {error}") from error
        decoded_length += len(chunk)
        if decoded_length > expected_length:
            raise ValueError(
                "Brotli page expands beyond payloadByteCount: "
                f"{decoded_length} > {expected_length}"
            )
        chunks.append(chunk)

    if not decoder.is_finished():
        # An empty process call cannot complete a stream that is missing input,
        # but it gives the decoder one final opportunity to flush buffered data.
        try:
            final_chunk = decoder.process(b"")
        except brotli.error as error:
            raise ValueError(f"truncated Brotli page payload: {error}") from error
        decoded_length += len(final_chunk)
        if decoded_length > expected_length:
            raise ValueError(
                "Brotli page expands beyond payloadByteCount: "
                f"{decoded_length} > {expected_length}"
            )
        chunks.append(final_chunk)
    if not decoder.is_finished():
        raise ValueError("Brotli page payload ended before the stream completed")
    if decoded_length != expected_length:
        raise ValueError(
            f"Brotli page decoded to {decoded_length} bytes, "
            f"expected {expected_length}"
        )
    return b"".join(chunks)


def pbgra_violations(frame: np.ndarray) -> dict[str, int]:
    alpha = frame[..., 3]
    color = frame[..., :3]
    return {
        "color_above_alpha_pixels": int(np.any(color > alpha[..., None], axis=2).sum()),
        "alpha0_nonzero_rgb_pixels": int(
            ((alpha == 0) & np.any(color != 0, axis=2)).sum()
        ),
    }


def pbgra_to_rgba(frame: np.ndarray) -> np.ndarray:
    alpha = frame[..., 3].astype(np.uint16)
    premultiplied_rgb = frame[..., [2, 1, 0]].astype(np.uint16)
    rgb = np.zeros_like(premultiplied_rgb, dtype=np.uint16)
    visible = alpha > 0
    if np.any(visible):
        denominator = alpha[visible, None]
        rgb[visible] = np.minimum(
            255,
            (premultiplied_rgb[visible] * 255 + denominator // 2) // denominator,
        )
    return np.concatenate(
        (rgb.astype(np.uint8), alpha[..., None].astype(np.uint8)), axis=2
    )


class AtlasReader:
    def __init__(
        self,
        root: Path,
        manifest_path: Path,
        manifest: dict[str, Any],
        failures: list[dict[str, Any]],
    ) -> None:
        self.root = root
        self.manifest_path = manifest_path
        self.manifest = manifest
        self.failures = failures
        self.pages = manifest.get("pages") if isinstance(manifest, dict) else None
        if not isinstance(self.pages, dict):
            self.pages = {}
            add_failure(failures, "manifest.pages", "manifest pages must be an object")
        self.locations: dict[str, FrameLocation] = {}
        self.page_frame_order: dict[str, list[str]] = {}
        self.page_order = {name: index for index, name in enumerate(self.pages)}
        self._decoded_pages: OrderedDict[str, np.ndarray] = OrderedDict()
        self.page_validation: dict[str, dict[str, Any]] = {}
        self._index_manifest()

    def _index_manifest(self) -> None:
        if (
            self.manifest.get("version") != ATLAS_MANIFEST_VERSION
            or self.manifest.get("compression") != ATLAS_COMPRESSION
        ):
            add_failure(
                self.failures,
                "manifest.compression",
                "runtime atlas manifest must declare v4 Brotli compression",
                version=self.manifest.get("version"),
                compression=self.manifest.get("compression"),
            )
        for page_name, raw_page in self.pages.items():
            if not isinstance(raw_page, dict):
                add_failure(
                    self.failures,
                    "manifest.page_type",
                    "page descriptor must be an object",
                    page=page_name,
                )
                continue
            encoding = raw_page.get("encoding")
            if encoding not in SUPPORTED_PAGE_ENCODINGS:
                add_failure(
                    self.failures,
                    "manifest.page_encoding",
                    "page encoding is not in the v4 runtime whitelist",
                    page=page_name,
                    encoding=encoding,
                    supported=sorted(SUPPORTED_PAGE_ENCODINGS),
                )
            payload_byte_count = raw_page.get("payloadByteCount")
            if (
                not is_manifest_integer(payload_byte_count)
                or payload_byte_count <= 0
                or payload_byte_count > MAX_PAGE_PAYLOAD_BYTES
            ):
                add_failure(
                    self.failures,
                    "manifest.page_payload_size",
                    "payloadByteCount must be an integer in the 1..32 MiB range",
                    page=page_name,
                    payloadByteCount=payload_byte_count,
                    maximum=MAX_PAGE_PAYLOAD_BYTES,
                )
            decoded_sha256 = raw_page.get("decodedSha256")
            if not is_canonical_sha256(decoded_sha256):
                add_failure(
                    self.failures,
                    "manifest.page_decoded_sha256",
                    "decodedSha256 must be a canonical lowercase SHA-256",
                    page=page_name,
                    decodedSha256=decoded_sha256,
                )
            raw_frames = raw_page.get("frames")
            if not isinstance(raw_frames, dict):
                add_failure(
                    self.failures,
                    "manifest.frames",
                    "page frames must be an object",
                    page=page_name,
                )
                continue
            ordered_paths: list[str] = []
            for raw_path, raw_descriptor in raw_frames.items():
                path = normalized_resource_path(str(raw_path))
                ordered_paths.append(path)
                if path in self.locations:
                    add_failure(
                        self.failures,
                        "manifest.cross_page_duplicate",
                        "logical resource appears on more than one page",
                        resource=path,
                        first_page=self.locations[path].page_name,
                        second_page=page_name,
                    )
                    continue
                if not isinstance(raw_descriptor, dict):
                    add_failure(
                        self.failures,
                        "manifest.frame_descriptor",
                        "frame descriptor must be an object",
                        page=page_name,
                        resource=path,
                    )
                    continue
                descriptor: dict[str, int] = {}
                valid = True
                for key in FRAME_DESCRIPTOR_KEYS:
                    value = raw_descriptor.get(key)
                    if not is_manifest_integer(value):
                        valid = False
                        add_failure(
                            self.failures,
                            "manifest.frame_integer",
                            f"frame {key} must be an integer",
                            page=page_name,
                            resource=path,
                            field=key,
                            value=value,
                        )
                    else:
                        descriptor[key] = value
                if valid:
                    self.locations[path] = FrameLocation(page_name, descriptor)
            self.page_frame_order[page_name] = ordered_paths
            declared = raw_page.get("logicalFrameCount")
            if declared != len(raw_frames):
                add_failure(
                    self.failures,
                    "manifest.page_frame_count",
                    "logicalFrameCount does not equal frames size",
                    page=page_name,
                    declared=declared,
                    actual=len(raw_frames),
                )

        source_count = self.manifest.get("sourceFrameCount")
        page_count = self.manifest.get("pageFrameCount")
        if source_count != len(self.locations):
            add_failure(
                self.failures,
                "manifest.source_count",
                "sourceFrameCount does not equal unique logical resources",
                declared=source_count,
                actual=len(self.locations),
            )
        if page_count != sum(len(paths) for paths in self.page_frame_order.values()):
            add_failure(
                self.failures,
                "manifest.page_count",
                "pageFrameCount does not equal page-local frame entries",
                declared=page_count,
                actual=sum(len(paths) for paths in self.page_frame_order.values()),
            )

    @staticmethod
    def _required_page_integer(
        raw_page: dict[str, Any],
        page_name: str,
        field: str,
        *,
        minimum: int | None = None,
        maximum: int | None = None,
    ) -> int:
        value = raw_page.get(field)
        if not is_manifest_integer(value):
            raise ValueError(f"page {page_name} {field} must be an integer")
        if minimum is not None and value < minimum:
            raise ValueError(
                f"page {page_name} {field}={value} is below {minimum}"
            )
        if maximum is not None and value > maximum:
            raise ValueError(
                f"page {page_name} {field}={value} exceeds {maximum}"
            )
        return value

    def _ordered_page_descriptors(
        self,
        page_name: str,
        raw_page: dict[str, Any],
        atlas_width: int,
        atlas_height: int,
    ) -> list[tuple[str, dict[str, int]]]:
        raw_frames = raw_page.get("frames")
        if not isinstance(raw_frames, dict) or not raw_frames:
            raise ValueError(f"page {page_name} frames must be a non-empty object")
        logical_frame_count = raw_page.get("logicalFrameCount")
        if (
            not is_manifest_integer(logical_frame_count)
            or logical_frame_count != len(raw_frames)
        ):
            raise ValueError(
                f"page {page_name} logicalFrameCount does not match frames order"
            )

        ordered: list[tuple[str, dict[str, int]]] = []
        seen_paths: set[str] = set()
        for frame_index, (raw_path, raw_descriptor) in enumerate(raw_frames.items()):
            resource_path = normalized_resource_path(str(raw_path))
            if resource_path in seen_paths:
                raise ValueError(
                    f"page {page_name} repeats normalized resource {resource_path}"
                )
            seen_paths.add(resource_path)
            if not isinstance(raw_descriptor, dict):
                raise ValueError(
                    f"page {page_name} frame {frame_index} descriptor is not an object"
                )
            descriptor: dict[str, int] = {}
            for key in FRAME_DESCRIPTOR_KEYS:
                value = raw_descriptor.get(key)
                if not is_manifest_integer(value):
                    raise ValueError(
                        f"page {page_name} frame {frame_index} {key} "
                        "must be an integer"
                    )
                descriptor[key] = value

            x = descriptor["x"]
            y = descriptor["y"]
            width = descriptor["width"]
            height = descriptor["height"]
            destination_x = descriptor["destinationX"]
            destination_y = descriptor["destinationY"]
            if (
                width <= 0
                or height <= 0
                or x < 0
                or y < 0
                or x + width > atlas_width
                or y + height > atlas_height
                or destination_x >= DISPLAY_WIDTH
                or destination_y >= DISPLAY_HEIGHT
                or destination_x + width <= 0
                or destination_y + height <= 0
            ):
                raise ValueError(
                    f"page {page_name} frame {frame_index} descriptor is out of bounds"
                )
            ordered.append((resource_path, descriptor))
        return ordered

    @staticmethod
    def _regions_intersect(
        first: tuple[int, int, int, int],
        second: tuple[int, int, int, int],
    ) -> bool:
        first_x, first_y, first_width, first_height = first
        second_x, second_y, second_width, second_height = second
        return (
            first_x < second_x + second_width
            and second_x < first_x + first_width
            and first_y < second_y + second_height
            and second_y < first_y + first_height
        )

    def _reconstruct_delta_sub_atlas(
        self,
        page_name: str,
        payload: bytes,
        ordered_frames: list[tuple[str, dict[str, int]]],
        atlas_width: int,
        atlas_height: int,
    ) -> bytes:
        minimum_header_bytes = len(ordered_frames) * DELTA_SUB_HEADER.size
        if len(payload) < minimum_header_bytes:
            raise ValueError(
                f"delta-sub page {page_name} has {len(payload)} bytes, "
                f"below its {minimum_header_bytes}-byte header minimum"
            )

        previous_display_frame = np.zeros(
            (DISPLAY_HEIGHT, DISPLAY_WIDTH, 4), dtype=np.uint8
        )
        atlas = np.zeros((atlas_height, atlas_width, 4), dtype=np.uint8)
        written_regions: set[tuple[int, int, int, int]] = set()
        validated_regions: list[tuple[int, int, int, int]] = []
        offset = 0
        for frame_index, (resource_path, descriptor) in enumerate(ordered_frames):
            if len(payload) - offset < DELTA_SUB_HEADER.size:
                raise ValueError(
                    f"delta-sub page {page_name} frame {frame_index} header is truncated"
                )
            delta_x, delta_y, delta_width, delta_height = DELTA_SUB_HEADER.unpack_from(
                payload, offset
            )
            offset += DELTA_SUB_HEADER.size
            header = (delta_x, delta_y, delta_width, delta_height)
            empty_delta = header == (0, 0, 0, 0)
            if not empty_delta and (
                delta_width == 0
                or delta_height == 0
                or delta_x + delta_width > DISPLAY_WIDTH
                or delta_y + delta_height > DISPLAY_HEIGHT
            ):
                raise ValueError(
                    f"delta-sub page {page_name} frame {frame_index} "
                    f"has invalid rectangle {header}"
                )

            if not empty_delta:
                block_byte_count = delta_width * delta_height * 4
                if block_byte_count > len(payload) - offset:
                    raise ValueError(
                        f"delta-sub page {page_name} frame {frame_index} "
                        "ends inside its byte-sub block"
                    )
                delta = np.frombuffer(
                    payload,
                    dtype=np.uint8,
                    count=block_byte_count,
                    offset=offset,
                ).reshape((delta_height, delta_width, 4))
                destination = previous_display_frame[
                    delta_y : delta_y + delta_height,
                    delta_x : delta_x + delta_width,
                ]
                np.add(destination, delta, out=destination)
                offset += block_byte_count

            atlas_x = descriptor["x"]
            atlas_y = descriptor["y"]
            sprite_width = descriptor["width"]
            sprite_height = descriptor["height"]
            destination_x = descriptor["destinationX"]
            destination_y = descriptor["destinationY"]
            crop = np.zeros((sprite_height, sprite_width, 4), dtype=np.uint8)
            display_x0 = max(0, destination_x)
            display_y0 = max(0, destination_y)
            display_x1 = min(DISPLAY_WIDTH, destination_x + sprite_width)
            display_y1 = min(DISPLAY_HEIGHT, destination_y + sprite_height)
            if display_x1 > display_x0 and display_y1 > display_y0:
                crop_x = display_x0 - destination_x
                crop_y = display_y0 - destination_y
                crop[
                    crop_y : crop_y + display_y1 - display_y0,
                    crop_x : crop_x + display_x1 - display_x0,
                ] = previous_display_frame[
                    display_y0:display_y1,
                    display_x0:display_x1,
                ]

            region = (atlas_x, atlas_y, sprite_width, sprite_height)
            atlas_region = atlas[
                atlas_y : atlas_y + sprite_height,
                atlas_x : atlas_x + sprite_width,
            ]
            if region in written_regions:
                if not np.array_equal(atlas_region, crop):
                    raise ValueError(
                        f"delta-sub page {page_name} repeated region differs "
                        f"at frame {frame_index} ({resource_path})"
                    )
            else:
                if any(
                    self._regions_intersect(region, existing)
                    for existing in validated_regions
                ):
                    raise ValueError(
                        f"delta-sub page {page_name} atlas regions overlap "
                        f"at frame {frame_index} ({resource_path})"
                    )
                written_regions.add(region)
                validated_regions.append(region)
                atlas_region[:] = crop

        if offset != len(payload):
            raise ValueError(
                f"delta-sub page {page_name} has {len(payload) - offset} trailing bytes"
            )
        return atlas.tobytes()

    def decode_page(self, page_name: str) -> np.ndarray:
        cached = self._decoded_pages.pop(page_name, None)
        if cached is not None:
            self._decoded_pages[page_name] = cached
            return cached

        raw_page = self.pages[page_name]
        if not isinstance(raw_page, dict):
            raise ValueError(f"page {page_name} descriptor is not an object")
        width = self._required_page_integer(raw_page, page_name, "width", minimum=1)
        height = self._required_page_integer(raw_page, page_name, "height", minimum=1)
        expected_bytes = width * height * 4
        declared_bytes = self._required_page_integer(
            raw_page,
            page_name,
            "uncompressedByteCount",
            minimum=1,
            maximum=MAX_DECODED_PAGE_BYTES,
        )
        if declared_bytes != expected_bytes:
            raise ValueError(
                f"invalid page geometry {page_name}: {width}x{height}, "
                f"declared={declared_bytes}, expected={expected_bytes}"
            )
        encoding = raw_page.get("encoding")
        if encoding not in SUPPORTED_PAGE_ENCODINGS:
            raise ValueError(
                f"page {page_name} has unsupported encoding {encoding!r}"
            )
        payload_byte_count = self._required_page_integer(
            raw_page,
            page_name,
            "payloadByteCount",
            minimum=1,
            maximum=MAX_PAGE_PAYLOAD_BYTES,
        )
        ordered_frames = self._ordered_page_descriptors(
            page_name, raw_page, width, height
        )
        if encoding == DIRECT_PAGE_ENCODING and payload_byte_count != expected_bytes:
            raise ValueError(
                f"direct page {page_name} payloadByteCount={payload_byte_count}, "
                f"expected uncompressedByteCount={expected_bytes}"
            )
        resource = normalized_resource_path(str(raw_page.get("resource", "")))
        resource_path = self.root / Path(resource)
        compressed = resource_path.read_bytes()
        compressed_byte_count = raw_page.get("compressedByteCount")
        if (
            not is_manifest_integer(compressed_byte_count)
            or compressed_byte_count <= 0
            or compressed_byte_count > payload_byte_count
            or compressed_byte_count != len(compressed)
        ):
            add_failure(
                self.failures,
                "page.compressed_size",
                "compressedByteCount is invalid or does not match the resource",
                page=page_name,
                declared=compressed_byte_count,
                actual=len(compressed),
                payloadByteCount=payload_byte_count,
            )
        declared_sha = raw_page.get("contentSha256")
        actual_sha = sha256_bytes(compressed)
        if declared_sha != actual_sha:
            add_failure(
                self.failures,
                "page.sha256",
                "compressed page SHA-256 mismatch",
                page=page_name,
                declared=declared_sha,
                actual=actual_sha,
            )
        if (
            self.manifest.get("version") != ATLAS_MANIFEST_VERSION
            or self.manifest.get("compression") != ATLAS_COMPRESSION
        ):
            raise ValueError("runtime atlas manifest must declare v4 Brotli compression")
        payload = decompress_brotli_exact(compressed, payload_byte_count)
        if encoding == DIRECT_PAGE_ENCODING:
            decoded_atlas = payload
        else:
            decoded_atlas = self._reconstruct_delta_sub_atlas(
                page_name,
                payload,
                ordered_frames,
                width,
                height,
            )
        if len(decoded_atlas) != expected_bytes:
            raise ValueError(
                f"decoded atlas page {page_name} has {len(decoded_atlas)} bytes, "
                f"expected {expected_bytes}"
            )
        declared_decoded_sha = raw_page.get("decodedSha256")
        if not is_canonical_sha256(declared_decoded_sha):
            raise ValueError(
                f"page {page_name} decodedSha256 is not canonical lowercase SHA-256"
            )
        actual_decoded_sha = sha256_bytes(decoded_atlas)
        if declared_decoded_sha != actual_decoded_sha:
            add_failure(
                self.failures,
                "page.decoded_sha256",
                "decoded atlas SHA-256 mismatch",
                page=page_name,
                declared=declared_decoded_sha,
                actual=actual_decoded_sha,
            )
        frame = np.frombuffer(decoded_atlas, dtype=np.uint8).reshape(height, width, 4)
        violations = pbgra_violations(frame)
        self.page_validation[page_name] = {
            "width": width,
            "height": height,
            "encoding": encoding,
            "payload_bytes": payload_byte_count,
            "compressed_bytes": len(compressed),
            "decoded_bytes": len(decoded_atlas),
            "decoded_sha256": actual_decoded_sha,
            **violations,
        }
        if violations["color_above_alpha_pixels"]:
            add_failure(
                self.failures,
                "pbgra.color_above_alpha",
                "Pbgra color channel exceeds alpha",
                page=page_name,
                pixels=violations["color_above_alpha_pixels"],
            )
        if violations["alpha0_nonzero_rgb_pixels"]:
            add_failure(
                self.failures,
                "pbgra.dirty_transparent",
                "fully transparent atlas pixels contain RGB",
                page=page_name,
                pixels=violations["alpha0_nonzero_rgb_pixels"],
            )
        self._decoded_pages[page_name] = frame
        while len(self._decoded_pages) > 2:
            self._decoded_pages.popitem(last=False)
        return frame

    def reconstruct(self, resource_path: str) -> np.ndarray:
        location = self.locations[resource_path]
        page = self.decode_page(location.page_name)
        d = location.descriptor
        x, y, width, height = d["x"], d["y"], d["width"], d["height"]
        if width <= 0 or height <= 0:
            raise ValueError(f"non-positive sprite size for {resource_path}")
        if x < 0 or y < 0 or x + width > page.shape[1] or y + height > page.shape[0]:
            raise ValueError(f"sprite source rectangle is outside page: {resource_path}")

        canvas = np.zeros((DISPLAY_HEIGHT, DISPLAY_WIDTH, 4), dtype=np.uint8)
        destination_x = d["destinationX"]
        destination_y = d["destinationY"]
        target_left = max(0, destination_x)
        target_top = max(0, destination_y)
        target_right = min(DISPLAY_WIDTH, destination_x + width)
        target_bottom = min(DISPLAY_HEIGHT, destination_y + height)
        if target_left < target_right and target_top < target_bottom:
            source_left = x + target_left - destination_x
            source_top = y + target_top - destination_y
            source_right = source_left + target_right - target_left
            source_bottom = source_top + target_bottom - target_top
            canvas[target_top:target_bottom, target_left:target_right] = page[
                source_top:source_bottom, source_left:source_right
            ]
        return canvas


def expected_runtime_resources(root: Path) -> tuple[set[str], dict[str, list[str]]]:
    assets = root / "Assets"
    expected = {"Assets/luban-idle.png"}
    sequences: dict[str, list[str]] = {}
    for name, expression in SEQUENCE_EXPRESSIONS.items():
        matched: list[tuple[int, str]] = []
        broad_prefix = expression.pattern.split("(\\d{3})", 1)[0]
        # Use the strict expression for acceptance; the broad Assets scan also
        # makes malformed numeric names visible instead of silently skipping.
        for path in assets.glob("luban-*.png"):
            resource = f"Assets/{path.name}"
            match = expression.fullmatch(resource)
            if match:
                matched.append((int(match.group(1)), resource))
            elif re.match(broad_prefix, resource):
                raise ValueError(f"malformed dense frame name: {resource}")
        matched.sort(key=lambda item: item[0])
        sequences[name] = [resource for _, resource in matched]
        expected.update(sequences[name])
    return expected, sequences


def maximum_cyclic_identical_run(frame_hashes: list[bytes]) -> int:
    if not frame_hashes:
        return 0
    if len(set(frame_hashes)) == 1:
        return len(frame_hashes)
    doubled = frame_hashes + frame_hashes
    maximum = 1
    current = 1
    for index in range(1, len(doubled)):
        if doubled[index] == doubled[index - 1]:
            current += 1
            maximum = max(maximum, current)
        else:
            current = 1
        if index >= len(frame_hashes) and current == 1:
            break
    return min(maximum, len(frame_hashes))


def validate_resource_contract(
    reader: AtlasReader,
    disk_sequences: dict[str, list[str]],
    expected_resources: set[str],
    failures: list[dict[str, Any]],
) -> dict[str, list[str]]:
    actual_resources = set(reader.locations)
    missing = sorted(expected_resources - actual_resources)
    extra = sorted(actual_resources - expected_resources)
    if missing:
        add_failure(
            failures,
            "resources.missing",
            "runtime manifest does not cover every expected dense resource",
            count=len(missing),
            sample=missing[:20],
        )
    if extra:
        add_failure(
            failures,
            "resources.extra",
            "runtime manifest contains stale/non-contract resources",
            count=len(extra),
            sample=extra[:20],
        )
    manifest_sequences: dict[str, list[str]] = {}
    for name, expression in SEQUENCE_EXPRESSIONS.items():
        numbered: list[tuple[int, str]] = []
        for resource in actual_resources:
            match = expression.fullmatch(resource)
            if match:
                numbered.append((int(match.group(1)), resource))
        numbered.sort(key=lambda item: item[0])
        numbers = [number for number, _ in numbered]
        resources = [resource for _, resource in numbered]
        manifest_sequences[name] = resources
        if not numbers:
            if name not in OPTIONAL_SEQUENCE_NAMES:
                add_failure(
                    failures,
                    "sequence.missing",
                    "required dense sequence is absent from manifest",
                    sequence=name,
                )
            continue
        if numbers != list(range(1, len(numbers) + 1)):
            add_failure(
                failures,
                "sequence.numbering",
                "dense frame numbers must be contiguous from 001",
                sequence=name,
                numbers=numbers,
            )
        if resources != disk_sequences[name]:
            add_failure(
                failures,
                "sequence.disk_manifest_mismatch",
                "manifest sequence does not exactly match dense assets on disk",
                sequence=name,
                manifest_count=len(resources),
                disk_count=len(disk_sequences[name]),
            )
        if (
            name.endswith(".loop") and
            not name.startswith("work.") and
            len(resources) != 48
        ):
            add_failure(
                failures,
                "sequence.loop_count",
                "loop sequence must contain exactly 48 frames",
                sequence=name,
                actual=len(resources),
            )
        if name.startswith("edge.") and len(resources) != EDGE_PEEK_FRAME_COUNT:
            add_failure(
                failures,
                "sequence.edge_count",
                "edge-peek master sequence must contain exactly "
                f"{EDGE_PEEK_FRAME_COUNT} frames",
                sequence=name,
                actual=len(resources),
            )
        if name.startswith("roam."):
            unique_count = len(
                {
                    hashlib.sha256(reader.reconstruct(resource).tobytes()).digest()
                    for resource in resources
                }
            )
            if len(resources) < MIN_ROAM_FRAME_COUNT:
                add_failure(
                    failures,
                    "sequence.roam_count",
                    "roaming sequence must contain enough frames for smooth playback",
                    sequence=name,
                    minimum=MIN_ROAM_FRAME_COUNT,
                    actual=len(resources),
                )
            if unique_count != len(resources):
                add_failure(
                    failures,
                    "sequence.roam_unique",
                    "every roaming runtime frame must be pixel-unique",
                    sequence=name,
                    expected=len(resources),
                    actual=unique_count,
                )
        if name.startswith("reminder."):
            phase = name.removeprefix("reminder.")
            expected_count = REMINDER_PHASE_FRAME_COUNTS[phase]
            if len(resources) != expected_count:
                add_failure(
                    failures,
                    "sequence.reminder_count",
                    "reminder sequence frame count must match its runtime contract",
                    sequence=name,
                    expected=expected_count,
                    actual=len(resources),
                )
        if name.startswith("work."):
            phase = name.removeprefix("work.")
            expected_count = WORK_PHASE_FRAME_COUNTS[phase]
            if len(resources) != expected_count:
                add_failure(
                    failures,
                    "sequence.work_count",
                    "work sequence frame count must match its runtime contract",
                    sequence=name,
                    expected=expected_count,
                    actual=len(resources),
                )
            frame_hashes = [
                hashlib.sha256(reader.reconstruct(resource).tobytes()).digest()
                for resource in resources
            ]
            unique_count = len(set(frame_hashes))
            if phase in WORK_TYPING_LOOP_PHASES:
                # Count validation above owns malformed/legacy lengths. Only
                # index into declared seam positions once the full v5 cycle is
                # present, so diagnostics fail closed instead of crashing.
                if len(frame_hashes) == WORK_TYPING_LOOP_PERIOD_FRAMES:
                    if unique_count < WORK_TYPING_LOOP_MIN_UNIQUE_POSES:
                        add_failure(
                            failures,
                            "sequence.work_cycle_unique",
                            "work typing cycle must preserve enough distinct articulated poses",
                            sequence=name,
                            period=WORK_TYPING_LOOP_PERIOD_FRAMES,
                            minimum=WORK_TYPING_LOOP_MIN_UNIQUE_POSES,
                            actual=unique_count,
                        )
                    neutral_hash = frame_hashes[0]
                    mismatched_seams = [
                        index
                        for index in WORK_TYPING_NEUTRAL_SEAM_INDICES
                        if frame_hashes[index] != neutral_hash
                    ]
                    if mismatched_seams:
                        add_failure(
                            failures,
                            "sequence.work_neutral_seams",
                            "declared runtime pause seams must be byte-exact neutral frames",
                            sequence=name,
                            indices_0_based=mismatched_seams,
                        )
                    maximum_run = maximum_cyclic_identical_run(frame_hashes)
                    if maximum_run > WORK_TYPING_MAX_IDENTICAL_RUN_FRAMES:
                        add_failure(
                            failures,
                            "sequence.work_still_run",
                            "typing loop contains an excessive identical-frame pause",
                            sequence=name,
                            maximum=WORK_TYPING_MAX_IDENTICAL_RUN_FRAMES,
                            actual=maximum_run,
                        )
            else:
                minimum_unique = WORK_TRANSITION_MIN_UNIQUE_POSES[phase]
                if unique_count < minimum_unique:
                    add_failure(
                        failures,
                        "sequence.work_unique",
                        "work transition must preserve enough authored poses",
                        sequence=name,
                        minimum=minimum_unique,
                        actual=unique_count,
                    )

        page_ranks = [reader.page_order[reader.locations[path].page_name] for path in resources]
        if page_ranks != sorted(page_ranks):
            add_failure(
                failures,
                "sequence.page_order",
                "sequence goes backwards across manifest pages",
                sequence=name,
                page_ranks=page_ranks,
            )
        for page_name in dict.fromkeys(
            reader.locations[path].page_name for path in resources
        ):
            page_numbers = []
            for resource in reader.page_frame_order[page_name]:
                match = expression.fullmatch(resource)
                if match:
                    page_numbers.append(int(match.group(1)))
            if page_numbers != sorted(page_numbers):
                add_failure(
                    failures,
                    "sequence.in_page_order",
                    "sequence frame order is not increasing inside page",
                    sequence=name,
                    page=page_name,
                    numbers=page_numbers,
                )

    work_loop = manifest_sequences.get("work.loop", [])
    work_serious_loop = manifest_sequences.get("work.serious-loop", [])
    work_enter = manifest_sequences.get("work.enter", [])
    work_tap = manifest_sequences.get("work.tap", [])
    work_serious_exit = manifest_sequences.get("work.serious-exit", [])
    if all(
        (work_loop, work_serious_loop, work_enter, work_tap, work_serious_exit)
    ):
        normal_neutral = reader.reconstruct(work_loop[0])
        serious_neutral = reader.reconstruct(work_serious_loop[0])
        work_boundaries = (
            ("enter_to_normal", reader.reconstruct(work_enter[-1]), normal_neutral),
            ("tap_from_normal", reader.reconstruct(work_tap[0]), normal_neutral),
            ("tap_to_normal", reader.reconstruct(work_tap[-1]), normal_neutral),
            (
                "serious_exit_from_serious",
                reader.reconstruct(work_serious_exit[0]),
                serious_neutral,
            ),
            (
                "serious_exit_to_normal",
                reader.reconstruct(work_serious_exit[-1]),
                normal_neutral,
            ),
        )
        for boundary, first, second in work_boundaries:
            if not np.array_equal(first, second):
                add_failure(
                    failures,
                    "sequence.work_boundary",
                    "work-mode transition boundary must be byte-exact",
                    boundary=boundary,
                    rgba_equal=False,
                    alpha_equal=bool(
                        np.array_equal(first[..., 3], second[..., 3])
                    ),
                )

    boarding = manifest_sequences.get("roam.boarding", [])
    flight = manifest_sequences.get("roam.flight", [])
    idle_resource = "Assets/luban-idle.png"
    if boarding and flight and idle_resource in reader.locations:
        idle_frame = reader.reconstruct(idle_resource)
        boarding_first = reader.reconstruct(boarding[0])
        boarding_last = reader.reconstruct(boarding[-1])
        flight_first = reader.reconstruct(flight[0])
        for boundary, first, second in (
            ("idle_to_boarding", idle_frame, boarding_first),
            ("boarding_to_flight", boarding_last, flight_first),
        ):
            if not np.array_equal(first, second):
                add_failure(
                    failures,
                    "sequence.roam_boundary",
                    "roaming entry boundary must be byte-exact",
                    boundary=boundary,
                    rgba_equal=False,
                    alpha_equal=bool(
                        np.array_equal(first[..., 3], second[..., 3])
                    ),
                )
    return manifest_sequences


def resize_pbgra(frame: np.ndarray, width: int, height: int) -> np.ndarray:
    if frame.shape[1] == width and frame.shape[0] == height:
        return frame.copy()
    resized = cv2.resize(frame, (width, height), interpolation=cv2.INTER_AREA)
    # Area filtering is channel-linear, but canonicalise transparent output and
    # guard the Pbgra invariant against integer rounding on unusual OpenCV builds.
    alpha = resized[..., 3]
    resized[..., :3] = np.minimum(resized[..., :3], alpha[..., None])
    resized[alpha == 0, :3] = 0
    return resized


def alpha_iou(first: np.ndarray, second: np.ndarray) -> float:
    first_mask = first[..., 3] >= 24
    second_mask = second[..., 3] >= 24
    union = np.logical_or(first_mask, second_mask).sum()
    return float(np.logical_and(first_mask, second_mask).sum() / union) if union else 1.0


def component_box(mask: np.ndarray, minimum_count: int, minimum_width: int) -> tuple[int, int, int, int] | None:
    count, _, stats, _ = cv2.connectedComponentsWithStats(mask.astype(np.uint8), 8)
    candidates: list[tuple[int, int, tuple[int, int, int, int]]] = []
    for label in range(1, count):
        x, y, width, height, area = [int(value) for value in stats[label]]
        if area >= minimum_count and width >= minimum_width:
            candidates.append((area * width, area, (x, y, x + width, y + height)))
    return max(candidates, default=(0, 0, None), key=lambda value: (value[0], value[1]))[2]


def frame_geometry(frame: np.ndarray, surface: Surface) -> dict[str, Any]:
    alpha = frame[..., 3]
    mask = alpha >= 24
    ys, xs = np.nonzero(mask)
    if not len(xs):
        raise ValueError("logical frame has no alpha>=24 pixels")
    left, top, right, bottom = (
        int(xs.min()), int(ys.min()), int(xs.max() + 1), int(ys.max() + 1)
    )
    weights = alpha.astype(np.float64)
    weight_sum = float(weights.sum())
    grid_y, grid_x = np.indices(alpha.shape)
    centroid = np.asarray(
        [
            float((grid_x * weights).sum() / weight_sum),
            float((grid_y * weights).sum() / weight_sum),
        ]
    )

    rgba = pbgra_to_rgba(frame)
    red = rgba[..., 0].astype(np.int16)
    green = rgba[..., 1].astype(np.int16)
    blue = rgba[..., 2].astype(np.int16)
    upper_limit = top + round((bottom - top) * 0.62)
    blue_hat = (
        mask
        & (grid_y < upper_limit)
        & (blue >= 95)
        & (green >= 55)
        & (blue * 100 >= red * 122)
        & (blue * 100 >= green * 108)
    )
    area_scale = (surface.width / DISPLAY_WIDTH) * (surface.height / DISPLAY_HEIGHT)
    brim_box = component_box(
        blue_hat,
        minimum_count=max(4, round(18 * area_scale)),
        minimum_width=max(4, round(12 * surface.width / DISPLAY_WIDTH)),
    )
    if brim_box is None:
        # Reliable colour-independent fallback: the upper alpha silhouette is
        # still the character's hat/head because the pillow is a static layer.
        upper = mask & (grid_y < top + round((bottom - top) * 0.48))
        upper_ys, upper_xs = np.nonzero(upper)
        if not len(upper_xs):
            raise ValueError("cannot locate blue brim or upper-alpha head proxy")
        brim_box = (
            int(upper_xs.min()), int(upper_ys.min()),
            int(upper_xs.max() + 1), int(upper_ys.max() + 1),
        )
        head_proxy_method = "upper_alpha_proxy"
    else:
        head_proxy_method = "blue_brim"
    brim_center = np.asarray(
        [(brim_box[0] + brim_box[2]) / 2.0, (brim_box[1] + brim_box[3]) / 2.0]
    )

    # Whole-silhouette bounds are stable under limb motion and match the dense
    # source QA contract.  A percentile of per-row widths jumps whenever an arm
    # enters or leaves the sampled torso rows, which looks like a scale change
    # even though the rendered character did not resize.
    torso_width = float(right - left)
    torso_height = float(bottom - top)

    core = alpha >= 160
    if np.any(core):
        distance_to_core = cv2.distanceTransform((~core).astype(np.uint8), cv2.DIST_L2, 3)
        # Below 1.5 output pixels the normal two-tap antialias fringe can be
        # misclassified as a detached trail after downsampling.
        distance_limit = max(1.5, 2.0 * surface.width / DISPLAY_WIDTH)
        trail = (
            (alpha >= 8)
            & (alpha < 160)
            & (distance_to_core > distance_limit)
        )
        trail_count = int(trail.sum())
    else:
        trail_count = int(((alpha >= 8) & (alpha < 160)).sum())
    visible_count = int((alpha >= 8).sum())
    return {
        "bbox": [left, top, right, bottom],
        "baseline": bottom,
        "centroid_px": centroid,
        "head_center_px": brim_center,
        "head_width_px": float(brim_box[2] - brim_box[0]),
        "head_proxy_method": head_proxy_method,
        "torso_width_px": torso_width,
        "torso_height_px": torso_height,
        "torso_scale_method": "alpha24_bbox",
        "wide_translucent_trail_pixels": trail_count,
        "wide_translucent_trail_ratio": trail_count / max(visible_count, 1),
        "pixel_sha256": sha256_bytes(frame.tobytes()),
    }


def relative_step(first: float, second: float) -> float:
    return abs(second - first) / max((first + second) / 2.0, 1e-9)


def pair_gate_score(sequence_name: str, pair: dict[str, Any]) -> float:
    if sequence_name == ROAM_BOARDING_SEQUENCE:
        return max(
            pair["centroid_step_dip"] / MAX_BOARDING_CENTROID_STEP_DIP,
            pair["head_center_step_dip"] / MAX_BOARDING_HEAD_CENTER_STEP_DIP,
        )
    return max(
        max(0.0, (MIN_ALPHA_IOU - pair["alpha_iou"]) / 0.03),
        pair["head_center_step_dip"] / MAX_HEAD_CENTER_STEP_DIP,
        pair["head_width_change_ratio"] / MAX_HAT_SCALE_STEP,
        pair["torso_width_change_ratio"] / MAX_TORSO_SCALE_STEP,
        pair["torso_height_change_ratio"] / MAX_TORSO_SCALE_STEP,
        (
            pair["baseline_step_physical_px"]
            / MAX_BASELINE_STEP_PHYSICAL_PX
            if pair["baseline_gate_applicable"]
            else 0.0
        ),
    )


def pair_waived(sequence: str, metric: str, first: int, second: int) -> str | None:
    return EXACT_PAIR_WAIVERS.get((sequence, metric, first, second))


def center_waived(sequence: str, metric: str, center: int) -> str | None:
    return EXACT_CENTER_WAIVERS.get((sequence, metric, center))


def analyze_surface(
    sequence_name: str,
    frames: list[np.ndarray],
    surface: Surface,
    *,
    loop: bool,
) -> tuple[dict[str, Any], list[dict[str, Any]], list[dict[str, Any]]]:
    is_boarding_transition = sequence_name == ROAM_BOARDING_SEQUENCE
    geometry = [frame_geometry(frame, surface) for frame in frames]
    pair_indexes = [(index, index + 1) for index in range(len(frames) - 1)]
    if loop:
        pair_indexes.append((len(frames) - 1, 0))
    pairs: list[dict[str, Any]] = []
    violations: list[dict[str, Any]] = []
    report_only_findings: list[dict[str, Any]] = []
    waivers: list[dict[str, Any]] = []

    edge_contact: dict[str, Any] | None = None
    if sequence_name.startswith("edge."):
        direction = sequence_name.removeprefix("edge.")
        if direction == "left":
            axis = "x"
            side = "minimum"
            target = 0
            positions = [int(record["bbox"][0]) for record in geometry]
        elif direction == "top":
            axis = "y"
            side = "minimum"
            target = 0
            positions = [int(record["bbox"][1]) for record in geometry]
        elif direction == "bottom":
            axis = "y"
            side = "maximum"
            target = surface.height - 1
            positions = [int(record["bbox"][3]) - 1 for record in geometry]
        else:
            raise ValueError(f"unsupported edge direction {direction!r}")

        contact_errors = [abs(position - target) for position in positions]
        contact_steps = [
            abs(positions[second_index] - positions[first_index])
            for first_index, second_index in pair_indexes
        ]
        for frame_number, error in enumerate(contact_errors, start=1):
            if error > MAX_EDGE_CONTACT_ERROR_PX + FLOAT_COMPARISON_EPSILON:
                violations.append(
                    {
                        "metric": "edge_contact_error_px",
                        "frame": frame_number,
                        "value": error,
                        "limit": MAX_EDGE_CONTACT_ERROR_PX,
                        "axis": axis,
                        "side": side,
                        "target": target,
                    }
                )
        for (first_index, second_index), step in zip(
            pair_indexes, contact_steps, strict=True
        ):
            if step > MAX_EDGE_CONTACT_STEP_PX + FLOAT_COMPARISON_EPSILON:
                violations.append(
                    {
                        "metric": "edge_contact_step_px",
                        "from": first_index + 1,
                        "to": second_index + 1,
                        "value": step,
                        "limit": MAX_EDGE_CONTACT_STEP_PX,
                        "axis": axis,
                        "side": side,
                    }
                )
        edge_contact = {
            "axis": axis,
            "side": side,
            "target_px": target,
            "positions_px": positions,
            "maximum_absolute_error_px": max(contact_errors, default=0),
            "maximum_adjacent_step_px": max(contact_steps, default=0),
        }

    def violate(metric: str, first_number: int, second_number: int, value: float, limit: float) -> None:
        reason = pair_waived(sequence_name, metric, first_number, second_number)
        record = {
            "metric": metric,
            "from": first_number,
            "to": second_number,
            "value": value,
            "limit": limit,
        }
        if reason:
            waivers.append({**record, "reason": reason})
        else:
            violations.append(record)

    for first_index, second_index in pair_indexes:
        first_number = first_index + 1
        second_number = second_index + 1
        first_geometry = geometry[first_index]
        second_geometry = geometry[second_index]
        iou = alpha_iou(frames[first_index], frames[second_index])
        head_delta = (
            np.asarray(second_geometry["head_center_px"])
            - np.asarray(first_geometry["head_center_px"])
        )
        head_step_dip = math.hypot(
            float(head_delta[0]) * surface.x_to_dip,
            float(head_delta[1]) * surface.y_to_dip,
        )
        head_scale = relative_step(
            float(first_geometry["head_width_px"]),
            float(second_geometry["head_width_px"]),
        )
        torso_width_scale = relative_step(
            float(first_geometry["torso_width_px"]),
            float(second_geometry["torso_width_px"]),
        )
        torso_height_scale = relative_step(
            float(first_geometry["torso_height_px"]),
            float(second_geometry["torso_height_px"]),
        )
        baseline_delta = abs(
            int(second_geometry["baseline"]) - int(first_geometry["baseline"])
        )
        baseline_physical = (
            baseline_delta * surface.y_to_dip * MAX_USER_DPI_SCALE
            if surface.physical_scale is None
            else float(baseline_delta)
        )
        centroid_delta = (
            np.asarray(second_geometry["centroid_px"])
            - np.asarray(first_geometry["centroid_px"])
        )
        centroid_step_dip = math.hypot(
            float(centroid_delta[0]) * surface.x_to_dip,
            float(centroid_delta[1]) * surface.y_to_dip,
        )
        # Native geometry keeps the exact 2 DIP gate.  A thresholded feature can
        # move by one complete output pixel after resampling, so scaled surfaces
        # receive one physical-pixel worth of DIP tolerance (plus comparison
        # epsilon below), rather than the previous half-pixel allowance.
        quantisation_dip = (
            0.0
            if surface.physical_scale is None
            else 1.0 / surface.physical_scale
        )
        head_center_limit = (
            MAX_BOARDING_HEAD_CENTER_STEP_DIP
            if is_boarding_transition
            else MAX_HEAD_CENTER_STEP_DIP
        ) + quantisation_dip
        centroid_step_limit = (
            MAX_BOARDING_CENTROID_STEP_DIP + quantisation_dip
            if is_boarding_transition
            else None
        )
        baseline_limit = MAX_BASELINE_STEP_PHYSICAL_PX + (
            0.0 if surface.physical_scale is None else 1.0
        )
        head_scale_limit = MAX_HAT_SCALE_STEP + (
            0.0
            if surface.physical_scale is None
            else 1.0 / max(
                (float(first_geometry["head_width_px"]) + float(second_geometry["head_width_px"])) / 2.0,
                1.0,
            )
        )
        torso_width_limit = MAX_TORSO_SCALE_STEP + (
            0.0
            if surface.physical_scale is None
            else 1.0 / max(
                (float(first_geometry["torso_width_px"]) + float(second_geometry["torso_width_px"])) / 2.0,
                1.0,
            )
        )
        torso_height_limit = MAX_TORSO_SCALE_STEP + (
            0.0
            if surface.physical_scale is None
            else 1.0 / max(
                (float(first_geometry["torso_height_px"]) + float(second_geometry["torso_height_px"])) / 2.0,
                1.0,
            )
        )
        pair = {
            "from": first_number,
            "to": second_number,
            "wrap": bool(loop and second_index == 0),
            "alpha_iou": iou,
            "head_center_step_dip": head_step_dip,
            "head_center_step_limit_dip": head_center_limit,
            "head_width_change_ratio": head_scale,
            "torso_width_change_ratio": torso_width_scale,
            "torso_height_change_ratio": torso_height_scale,
            "baseline_step_physical_px": baseline_physical,
            # The flying panda mount deliberately bobs its bell and bamboo
            # leaves and has no ground-contact baseline. Steady roam loops keep
            # the normal head/hat/torso gates; boarding uses its dedicated
            # non-loop displacement/trail profile.
            "baseline_gate_applicable": not (
                sequence_name.startswith("edge.") or
                sequence_name.startswith("roam.")
            ),
            "centroid_step_dip": centroid_step_dip,
            "centroid_step_limit_dip": centroid_step_limit,
            "gate_profile": (
                "non_loop_pose_transition"
                if is_boarding_transition
                else "steady_motion"
            ),
        }
        pairs.append(pair)
        if not is_boarding_transition and iou < MIN_ALPHA_IOU:
            violate("alpha_iou", first_number, second_number, iou, MIN_ALPHA_IOU)
        if head_step_dip > head_center_limit + FLOAT_COMPARISON_EPSILON:
            violate(
                "head_center_step_dip", first_number, second_number,
                head_step_dip, head_center_limit,
            )
        if (
            centroid_step_limit is not None
            and centroid_step_dip
            > centroid_step_limit + FLOAT_COMPARISON_EPSILON
        ):
            violate(
                "centroid_step_dip",
                first_number,
                second_number,
                centroid_step_dip,
                centroid_step_limit,
            )
        if not is_boarding_transition and head_scale > head_scale_limit:
            violate("head_width_change_ratio", first_number, second_number, head_scale, head_scale_limit)
        if not is_boarding_transition and torso_width_scale > torso_width_limit:
            violate(
                "torso_width_change_ratio", first_number, second_number,
                torso_width_scale, torso_width_limit,
            )
        if not is_boarding_transition and torso_height_scale > torso_height_limit:
            violate(
                "torso_height_change_ratio", first_number, second_number,
                torso_height_scale, torso_height_limit,
            )
        if (
            not (
                sequence_name.startswith("edge.") or
                sequence_name.startswith("roam.")
            )
            and baseline_physical > baseline_limit + FLOAT_COMPARISON_EPSILON
        ):
            violate(
                "baseline_step_physical_px", first_number, second_number,
                baseline_physical, baseline_limit,
            )

    mean_iou = float(np.mean([pair["alpha_iou"] for pair in pairs])) if pairs else 1.0
    if not is_boarding_transition and mean_iou < MIN_MEAN_ALPHA_IOU:
        violations.append(
            {
                "metric": "mean_alpha_iou",
                "value": mean_iou,
                "limit": MIN_MEAN_ALPHA_IOU,
            }
        )

    duplicate_pairs = [
        pair["from"]
        for pair in pairs
        if geometry[pair["from"] - 1]["pixel_sha256"]
        == geometry[pair["to"] - 1]["pixel_sha256"]
    ]
    allowed_neutral_duplicate_pairs: list[int] = []
    unexpected_duplicate_pairs = duplicate_pairs
    if sequence_name in ("work.loop", "work.serious-loop"):
        neutral_hash = geometry[0]["pixel_sha256"]
        allowed_neutral_duplicate_pairs = [
            frame_number
            for frame_number in duplicate_pairs
            if geometry[frame_number - 1]["pixel_sha256"] == neutral_hash
        ]
        unexpected_duplicate_pairs = [
            frame_number
            for frame_number in duplicate_pairs
            if frame_number not in allowed_neutral_duplicate_pairs
        ]
    if unexpected_duplicate_pairs:
        violations.append(
            {
                "metric": "adjacent_duplicate",
                "from_frames": unexpected_duplicate_pairs,
                "limit": 0,
            }
        )

    centers = range(len(frames)) if loop else range(1, len(frames) - 1)
    center_metrics: list[dict[str, Any]] = []
    for center_index in centers:
        previous_index = (center_index - 1) % len(frames)
        next_index = (center_index + 1) % len(frames)
        previous_centroid = np.asarray(geometry[previous_index]["centroid_px"], dtype=np.float64)
        center_centroid = np.asarray(geometry[center_index]["centroid_px"], dtype=np.float64)
        next_centroid = np.asarray(geometry[next_index]["centroid_px"], dtype=np.float64)
        previous_head = np.asarray(
            geometry[previous_index]["head_center_px"], dtype=np.float64
        )
        center_head = np.asarray(
            geometry[center_index]["head_center_px"], dtype=np.float64
        )
        next_head = np.asarray(
            geometry[next_index]["head_center_px"], dtype=np.float64
        )
        dip_scale = np.asarray([surface.x_to_dip, surface.y_to_dip])
        previous_step = (center_centroid - previous_centroid) * dip_scale
        next_step = (next_centroid - center_centroid) * dip_scale
        jerk = float(np.linalg.norm(next_step - previous_step))
        previous_length = float(np.linalg.norm(previous_step))
        next_length = float(np.linalg.norm(next_step))
        dot = float(np.dot(previous_step, next_step))
        return_distance = float(
            np.linalg.norm((next_centroid - previous_centroid) * dip_scale)
        )
        previous_head_step = (center_head - previous_head) * dip_scale
        next_head_step = (next_head - center_head) * dip_scale
        head_jerk = float(np.linalg.norm(next_head_step - previous_head_step))
        previous_head_length = float(np.linalg.norm(previous_head_step))
        next_head_length = float(np.linalg.norm(next_head_step))
        head_dot = float(np.dot(previous_head_step, next_head_step))
        head_return_distance = float(
            np.linalg.norm((next_head - previous_head) * dip_scale)
        )

        previous_alpha = frames[previous_index][..., 3]
        center_alpha = frames[center_index][..., 3]
        next_alpha = frames[next_index][..., 3]
        center_core = center_alpha >= 160
        if np.any(center_core):
            distance = cv2.distanceTransform(
                (~center_core).astype(np.uint8), cv2.DIST_L2, 3
            )
            distance_limit = max(1.5, 1.75 * surface.width / DISPLAY_WIDTH)
            transient = (
                (center_alpha >= 8)
                & (center_alpha < 160)
                & (previous_alpha < 8)
                & (next_alpha < 8)
                & (distance > distance_limit)
            )
            transient_pixels = int(transient.sum())
        else:
            transient_pixels = 0
        edge_pixels = int(((center_alpha > 0) & (center_alpha < 224)).sum())
        transient_ratio = transient_pixels / max(edge_pixels, 1)
        center_number = center_index + 1
        center_record = {
            "center_frame": center_number,
            "centroid_second_difference_dip": jerk,
            "previous_step_dip": previous_length,
            "next_step_dip": next_length,
            "step_dot_product": dot,
            "two_frame_return_distance_dip": return_distance,
            "head_second_difference_dip": head_jerk,
            "previous_head_step_dip": previous_head_length,
            "next_head_step_dip": next_head_length,
            "head_step_dot_product": head_dot,
            "head_two_frame_return_distance_dip": head_return_distance,
            "transient_edge_pixels": transient_pixels,
            "transient_edge_ratio": transient_ratio,
        }
        center_metrics.append(center_record)
        head_jerk_limit = MAX_HEAD_SECOND_DIFFERENCE_DIP + (
            0.0 if surface.physical_scale is None else 1.0 / surface.physical_scale
        )
        if head_jerk > head_jerk_limit:
            reason = center_waived(
                sequence_name, "head_second_difference_dip", center_number
            )
            record = {
                "metric": "head_second_difference_dip",
                "center_frame": center_number,
                "value": head_jerk,
                "limit": head_jerk_limit,
            }
            report_only_findings.append(
                {
                    **record,
                    "status": "report_only",
                    **({"former_waiver_reason": reason} if reason else {}),
                }
            )
        head_micro_roundtrip = (
            head_dot < 0.0
            and 0.20 <= previous_head_length <= 1.50
            and 0.20 <= next_head_length <= 1.50
            and head_return_distance < 0.35
            and head_jerk > 0.75
        )
        if head_micro_roundtrip:
            reason = center_waived(
                sequence_name, "head_micro_roundtrip", center_number
            )
            record = {
                "metric": "head_micro_roundtrip",
                "center_frame": center_number,
                "value": head_jerk,
                "limit": 0,
            }
            report_only_findings.append(
                {
                    **record,
                    "status": "report_only",
                    **({"former_waiver_reason": reason} if reason else {}),
                }
            )
        transient_limit_pixels = max(4, round(edge_pixels * MAX_TRANSIENT_EDGE_RATIO))
        if transient_pixels > transient_limit_pixels:
            reason = center_waived(sequence_name, "transient_edge_pixels", center_number)
            record = {
                "metric": "transient_edge_pixels",
                "center_frame": center_number,
                "value": transient_pixels,
                "limit": transient_limit_pixels,
                "ratio": transient_ratio,
            }
            report_only_findings.append(
                {
                    **record,
                    "status": "report_only",
                    **({"former_waiver_reason": reason} if reason else {}),
                }
            )

    for frame_number, record in enumerate(geometry, start=1):
        visible_estimate = max(
            1,
            round(
                record["wide_translucent_trail_pixels"]
                / max(record["wide_translucent_trail_ratio"], 1e-12)
            ),
        )
        trail_ratio_limit = (
            MAX_BOARDING_WIDE_TRANSLUCENT_TRAIL_RATIO
            if is_boarding_transition
            else MAX_WIDE_TRANSLUCENT_TRAIL_RATIO
        )
        trail_limit_pixels = max(8, round(visible_estimate * trail_ratio_limit))
        if record["wide_translucent_trail_pixels"] > trail_limit_pixels:
            finding = {
                "metric": "wide_translucent_trail_pixels",
                "frame": frame_number,
                "value": record["wide_translucent_trail_pixels"],
                "limit": trail_limit_pixels,
                "ratio": record["wide_translucent_trail_ratio"],
                "ratio_limit": trail_ratio_limit,
            }
            if is_boarding_transition:
                violations.append(finding)
            else:
                report_only_findings.append(
                    {**finding, "status": "report_only"}
                )

    pair_worst = sorted(
        pairs,
        key=lambda pair: pair_gate_score(sequence_name, pair),
        reverse=True,
    )
    center_worst = sorted(
        center_metrics,
        key=lambda value: max(
            value["head_second_difference_dip"]
            / MAX_HEAD_SECOND_DIFFERENCE_DIP,
            value["transient_edge_ratio"] / MAX_TRANSIENT_EDGE_RATIO,
        ),
        reverse=True,
    )
    return (
        {
            "width": surface.width,
            "height": surface.height,
            "frame_count": len(frames),
            "is_loop": loop,
            "gate_profile": (
                "non_loop_pose_transition"
                if is_boarding_transition
                else "steady_motion"
            ),
            "head_proxy_methods": dict(
                sorted(
                    (method, sum(g["head_proxy_method"] == method for g in geometry))
                    for method in {g["head_proxy_method"] for g in geometry}
                )
            ),
            "minimum_alpha_iou": min((pair["alpha_iou"] for pair in pairs), default=1.0),
            "mean_alpha_iou": mean_iou,
            "maximum_head_center_step_dip": max(
                (pair["head_center_step_dip"] for pair in pairs), default=0.0
            ),
            "maximum_head_width_change_ratio": max(
                (pair["head_width_change_ratio"] for pair in pairs), default=0.0
            ),
            "maximum_torso_width_change_ratio": max(
                (pair["torso_width_change_ratio"] for pair in pairs), default=0.0
            ),
            "maximum_torso_height_change_ratio": max(
                (pair["torso_height_change_ratio"] for pair in pairs), default=0.0
            ),
            "maximum_baseline_step_physical_px": max(
                (pair["baseline_step_physical_px"] for pair in pairs), default=0.0
            ),
            "maximum_centroid_second_difference_dip": max(
                (value["centroid_second_difference_dip"] for value in center_metrics),
                default=0.0,
            ),
            "maximum_head_second_difference_dip": max(
                (value["head_second_difference_dip"] for value in center_metrics),
                default=0.0,
            ),
            "maximum_transient_edge_pixels": max(
                (value["transient_edge_pixels"] for value in center_metrics), default=0
            ),
            "maximum_transient_edge_ratio": max(
                (value["transient_edge_ratio"] for value in center_metrics), default=0.0
            ),
            "maximum_wide_translucent_trail_pixels": max(
                (g["wide_translucent_trail_pixels"] for g in geometry), default=0
            ),
            "maximum_wide_translucent_trail_ratio": max(
                (g["wide_translucent_trail_ratio"] for g in geometry), default=0.0
            ),
            "edge_contact": edge_contact,
            "adjacent_duplicate_pairs_from_1_based": duplicate_pairs,
            "allowed_work_neutral_duplicate_pairs_from_1_based": (
                allowed_neutral_duplicate_pairs
            ),
            "worst_pairs": pair_worst[:12],
            "worst_centers": center_worst[:12],
            "report_only_finding_count": len(report_only_findings),
            "report_only_findings": report_only_findings,
            "violation_count": len(violations),
            "violations": violations[:80],
        },
        violations,
        waivers,
    )


def surface_matrix() -> list[Surface]:
    surfaces = [
        Surface(
            "native-399x509",
            DISPLAY_WIDTH,
            DISPLAY_HEIGHT,
            PET_WIDTH_DIP / DISPLAY_WIDTH,
            PET_HEIGHT_DIP / DISPLAY_HEIGHT,
            None,
        )
    ]
    for user_scale in USER_SCALES:
        for dpi_scale in DPI_SCALES:
            physical_scale = user_scale * dpi_scale
            width = max(1, round(PET_WIDTH_DIP * physical_scale))
            height = max(1, round(PET_HEIGHT_DIP * physical_scale))
            surfaces.append(
                Surface(
                    f"scale-{user_scale:g}_dpi-{dpi_scale:g}",
                    width,
                    height,
                    1.0 / physical_scale,
                    1.0 / physical_scale,
                    physical_scale,
                )
            )
    return surfaces


def analyze_sequence(
    name: str,
    resources: list[str],
    reader: AtlasReader,
    failures: list[dict[str, Any]],
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    native_frames = [reader.reconstruct(resource) for resource in resources]
    surface_results: dict[str, Any] = {}
    contact_candidates: list[dict[str, Any]] = []
    applied_waivers: list[dict[str, Any]] = []
    is_loop = (
        name.endswith(".loop") or
        name.endswith("-loop") or
        name.startswith("edge.") or
        name.removeprefix("roam.") in ROAM_LOOP_SEQUENCES or
        name == "reminder.hold"
    )
    for surface in surface_matrix():
        frames = (
            native_frames
            if surface.width == DISPLAY_WIDTH and surface.height == DISPLAY_HEIGHT
            else [resize_pbgra(frame, surface.width, surface.height) for frame in native_frames]
        )
        result, violations, waivers = analyze_surface(
            name, frames, surface, loop=is_loop
        )
        surface_results[surface.name] = result
        applied_waivers.extend(
            {"surface": surface.name, **waiver} for waiver in waivers
        )
        for violation in violations:
            add_failure(
                failures,
                "motion.gate",
                "final atlas motion quality gate failed",
                sequence=name,
                surface=surface.name,
                **violation,
            )
        if surface.name == "native-399x509":
            for pair in result["worst_pairs"]:
                score = pair_gate_score(name, pair)
                contact_candidates.append(
                    {
                        "sequence": name,
                        "kind": "pair",
                        "from": pair["from"],
                        "to": pair["to"],
                        "score": score,
                    }
                )
            for center in result["worst_centers"]:
                score = max(
                    center["head_second_difference_dip"]
                    / MAX_HEAD_SECOND_DIFFERENCE_DIP,
                    center["transient_edge_ratio"] / MAX_TRANSIENT_EDGE_RATIO,
                )
                contact_candidates.append(
                    {
                        "sequence": name,
                        "kind": "center",
                        "center": center["center_frame"],
                        "score": score,
                    }
                )
    return (
        {
            "resources": resources,
            "surfaces": surface_results,
            "applied_exact_waivers": applied_waivers,
        },
        contact_candidates,
    )


def checker_tile(frame: np.ndarray, label: str, width: int = 112) -> Image.Image:
    rgba = pbgra_to_rgba(frame)
    yy, xx = np.indices(rgba.shape[:2])
    checker = np.empty((*rgba.shape[:2], 3), dtype=np.uint8)
    light = ((xx // 20 + yy // 20) % 2) == 0
    checker[light] = (242, 242, 242)
    checker[~light] = (36, 38, 42)
    alpha = rgba[..., 3:4].astype(np.float32) / 255.0
    shown = np.rint(rgba[..., :3] * alpha + checker * (1.0 - alpha)).astype(np.uint8)
    height = round(width * DISPLAY_HEIGHT / DISPLAY_WIDTH)
    rendered = Image.fromarray(shown, "RGB").resize((width, height), Image.Resampling.LANCZOS)
    tile = Image.new("RGB", (width, height + 20), (20, 20, 22))
    tile.paste(rendered, (0, 20))
    ImageDraw.Draw(tile).text((4, 3), label, fill="white", font=ImageFont.load_default())
    return tile


def difference_tile(first: np.ndarray, second: np.ndarray, width: int = 112) -> Image.Image:
    delta = np.abs(first[..., 3].astype(np.int16) - second[..., 3].astype(np.int16)).astype(np.uint8)
    heat = np.zeros((DISPLAY_HEIGHT, DISPLAY_WIDTH, 3), dtype=np.uint8)
    heat[..., 0] = delta
    heat[..., 1] = delta // 5
    height = round(width * DISPLAY_HEIGHT / DISPLAY_WIDTH)
    rendered = Image.fromarray(heat, "RGB").resize((width, height), Image.Resampling.LANCZOS)
    tile = Image.new("RGB", (width, height + 20), (20, 20, 22))
    tile.paste(rendered, (0, 20))
    ImageDraw.Draw(tile).text((4, 3), "alpha delta", fill="white", font=ImageFont.load_default())
    return tile


def write_worst_contacts(
    candidates: list[dict[str, Any]],
    sequences: dict[str, list[str]],
    reader: AtlasReader,
    destination: Path,
    limit: int,
) -> list[dict[str, Any]]:
    selected: list[dict[str, Any]] = []
    occupied: set[tuple[str, int]] = set()
    for candidate in sorted(candidates, key=lambda value: float(value["score"]), reverse=True):
        center = int(candidate.get("center", candidate.get("from", 1)))
        identity = (str(candidate["sequence"]), center)
        if identity in occupied:
            continue
        occupied.add(identity)
        selected.append(candidate)
        if len(selected) >= limit:
            break
    if not selected:
        return []

    label_width = 250
    rows: list[Image.Image] = []
    for candidate in selected:
        name = str(candidate["sequence"])
        resources = sequences[name]
        is_loop = (
            name.endswith(".loop") or
            name.endswith("-loop") or
            name.startswith("edge.") or
            name.removeprefix("roam.") in ROAM_LOOP_SEQUENCES or
            name == "reminder.hold"
        )
        center = int(candidate.get("center", candidate.get("from", 1)))
        indexes: list[int] = []
        for offset in (-2, -1, 0, 1, 2):
            index = center - 1 + offset
            if is_loop:
                index %= len(resources)
            elif index < 0 or index >= len(resources):
                continue
            indexes.append(index)
        frames = [reader.reconstruct(resources[index]) for index in indexes]
        tiles = [
            checker_tile(frame, f"{index + 1:03d}")
            for index, frame in zip(indexes, frames)
        ]
        if candidate["kind"] == "pair":
            first_index = int(candidate["from"]) - 1
            second_index = int(candidate["to"]) - 1
        else:
            first_index = (center - 2) % len(resources)
            second_index = center % len(resources)
        tiles.append(
            difference_tile(
                reader.reconstruct(resources[first_index]),
                reader.reconstruct(resources[second_index]),
            )
        )
        row_height = max(tile.height for tile in tiles)
        row = Image.new(
            "RGB", (label_width + sum(tile.width for tile in tiles), row_height), (20, 20, 22)
        )
        label = (
            f"{name}\n{candidate['kind']}\n"
            f"score={float(candidate['score']):.2f}"
        )
        ImageDraw.Draw(row).multiline_text(
            (8, 8), label, fill="white", font=ImageFont.load_default(), spacing=5
        )
        x = label_width
        for tile in tiles:
            row.paste(tile, (x, 0))
            x += tile.width
        rows.append(row)
    gap = 4
    board = Image.new(
        "RGB",
        (max(row.width for row in rows), sum(row.height for row in rows) + gap * (len(rows) - 1)),
        "white",
    )
    y = 0
    for row in rows:
        board.paste(row, (0, y))
        y += row.height + gap
    destination.parent.mkdir(parents=True, exist_ok=True)
    board.save(destination, optimize=True)
    return selected


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Decode and QA final Pbgra32 desktop-pet sprite atlas motion"
    )
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--contacts", action="store_true", help="write a checkerboard sheet for worst transitions"
    )
    parser.add_argument("--contact-limit", type=int, default=16)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    manifest_path = args.manifest if args.manifest.is_absolute() else ROOT / args.manifest
    output = args.out if args.out.is_absolute() else ROOT / args.out
    output.mkdir(parents=True, exist_ok=True)
    failures: list[dict[str, Any]] = []
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except Exception as error:
        report = {
            "passed": False,
            "manifest": str(manifest_path),
            "failures": [{"code": "manifest.read", "message": str(error)}],
        }
        (output / "metrics.json").write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(json.dumps({"passed": False, "failure_count": 1}, ensure_ascii=False))
        return 1

    if manifest.get("displayWidth") != DISPLAY_WIDTH or manifest.get("displayHeight") != DISPLAY_HEIGHT:
        add_failure(
            failures,
            "manifest.display_size",
            "manifest display surface must be 399x509",
            actual=[manifest.get("displayWidth"), manifest.get("displayHeight")],
        )
    reader = AtlasReader(ROOT, manifest_path, manifest, failures)
    try:
        expected_resources, disk_sequences = expected_runtime_resources(ROOT)
    except Exception as error:
        add_failure(failures, "resources.disk", str(error))
        expected_resources, disk_sequences = set(), {name: [] for name in SEQUENCE_EXPRESSIONS}
    manifest_sequences = validate_resource_contract(
        reader, disk_sequences, expected_resources, failures
    )

    reconstructed: dict[str, dict[str, Any]] = {}
    for resource in sorted(reader.locations):
        try:
            frame = reader.reconstruct(resource)
            violations = pbgra_violations(frame)
            reconstructed[resource] = {
                "pixel_sha256": sha256_bytes(frame.tobytes()),
                "visible_alpha24_pixels": int((frame[..., 3] >= 24).sum()),
                **violations,
            }
            if violations["color_above_alpha_pixels"]:
                add_failure(
                    failures,
                    "frame.color_above_alpha",
                    "reconstructed Pbgra color channel exceeds alpha",
                    resource=resource,
                    pixels=violations["color_above_alpha_pixels"],
                )
            if violations["alpha0_nonzero_rgb_pixels"]:
                add_failure(
                    failures,
                    "frame.dirty_transparent",
                    "reconstructed transparent pixels contain RGB",
                    resource=resource,
                    pixels=violations["alpha0_nonzero_rgb_pixels"],
                )
        except Exception as error:
            add_failure(
                failures,
                "frame.reconstruct",
                str(error),
                resource=resource,
            )

    sequence_results: dict[str, Any] = {}
    candidates: list[dict[str, Any]] = []
    for name in SEQUENCE_EXPRESSIONS:
        resources = manifest_sequences.get(name, [])
        if len(resources) < 2 or any(resource not in reconstructed for resource in resources):
            continue
        if name in MOTION_ANALYSIS_EXCLUDED_SEQUENCES:
            continue
        try:
            sequence_result, sequence_candidates = analyze_sequence(
                name, resources, reader, failures
            )
            sequence_results[name] = sequence_result
            candidates.extend(sequence_candidates)
        except Exception as error:
            add_failure(
                failures,
                "motion.analyze",
                str(error),
                sequence=name,
            )

    runtime_profile_results: dict[str, Any] = {}
    for runtime_name, (source_name, frame_count) in (
        RUNTIME_STABLE_ACTION_PREFIXES.items()
    ):
        resources = manifest_sequences.get(source_name, [])
        if len(resources) < frame_count:
            add_failure(
                failures,
                "runtime_profile.frame_count",
                "runtime stable-action prefix is shorter than declared",
                sequence=runtime_name,
                source_sequence=source_name,
                expected=frame_count,
                actual=len(resources),
            )
            continue
        try:
            runtime_result, _ = analyze_sequence(
                runtime_name,
                resources[:frame_count],
                reader,
                failures,
            )
            runtime_profile_results[runtime_name] = runtime_result
            native_result = runtime_result["surfaces"]["native-399x509"]
            micro_roundtrips = [
                finding
                for finding in native_result["report_only_findings"]
                if (
                    finding["metric"] == "head_micro_roundtrip"
                    and finding["value"]
                    > MAX_RUNTIME_HEAD_MICRO_ROUNDTRIP_DIP
                    + FLOAT_COMPARISON_EPSILON
                )
            ]
            if micro_roundtrips:
                add_failure(
                    failures,
                    "runtime_profile.head_micro_roundtrip",
                    "runtime stable-action prefix contains an internal head-height roundtrip",
                    sequence=runtime_name,
                    findings=micro_roundtrips,
                )
            centroid_jerk = native_result[
                "maximum_centroid_second_difference_dip"
            ]
            if (
                centroid_jerk
                > MAX_RUNTIME_CENTROID_SECOND_DIFFERENCE_DIP
                + FLOAT_COMPARISON_EPSILON
            ):
                add_failure(
                    failures,
                    "runtime_profile.centroid_second_difference",
                    "runtime stable-action prefix contains visible body-height jitter",
                    sequence=runtime_name,
                    value=centroid_jerk,
                    limit=MAX_RUNTIME_CENTROID_SECOND_DIFFERENCE_DIP,
                )
        except Exception as error:
            add_failure(
                failures,
                "runtime_profile.analyze",
                str(error),
                sequence=runtime_name,
                source_sequence=source_name,
            )

    contact_path = output / "worst-transitions.png"
    selected_contacts: list[dict[str, Any]] = []
    if args.contacts:
        try:
            selected_contacts = write_worst_contacts(
                candidates,
                manifest_sequences,
                reader,
                contact_path,
                max(1, args.contact_limit),
            )
        except Exception as error:
            add_failure(failures, "contacts.write", str(error))

    report = {
        "passed": not failures,
        "manifest": str(manifest_path),
        "manifest_sha256": sha256_bytes(manifest_path.read_bytes()),
        "display_surface": [DISPLAY_WIDTH, DISPLAY_HEIGHT],
        "pet_dip_size": [PET_WIDTH_DIP, PET_HEIGHT_DIP],
        "physical_surface_matrix": [
            {
                "name": surface.name,
                "width": surface.width,
                "height": surface.height,
            }
            for surface in surface_matrix()[1:]
        ],
        "page_count": len(reader.pages),
        "logical_resource_count": len(reader.locations),
        "reconstructed_logical_frame_count": len(reconstructed),
        "pages": reader.page_validation,
        "reconstructed_frames": reconstructed,
        "sequences": sequence_results,
        "runtime_profiles": runtime_profile_results,
        "contacts": {
            "written": bool(args.contacts and selected_contacts),
            "path": str(contact_path) if args.contacts else None,
            "selected": selected_contacts,
        },
        "thresholds": {
            "minimum_alpha_iou": MIN_ALPHA_IOU,
            "minimum_mean_alpha_iou": MIN_MEAN_ALPHA_IOU,
            "maximum_head_center_step_dip": MAX_HEAD_CENTER_STEP_DIP,
            "scaled_head_center_quantisation_allowance_output_px": 1.0,
            "maximum_native_hat_scale_step": MAX_HAT_SCALE_STEP,
            "maximum_native_alpha_bbox_scale_step": MAX_TORSO_SCALE_STEP,
            "maximum_baseline_step_physical_px": MAX_BASELINE_STEP_PHYSICAL_PX,
            "scaled_baseline_quantisation_allowance_output_px": 1.0,
            "maximum_edge_contact_error_px": MAX_EDGE_CONTACT_ERROR_PX,
            "maximum_edge_contact_step_px": MAX_EDGE_CONTACT_STEP_PX,
            "diagnostic_reference_head_second_difference_dip": (
                MAX_HEAD_SECOND_DIFFERENCE_DIP
            ),
            "diagnostic_reference_centroid_second_difference_dip": (
                MAX_CENTROID_SECOND_DIFFERENCE_DIP
            ),
            "maximum_runtime_centroid_second_difference_dip": (
                MAX_RUNTIME_CENTROID_SECOND_DIFFERENCE_DIP
            ),
            "maximum_runtime_head_micro_roundtrip_dip": (
                MAX_RUNTIME_HEAD_MICRO_ROUNDTRIP_DIP
            ),
            "diagnostic_reference_transient_edge_ratio": MAX_TRANSIENT_EDGE_RATIO,
            "diagnostic_reference_wide_translucent_trail_ratio": (
                MAX_WIDE_TRANSLUCENT_TRAIL_RATIO
            ),
            "roam_boarding_non_loop_profile": {
                "minimum_alpha_iou": None,
                "minimum_mean_alpha_iou": None,
                "fixed_silhouette_scale_gate": False,
                "maximum_centroid_step_dip": MAX_BOARDING_CENTROID_STEP_DIP,
                "maximum_head_center_step_dip": (
                    MAX_BOARDING_HEAD_CENTER_STEP_DIP
                ),
                "maximum_wide_translucent_trail_ratio": (
                    MAX_BOARDING_WIDE_TRANSLUCENT_TRAIL_RATIO
                ),
                "byte_exact_idle_and_flight_endpoints": True,
                "pixel_unique_frames": True,
                "clean_pbgra_required": True,
            },
        },
        "report_only_metrics": [
            "wide_translucent_trail_pixels",
            "transient_edge_pixels",
            "head_micro_roundtrip",
            "head_second_difference_dip",
        ],
        "strict_profile_overrides": {
            ROAM_BOARDING_SEQUENCE: [
                "centroid_step_dip",
                "head_center_step_dip",
                "wide_translucent_trail_pixels",
            ],
            **{
                name: [
                    "head_micro_roundtrip",
                    "centroid_second_difference_dip",
                ]
                for name in RUNTIME_STABLE_ACTION_PREFIXES
            },
        },
        "exact_pair_waivers": [
            {
                "sequence": key[0], "metric": key[1], "from": key[2], "to": key[3],
                "reason": reason,
            }
            for key, reason in EXACT_PAIR_WAIVERS.items()
        ],
        "exact_center_waivers": [
            {
                "sequence": key[0], "metric": key[1], "center": key[2],
                "reason": reason,
            }
            for key, reason in EXACT_CENTER_WAIVERS.items()
        ],
        "failure_count": len(failures),
        "failures": failures,
    }
    report_path = output / "metrics.json"
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(
        json.dumps(
            {
                "passed": report["passed"],
                "failure_count": len(failures),
                "page_count": len(reader.pages),
                "logical_frame_count": len(reader.locations),
                "report": str(report_path),
                "contacts": str(contact_path) if args.contacts else None,
            },
            ensure_ascii=False,
        ),
        flush=True,
    )
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
