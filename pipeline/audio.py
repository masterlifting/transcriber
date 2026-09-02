"""Audio probing and real-duration measurement via PyAV (already in the env).

Real media duration is measured by decoding all audio frames and summing
per-channel sample counts. It is independent of Whisper segment timestamps,
so it cannot be inflated by hallucinated segments beyond EOF.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any, Optional

import av


class AudioError(Exception):
    pass


def probe_audio(path: Path) -> dict[str, Any]:
    """Return basic stream metadata, raising AudioError with context."""
    try:
        container = av.open(str(path))
    except Exception as exc:  # noqa: BLE001 - wrap with file/operation context
        raise AudioError(f"cannot open audio file '{path}': {exc}") from exc

    try:
        if not container.streams.audio:
            raise AudioError(f"no audio stream found in '{path}'")
        stream = container.streams.audio[0]
        cc = stream.codec_context
        return {
            "codec": cc.name,
            "sample_rate": cc.sample_rate,
            "channels": cc.channels,
            "format": cc.format.name if cc.format else None,
        }
    finally:
        container.close()


def get_audio_duration(path: Path) -> float:
    """Decode the whole file and return real duration in seconds.

    Uses per-channel sample counts (frame.samples) summed over all frames,
    avoiding any shape/planar ambiguity and any dependence on container
    metadata or Whisper output.
    """
    try:
        container = av.open(str(path))
    except Exception as exc:  # noqa: BLE001
        raise AudioError(f"cannot open audio file '{path}': {exc}") from exc

    try:
        if not container.streams.audio:
            raise AudioError(f"no audio stream found in '{path}'")
        stream = container.streams.audio[0]
        rate = stream.codec_context.sample_rate
        if not rate:
            raise AudioError(f"audio stream in '{path}' has no sample rate")
        total_samples: int = 0
        for frame in container.decode(stream):
            total_samples += frame.samples
        if total_samples <= 0:
            raise AudioError(f"audio file '{path}' decoded to zero samples (empty or corrupt)")
        return total_samples / rate
    finally:
        container.close()


def validate_audio(path: Path, expected_min_duration: float = 0.05) -> dict[str, Any]:
    """Validate that the file opens and has a usable duration."""
    info = probe_audio(path)
    duration = get_audio_duration(path)
    if duration <= expected_min_duration:
        raise AudioError(
            f"audio file '{path}' has near-zero duration ({duration:.3f}s); "
            "cannot transcribe a zero-length track"
        )
    return {"info": info, "duration": duration}
