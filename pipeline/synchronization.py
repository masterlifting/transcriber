"""Session timing metadata: parse session.json and map tracks to a common
session timeline (session time 0 = earliest capture start)."""
from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

from .models import SessionTiming


class SessionError(Exception):
    pass


def parse_iso_timestamp(value: Any) -> Optional[datetime]:
    """Parse an ISO-8601 timestamp that may end with 'Z' or an offset."""
    if not value:
        return None
    text = str(value).strip()
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError as exc:
        raise SessionError(f"invalid timestamp '{text}' in session.json: {exc}") from exc


def load_session(call_dir: Path) -> SessionTiming:
    path = call_dir / "session.json"
    if not path.exists():
        raise SessionError(f"session.json not found in '{call_dir}'")

    try:
        raw: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise SessionError(
            f"invalid JSON in session.json: {exc.msg} at line {exc.lineno} column {exc.colno}"
        ) from exc
    except OSError as exc:
        raise SessionError(f"cannot read session.json: {exc}") from exc

    if not isinstance(raw, dict):
        raise SessionError("session.json root must be a JSON object")

    timing = SessionTiming()
    timing.started_at_utc = raw.get("startedAtUtc")
    timing.stopped_at_utc = raw.get("stoppedAtUtc")
    timing.offset_mic_minus_remote_ms = raw.get("startOffsetMicMinusRemoteMs")

    if timing.started_at_utc and timing.stopped_at_utc:
        try:
            start = parse_iso_timestamp(timing.started_at_utc)
            stop = parse_iso_timestamp(timing.stopped_at_utc)
            if start and stop:
                timing.session_duration_seconds = (stop - start).total_seconds()
        except SessionError:
            raise

    # Track objects: "microphone" (me) and "remote".
    track_map = {"me": raw.get("microphone"), "remote": raw.get("remote")}
    for key, obj in track_map.items():
        if not isinstance(obj, dict):
            raise SessionError(
                f"session.json is missing the '{key}' track object "
                f"(expected key 'microphone'/'remote' with a JSON object)"
            )
        fname = obj.get("file")
        if not isinstance(fname, str) or not fname:
            raise SessionError(f"session.json track '{key}' is missing a 'file' field")
        timing.track_files[key] = fname
        dur = obj.get("durationSeconds")
        if isinstance(dur, (int, float)):
            timing.track_durations[key] = float(dur)
        first = obj.get("firstPacketAtUtc")
        if first:
            parse_iso_timestamp(first)  # validates
            timing.first_packet_utc[key] = str(first)

    # Call source: prefer the remote process name from the recorder.
    remote_obj = raw.get("remote")
    if isinstance(remote_obj, dict) and remote_obj.get("process"):
        timing.source = str(remote_obj["process"])

    # ---- Compute per-track offset onto the common session timeline ----
    # session time 0 = earliest capture start across tracks.
    firsts: dict[str, datetime] = {}
    for key in ("me", "remote"):
        if key in timing.first_packet_utc:
            ts = parse_iso_timestamp(timing.first_packet_utc[key])
            if ts is not None:
                firsts[key] = ts

    if len(firsts) == 2:
        t0 = min(firsts.values())
        timing.offsets = {
            key: (ts - t0).total_seconds() for key, ts in firsts.items()
        }
        timing.notes.append(
            "offsets computed from per-track firstPacketAtUtc timestamps"
        )
    elif timing.offset_mic_minus_remote_ms is not None:
        # startOffsetMicMinusRemoteMs = mic_first_packet - remote_first_packet.
        # Positive means the mic started after remote.
        offset = float(timing.offset_mic_minus_remote_ms) / 1000.0
        timing.offsets = {"remote": 0.0, "me": offset}
        timing.notes.append(
            "offsets derived from startOffsetMicMinusRemoteMs "
            f"(remote=0, me=+{offset * 1000:.0f} ms)"
        )
    else:
        timing.offsets = {"remote": 0.0, "me": 0.0}
        timing.notes.append(
            "NO timing metadata found in session.json; "
            "tracks aligned with zero offset (raw timestamps preserved)"
        )

    return timing
