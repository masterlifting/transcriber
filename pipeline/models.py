"""Canonical data models for the transcription pipeline."""
from __future__ import annotations

from dataclasses import dataclass, field, asdict
from typing import Any, Optional


@dataclass
class Word:
    start: float
    end: float
    word: str
    probability: Optional[float] = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "start": round(self.start, 3),
            "end": round(self.end, 3),
            "word": self.word,
            "probability": self.probability,
        }


@dataclass
class Segment:
    """One utterance on the common session timeline.

    ``start``/``end`` are corrected (session timeline, offset applied).
    ``raw_start``/``raw_end`` are the original Whisper timestamps from the
    track's own media timeline.
    """

    speaker: str
    source: str
    start: float
    end: float
    text: str
    raw_start: Optional[float] = None
    raw_end: Optional[float] = None
    language: Optional[str] = None
    language_probability: Optional[float] = None
    words: Optional[list[Word]] = None
    no_speech_prob: Optional[float] = None
    avg_logprob: Optional[float] = None

    def to_dict(self) -> dict[str, Any]:
        d: dict[str, Any] = {
            "speaker": self.speaker,
            "source": self.source,
            "start": round(self.start, 3),
            "end": round(self.end, 3),
            "text": self.text,
        }
        if self.raw_start is not None:
            d["rawStart"] = round(self.raw_start, 3)
        if self.raw_end is not None:
            d["rawEnd"] = round(self.raw_end, 3)
        if self.language is not None:
            d["language"] = self.language
        if self.language_probability is not None:
            d["languageProbability"] = round(self.language_probability, 3)
        if self.no_speech_prob is not None:
            d["noSpeechProb"] = round(self.no_speech_prob, 3)
        if self.avg_logprob is not None:
            d["avgLogprob"] = round(self.avg_logprob, 3)
        if self.words:
            d["words"] = [w.to_dict() for w in self.words]
        return d


@dataclass
class TrackTranscription:
    """Result of transcribing a single track."""

    source: str  # "me" | "remote"
    speaker: str  # "Andrei" | "Remote"
    audio_file: str
    real_duration_seconds: float  # decoded media duration, independent of Whisper
    processing_seconds: float
    detected_language: Optional[str]
    language_probability: Optional[float]
    segments: list[Segment] = field(default_factory=list)  # corrected to session timeline
    raw_segments: list[Segment] = field(default_factory=list)  # as produced by Whisper
    discarded_beyond_eof: int = 0
    clipped_to_eof: int = 0
    dropped_empty: int = 0
    removed_trailing_hallucinations: int = 0

    @property
    def rtf(self) -> float:
        return self.processing_seconds / self.real_duration_seconds if self.real_duration_seconds > 0 else 0.0

    @property
    def speed(self) -> float:
        return self.real_duration_seconds / self.processing_seconds if self.processing_seconds > 0 else 0.0

    def to_dict(self) -> dict[str, Any]:
        return {
            "speaker": self.speaker,
            "audioFile": self.audio_file,
            "durationSeconds": round(self.real_duration_seconds, 3),
            "processingSeconds": round(self.processing_seconds, 3),
            "rtf": round(self.rtf, 3),
            "speed": round(self.speed, 2),
            "detectedLanguage": self.detected_language,
            "languageProbability": round(self.language_probability, 3) if self.language_probability is not None else None,
            "segmentsKept": len(self.segments),
            "segmentsRaw": len(self.raw_segments),
            "segmentsDiscardedBeyondEof": self.discarded_beyond_eof,
            "segmentsClippedToEof": self.clipped_to_eof,
            "segmentsDroppedEmpty": self.dropped_empty,
            "trailingHallucinationsRemoved": self.removed_trailing_hallucinations,
        }


@dataclass
class SessionTiming:
    """Timing metadata parsed from session.json."""

    started_at_utc: Optional[str] = None
    stopped_at_utc: Optional[str] = None
    session_duration_seconds: Optional[float] = None
    source: str = "unknown"
    track_files: dict[str, str] = field(default_factory=dict)
    track_durations: dict[str, float] = field(default_factory=dict)
    first_packet_utc: dict[str, str] = field(default_factory=dict)
    offset_mic_minus_remote_ms: Optional[float] = None
    offsets: dict[str, float] = field(default_factory=dict)  # per-track shift onto session timeline
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return {
            "source": self.source,
            "startedAtUtc": self.started_at_utc,
            "stoppedAtUtc": self.stopped_at_utc,
            "durationSeconds": self.session_duration_seconds,
            "timingMetadata": {
                "trackFiles": self.track_files,
                "trackDurations": self.track_durations,
                "firstPacketAtUtc": self.first_packet_utc,
                "offsetMicMinusRemoteMs": self.offset_mic_minus_remote_ms,
                "appliedOffsetsSeconds": self.offsets,
                "notes": self.notes,
            },
        }
