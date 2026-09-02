"""Markdown transcript rendering (simple, no summarization, wording kept)."""
from __future__ import annotations

from typing import Optional

from .models import Segment


def format_timestamp(seconds: float, long_form: bool) -> str:
    total = max(0, int(round(seconds)))
    hours, rem = divmod(total, 3600)
    minutes, secs = divmod(rem, 60)
    if long_form:
        return f"{hours:02d}:{minutes:02d}:{secs:02d}"
    return f"{minutes:02d}:{secs:02d}"


def render_markdown(
    segments: list[Segment],
    started_at_utc: Optional[str],
    duration_seconds: Optional[float],
    source: str,
) -> str:
    long_form = bool(duration_seconds and duration_seconds >= 3600)

    lines: list[str] = []
    lines.append("# Call Transcript")
    lines.append("")
    if started_at_utc:
        lines.append(f"**Date:** {started_at_utc[:10]}")
    if duration_seconds is not None:
        lines.append(f"**Duration:** {format_timestamp(duration_seconds, long_form)}")
    lines.append(f"**Source:** {source}")
    lines.append("")
    lines.append("---")
    lines.append("")

    for seg in segments:
        ts = format_timestamp(seg.start, long_form)
        lines.append(f"**[{ts}] {seg.speaker}**")
        lines.append("")
        lines.append(seg.text)
        lines.append("")

    return "\n".join(lines)
