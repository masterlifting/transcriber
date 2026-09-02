"""Diagnostics: performance metrics and per-track quality counters."""
from __future__ import annotations

from typing import Optional

from .models import SessionTiming, TrackTranscription


def render_diagnostics(
    model_load_seconds: Optional[float],
    tracks: dict[str, TrackTranscription],
    timing: Optional[SessionTiming],
    total_transcription_seconds: float,
    pipeline_wall_seconds: float,
    total_merged_segments: int,
) -> str:
    lines: list[str] = []

    if model_load_seconds is not None:
        lines.append(f"Model loaded: {model_load_seconds:.1f}s")
    lines.append("")

    for key in ("me", "remote"):
        t = tracks.get(key)
        if t is None:
            continue
        lines.append(f"Track: {t.audio_file}")
        lines.append(f"Duration: {t.real_duration_seconds:.2f}s")
        lines.append(f"Transcription: {t.processing_seconds:.1f}s")
        lines.append(f"RTF: {t.rtf:.3f}")
        lines.append(f"Speed: {t.speed:.2f}x realtime")
        lines.append(
            f"Segments: {len(t.segments)} "
            f"(raw {len(t.raw_segments)}, "
            f"discarded beyond EOF {t.discarded_beyond_eof}, "
            f"clipped to EOF {t.clipped_to_eof}, "
            f"dropped empty {t.dropped_empty})"
        )
        lines.append(f"Detected language: {t.detected_language} "
                     f"(probability {t.language_probability:.3f})" if t.language_probability else
                     f"Detected language: {t.detected_language}")
        lines.append("")

    if timing is not None:
        lines.append("Synchronization:")
        for key, offset in sorted(timing.offsets.items()):
            lines.append(f"  {key} offset: {offset * 1000:+.0f} ms")
        for note in timing.notes:
            lines.append(f"  note: {note}")
        lines.append("")

    lines.append("Merge:")
    lines.append(f"  Total segments: {total_merged_segments}")
    lines.append("")

    lines.append("Performance:")
    lines.append(f"  Model load: {model_load_seconds:.1f}s" if model_load_seconds is not None else "  Model load: n/a")
    for key, t in tracks.items():
        lines.append(f"  {key} transcription: {t.processing_seconds:.1f}s")
    lines.append(f"  Total transcription: {total_transcription_seconds:.1f}s")
    lines.append(f"  Pipeline wall time: {pipeline_wall_seconds:.1f}s")

    return "\n".join(lines)
