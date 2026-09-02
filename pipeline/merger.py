"""Merge both tracks into one chronological conversation."""
from __future__ import annotations

from typing import Iterable

from .models import Segment, TrackTranscription

# Deterministic tie-break order when two segments share (nearly) the same start.
SOURCE_ORDER = {"me": 0, "remote": 1}


def apply_offset(track: TrackTranscription, offset_seconds: float) -> TrackTranscription:
    """Shift a track's segments onto the common session timeline (in place).

    Uses the corrected ``start``/``end`` (which may already be clipped to EOF),
    never the raw values, so the EOF boundary survives the shift.
    """
    for seg in track.segments:
        seg.start += offset_seconds
        seg.end += offset_seconds
    return track


def merge_tracks(tracks: Iterable[TrackTranscription]) -> list[Segment]:
    """Chronological merge; overlapping speech is preserved, never trimmed.

    Primary key: corrected start ascending.
    Tie-break: corrected end, then source order (me before remote), then text.
    """
    all_segments = [seg for track in tracks for seg in track.segments]

    def sort_key(seg: Segment):
        return (
            round(seg.start, 3),
            round(seg.end, 3),
            SOURCE_ORDER.get(seg.source, 9),
            seg.text,
        )

    return sorted(all_segments, key=sort_key)
