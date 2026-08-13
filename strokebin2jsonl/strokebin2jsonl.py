#!/usr/bin/env python3
"""Convert BehaviorRecognizer .strokebin (STRO v1, little-endian) to JSONL.

Binary layout mirrors behavior_recognizer Storage/Strokebin:
  header: magic "STRO" + u32 version + u64 createdAt + strings + u8 encoding
  events: u8 type + u32 payloadLen + payload
  SamplePoint (69 bytes): u64 ts, u64 dt, f64 x/y/pressure, u8 inContact,
                          u32 buttons, f64 tiltX/tiltY, u64 sequenceId
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional


MAGIC = b"STRO"
SAMPLE_POINT_SIZE = 69

EVENT_SESSION_START = 1
EVENT_STROKE_START = 2
EVENT_STROKE_POINT = 3
EVENT_STROKE_END = 4
EVENT_SESSION_FLUSH = 5
EVENT_SESSION_END = 6

FLUSH_REASON = {
    0: "None",
    1: "PenUpTimeout",
    2: "StrokeCountThreshold",
    3: "SessionStop",
    4: "Manual",
}

WRITE_STATUS = {
    0: "Ok",
    1: "DroppedOldest",
    2: "DroppedNewest",
    3: "QueueFull",
    4: "IoError",
}


class StrokeBinError(Exception):
    pass


def to_iso8601(unix_ms: int) -> str:
    dto = datetime.fromtimestamp(unix_ms / 1000.0, tz=timezone.utc)
    return dto.strftime("%Y-%m-%dT%H:%M:%S.") + f"{unix_ms % 1000:03d}Z"


@dataclass
class SamplePoint:
    timestamp_ms: int = 0
    delta_time_ms: int = 0
    x: float = 0.0
    y: float = 0.0
    pressure: float = 0.0
    in_contact: bool = False
    buttons: int = 0
    tilt_x: float = 0.0
    tilt_y: float = 0.0
    sequence_id: int = 0

    def to_dict(self) -> dict:
        return {
            "timestamp": self.timestamp_ms,
            "timestampIso": to_iso8601(self.timestamp_ms),
            "deltaTime": self.delta_time_ms,
            "x": self.x,
            "y": self.y,
            "pressure": self.pressure,
            "inContact": self.in_contact,
            "buttons": self.buttons,
            "tiltX": self.tilt_x,
            "tiltY": self.tilt_y,
            "sequenceId": self.sequence_id,
        }


@dataclass
class Stroke:
    stroke_id: int = 0
    start_timestamp_ms: int = 0
    end_timestamp_ms: int = 0
    points: list[SamplePoint] = field(default_factory=list)


@dataclass
class StrokeSegment:
    segment_id: int = 0
    reason: int = 0
    start_timestamp_ms: int = 0
    end_timestamp_ms: int = 0
    strokes: list[Stroke] = field(default_factory=list)
    point_count: int = 0
    write_status: int = 0


@dataclass
class RecordingSession:
    session_id: str = ""
    file_path: str = ""
    version: int = 1
    created_at_unix_ms: int = 0
    plugin_version: str = ""
    device_name: str = ""
    device_id: str = ""
    encoding: int = 0
    segments: list[StrokeSegment] = field(default_factory=list)
    session_end: Optional[dict] = None


class BinaryReader:
    def __init__(self, data: bytes):
        self.data = data
        self.offset = 0

    def remaining(self) -> int:
        return len(self.data) - self.offset

    def tell(self) -> int:
        return self.offset

    def seek(self, pos: int) -> None:
        self.offset = pos

    def read_exact(self, n: int) -> bytes:
        if self.offset + n > len(self.data):
            raise StrokeBinError(f"unexpected EOF at {self.offset}, need {n} bytes")
        chunk = self.data[self.offset : self.offset + n]
        self.offset += n
        return chunk

    def u8(self) -> int:
        return self.read_exact(1)[0]

    def u16(self) -> int:
        return struct.unpack_from("<H", self.read_exact(2))[0]

    def u32(self) -> int:
        return struct.unpack_from("<I", self.read_exact(4))[0]

    def u64(self) -> int:
        return struct.unpack_from("<Q", self.read_exact(8))[0]

    def f64(self) -> float:
        return struct.unpack_from("<d", self.read_exact(8))[0]

    def string(self) -> str:
        length = self.u16()
        return self.read_exact(length).decode("utf-8")


def try_read_sample_point(reader: BinaryReader, payload_end: int) -> Optional[SamplePoint]:
    start = reader.tell()
    if payload_end - start < SAMPLE_POINT_SIZE:
        return None
    try:
        point = SamplePoint(
            timestamp_ms=reader.u64(),
            delta_time_ms=reader.u64(),
            x=reader.f64(),
            y=reader.f64(),
            pressure=reader.f64(),
            in_contact=reader.u8() != 0,
            buttons=reader.u32(),
            tilt_x=reader.f64(),
            tilt_y=reader.f64(),
            sequence_id=reader.u64(),
        )
        if reader.tell() > payload_end:
            reader.seek(start)
            return None
        return point
    except StrokeBinError:
        reader.seek(start)
        return None


def find_stroke(segment: StrokeSegment, stroke_id: int) -> Optional[Stroke]:
    for stroke in segment.strokes:
        if stroke.stroke_id == stroke_id:
            return stroke
    return None


def begin_segment(session: RecordingSession) -> StrokeSegment:
    segment = StrokeSegment(segment_id=len(session.segments) + 1)
    session.segments.append(segment)
    return segment


def read_strokebin(path: Path) -> RecordingSession:
    data = path.read_bytes()
    if len(data) < 4 or data[:4] != MAGIC:
        raise StrokeBinError("invalid STRO magic")

    reader = BinaryReader(data)
    reader.seek(4)

    session = RecordingSession(file_path=str(path.resolve()))
    session.version = reader.u32()
    session.created_at_unix_ms = reader.u64()
    session.plugin_version = reader.string()
    session.device_name = reader.string()
    session.device_id = reader.string()
    session.encoding = reader.u8()

    active_segment: Optional[StrokeSegment] = None
    active_stroke: Optional[Stroke] = None

    while reader.remaining() >= 5:
        frame_start = reader.tell()
        type_raw = reader.u8()
        payload_len = reader.u32()
        if reader.remaining() < payload_len:
            # truncated frame — stop like C# reader
            reader.seek(frame_start)
            break

        payload_start = reader.tell()
        payload_end = payload_start + payload_len
        # advance past payload first so unknown types are skippable
        reader.seek(payload_end)
        ep = BinaryReader(data)
        ep.seek(payload_start)

        if type_raw == EVENT_SESSION_START:
            _ = ep.u64()  # createdAt
            session.session_id = ep.string()
            device_name = ep.string()
            device_id = ep.string()
            plugin_version = ep.string()
            _ = ep.u32()  # penUpTimeout
            _ = ep.u32()  # maxStrokes
            if device_name:
                session.device_name = device_name
            if device_id:
                session.device_id = device_id
            if plugin_version:
                session.plugin_version = plugin_version

        elif type_raw == EVENT_STROKE_START:
            if active_segment is None:
                active_segment = begin_segment(session)
            stroke = Stroke(
                stroke_id=ep.u64(),
                start_timestamp_ms=ep.u64(),
            )
            # optional first-point boundary snapshot: read but do not add to points
            if ep.tell() < payload_end:
                try_read_sample_point(ep, payload_end)
            active_segment.strokes.append(stroke)
            active_stroke = stroke

        elif type_raw == EVENT_STROKE_POINT:
            if active_segment is None:
                continue
            stroke_id = ep.u64()
            point = try_read_sample_point(ep, payload_end)
            if point is None:
                continue
            active_stroke = find_stroke(active_segment, stroke_id) or active_stroke
            if active_stroke is not None:
                active_stroke.points.append(point)
                active_stroke.end_timestamp_ms = point.timestamp_ms
                active_segment.point_count += 1

        elif type_raw == EVENT_STROKE_END:
            stroke_id = ep.u64()
            end_ts = ep.u64()
            _ = ep.u32()  # pointCount
            _ = ep.u64()  # duration
            # last-point boundary snapshot not added to points
            if active_stroke is not None and active_stroke.stroke_id == stroke_id:
                active_stroke.end_timestamp_ms = end_ts

        elif type_raw == EVENT_SESSION_FLUSH:
            if active_segment is None:
                active_segment = begin_segment(session)
            active_segment.end_timestamp_ms = ep.u64()
            active_segment.reason = ep.u8()
            active_segment.segment_id = ep.u64()
            _ = ep.u32()  # strokeCount
            active_segment.point_count = ep.u64()
            active_segment.write_status = ep.u8()
            if active_segment.start_timestamp_ms == 0 and active_segment.strokes:
                active_segment.start_timestamp_ms = active_segment.strokes[0].start_timestamp_ms
            active_segment = None
            active_stroke = None

        elif type_raw == EVENT_SESSION_END:
            end_ts = ep.u64() if ep.tell() + 8 <= payload_end else 0
            reason = ep.u8() if ep.tell() + 1 <= payload_end else 0
            completed = ep.u8() if ep.tell() + 1 <= payload_end else 0
            session.session_end = {
                "endTimestampMs": end_ts,
                "endTimestamp": to_iso8601(end_ts) if end_ts else None,
                "reason": FLUSH_REASON.get(reason, "None"),
                "completed": completed == 1,
            }

        else:
            # unknown event already skipped via payloadLen
            pass

    return session


def session_to_jsonl_lines(session: RecordingSession) -> list[str]:
    lines: list[str] = []

    header = {
        "type": "header",
        "version": session.version,
        "createdAt": to_iso8601(session.created_at_unix_ms),
        "createdAtUnixMs": session.created_at_unix_ms,
        "pluginVersion": session.plugin_version,
        "sessionId": session.session_id,
        "filePath": session.file_path,
        "encoding": session.encoding,
        "device": {
            "name": session.device_name,
            "id": session.device_id,
        },
    }
    lines.append(json.dumps(header, ensure_ascii=False, separators=(",", ":")))

    for seg in session.segments:
        lines.append(
            json.dumps(
                {
                    "type": "segment",
                    "segmentId": seg.segment_id,
                    "reason": FLUSH_REASON.get(seg.reason, "None"),
                    "startTimestamp": to_iso8601(seg.start_timestamp_ms)
                    if seg.start_timestamp_ms
                    else None,
                    "endTimestamp": to_iso8601(seg.end_timestamp_ms)
                    if seg.end_timestamp_ms
                    else None,
                    "pointCount": seg.point_count,
                    "writeStatus": WRITE_STATUS.get(seg.write_status, "Ok"),
                    "strokeCount": len(seg.strokes),
                },
                ensure_ascii=False,
                separators=(",", ":"),
            )
        )

        for stroke in seg.strokes:
            lines.append(
                json.dumps(
                    {
                        "type": "stroke",
                        "segmentId": seg.segment_id,
                        "strokeId": stroke.stroke_id,
                        "startTimestamp": to_iso8601(stroke.start_timestamp_ms)
                        if stroke.start_timestamp_ms
                        else None,
                        "endTimestamp": to_iso8601(stroke.end_timestamp_ms)
                        if stroke.end_timestamp_ms
                        else None,
                        "startTimestampMs": stroke.start_timestamp_ms,
                        "endTimestampMs": stroke.end_timestamp_ms,
                        "pointCount": len(stroke.points),
                        "points": [p.to_dict() for p in stroke.points],
                    },
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
            )

            for point in stroke.points:
                rec = {
                    "type": "point",
                    "segmentId": seg.segment_id,
                    "strokeId": stroke.stroke_id,
                }
                rec.update(point.to_dict())
                lines.append(json.dumps(rec, ensure_ascii=False, separators=(",", ":")))

    if session.session_end is not None:
        end_rec = {"type": "session_end"}
        end_rec.update(session.session_end)
        lines.append(json.dumps(end_rec, ensure_ascii=False, separators=(",", ":")))

    summary = {
        "type": "summary",
        "segmentCount": len(session.segments),
        "strokeCount": sum(len(s.strokes) for s in session.segments),
        "pointCount": sum(
            len(st.points) for s in session.segments for st in s.strokes
        ),
    }
    lines.append(json.dumps(summary, ensure_ascii=False, separators=(",", ":")))
    return lines


def convert(input_path: Path, output_path: Path) -> dict:
    session = read_strokebin(input_path)
    lines = session_to_jsonl_lines(session)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return {
        "input": str(input_path),
        "output": str(output_path),
        "segments": len(session.segments),
        "strokes": sum(len(s.strokes) for s in session.segments),
        "points": sum(len(st.points) for s in session.segments for st in s.strokes),
    }


def default_output_path(input_path: Path) -> Path:
    return input_path.with_suffix(".jsonl")


def parse_args(argv: Optional[list[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Convert .strokebin (STRO v1) to JSONL.",
    )
    parser.add_argument(
        "input",
        nargs="?",
        help="Path to .strokebin / .strokebin.part file",
    )
    parser.add_argument(
        "-o",
        "--output",
        help="Output .jsonl path (default: same name with .jsonl)",
    )
    return parser.parse_args(argv)


def main(argv: Optional[list[str]] = None) -> int:
    args = parse_args(argv)
    if not args.input:
        # interactive / drag-drop friendly prompt when launched as exe without args
        try:
            typed = input("Enter .strokebin path (or drag-drop here): ").strip().strip('"')
        except EOFError:
            typed = ""
        if not typed:
            print("Usage: strokebin2jsonl <file.strokebin> [-o out.jsonl]", file=sys.stderr)
            return 2
        args.input = typed

    input_path = Path(args.input)
    if not input_path.is_file():
        print(f"File not found: {input_path}", file=sys.stderr)
        return 1

    output_path = Path(args.output) if args.output else default_output_path(input_path)

    try:
        info = convert(input_path, output_path)
    except StrokeBinError as exc:
        print(f"Convert failed: {exc}", file=sys.stderr)
        return 1
    except Exception as exc:  # noqa: BLE001
        print(f"Unexpected error: {exc}", file=sys.stderr)
        return 1

    print(
        f"OK -> {info['output']} "
        f"(segments={info['segments']}, strokes={info['strokes']}, points={info['points']})"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
