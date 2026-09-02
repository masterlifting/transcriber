"""Reusable faster-whisper transcription with EOF/silence protection."""
from __future__ import annotations

import os
import time
from pathlib import Path
from typing import Any, Optional

from faster_whisper import WhisperModel

from .audio import get_audio_duration
from .models import Segment, TrackTranscription, Word

# Defaults verified against faster-whisper 1.2.1 and tuned empirically on
# Phase 2 recordings (see Phase 3 report).
DEFAULT_MODEL = "turbo"
DEFAULT_DEVICE = "cpu"
DEFAULT_COMPUTE_TYPE = "int8"
DEFAULT_CPU_THREADS = 8
DEFAULT_BEAM_SIZE = 5
DEFAULT_VAD_FILTER = True
# word_timestamps + hallucination_silence_threshold were tested empirically
# (configs B/D/E): they change segment timing for the worse on real content and
# do not remove the trailing-silence hallucination, so they default OFF.
DEFAULT_WORD_TIMESTAMPS = False
DEFAULT_HALLUCINATION_SILENCE_THRESHOLD = None
DEFAULT_CONDITION_ON_PREVIOUS_TEXT = True

# Layer-2 trailing-anomaly filter thresholds (timing/word-count based, no
# phrase lists). A final segment that is very short in absolute word count,
# spans a long stretch, and reaches EOF is a classic trailing-silence
# fabrication. Real final utterances never span long stretches of trailing
# silence (Whisper segments end at the last speech), so a long span + few
# words is a strong signal. Word-COUNT is used instead of word-RATE because
# rate is fragile against CTranslate2 run-to-run timing variance.
TRAILING_MAX_WORDS = 4
TRAILING_MIN_DURATION = 8.0  # segment must be this long to be an anomaly

EOF_EPSILON = 0.05  # seconds of tolerance when comparing against real duration


class TranscriptionError(Exception):
    pass


class Transcriber:
    """Loads the Whisper model exactly once per pipeline run."""

    def __init__(
        self,
        model: str = DEFAULT_MODEL,
        device: str = DEFAULT_DEVICE,
        compute_type: str = DEFAULT_COMPUTE_TYPE,
        cpu_threads: int = DEFAULT_CPU_THREADS,
    ) -> None:
        self._model: Optional[WhisperModel] = None
        self.model_name = model
        self.device = device
        self.compute_type = compute_type
        self.cpu_threads = cpu_threads
        self.load_seconds: Optional[float] = None
        self.last_trailing_removed: Optional[Segment] = None

    def load(self) -> None:
        if self._model is not None:
            return
        model_ref = self._resolve_model_path()
        started = time.perf_counter()
        try:
            self._model = WhisperModel(
                model_ref,
                device=self.device,
                compute_type=self.compute_type,
                cpu_threads=self.cpu_threads,
            )
        except Exception as exc:  # noqa: BLE001
            raise TranscriptionError(
                f"failed to load Whisper model '{model_ref}' "
                f"(device={self.device}, compute_type={self.compute_type}): {exc}"
            ) from exc
        self.load_seconds = time.perf_counter() - started
        print(f"Model source: {model_ref}")

    def _resolve_model_path(self) -> str:
        """Resolve the Whisper model deterministically (no silent fallback).

        Order:
        1. LT_MODEL_PATH env var (set by the orchestrator for Full portable builds).
        2. <approot>/models/turbo next to this package (bundled model).
        3. LT_ALLOW_DOWNLOAD=1 -> the "turbo" alias (Lite / development, HF cache).
        4. Otherwise raise: a bundled model is missing and downloading is not allowed.
        """
        env_path = os.environ.get("LT_MODEL_PATH")
        if env_path:
            return env_path

        bundled = Path(__file__).resolve().parent.parent / "models" / "turbo"
        if (bundled / "model.bin").exists():
            return str(bundled)

        if os.environ.get("LT_ALLOW_DOWNLOAD") == "1":
            return "turbo"

        raise TranscriptionError(
            "Whisper model is missing: no bundled model at "
            f"'{bundled}' and model download is not allowed. "
            "Set LT_MODEL_PATH to the model directory or LT_ALLOW_DOWNLOAD=1 "
            "(lite package) to permit first-run acquisition."
        )

    def transcribe_track(
        self,
        audio_path: Path,
        speaker: str,
        source: str,
        beam_size: int = DEFAULT_BEAM_SIZE,
        vad_filter: bool = DEFAULT_VAD_FILTER,
        word_timestamps: bool = DEFAULT_WORD_TIMESTAMPS,
        hallucination_silence_threshold: Optional[float] = DEFAULT_HALLUCINATION_SILENCE_THRESHOLD,
        condition_on_previous_text: bool = DEFAULT_CONDITION_ON_PREVIOUS_TEXT,
        vad_parameters: Optional[dict[str, Any]] = None,
        enable_trailing_filter: bool = True,
    ) -> TrackTranscription:
        if self._model is None:
            self.load()

        assert self._model is not None
        real_duration = get_audio_duration(audio_path)

        started = time.perf_counter()
        try:
            segments_iter, info = self._model.transcribe(
                str(audio_path),
                beam_size=beam_size,
                vad_filter=vad_filter,
                word_timestamps=word_timestamps,
                hallucination_silence_threshold=(
                    hallucination_silence_threshold if word_timestamps else None
                ),
                condition_on_previous_text=condition_on_previous_text,
                vad_parameters=vad_parameters,
            )
            raw_segments: list[Segment] = []
            for seg in segments_iter:
                words = None
                if seg.words:
                    words = [
                        Word(
                            start=float(w.start),
                            end=float(w.end),
                            word=w.word,
                            probability=float(w.probability) if w.probability is not None else None,
                        )
                        for w in seg.words
                    ]
                raw_segments.append(
                    Segment(
                        speaker=speaker,
                        source=source,
                        start=float(seg.start),
                        end=float(seg.end),
                        text=seg.text.strip(),
                        raw_start=float(seg.start),
                        raw_end=float(seg.end),
                        language=getattr(info, "language", None),
                        language_probability=(
                            float(info.language_probability)
                            if getattr(info, "language_probability", None) is not None
                            else None
                        ),
                        words=words,
                        no_speech_prob=(
                            float(seg.no_speech_prob)
                            if getattr(seg, "no_speech_prob", None) is not None
                            else None
                        ),
                        avg_logprob=(
                            float(seg.avg_logprob)
                            if getattr(seg, "avg_logprob", None) is not None
                            else None
                        ),
                    )
                )
        except Exception as exc:  # noqa: BLE001
            raise TranscriptionError(
                f"Whisper transcription failed for '{audio_path}' "
                f"(source={source}): {exc}"
            ) from exc

        processing_seconds = time.perf_counter() - started

        # ---- Layer 1: hard EOF boundary ----
        kept: list[Segment] = []
        discarded = 0
        clipped = 0
        dropped_empty = 0
        for seg in raw_segments:
            if not seg.text:
                dropped_empty += 1
                continue
            if seg.raw_start >= real_duration - EOF_EPSILON:
                discarded += 1
                continue
            if seg.raw_end > real_duration:
                seg.end = real_duration
                clipped += 1
            kept.append(seg)

        # ---- Layer 2: trailing-silence hallucination filter ----
        # Timing/word-count based (no phrase blacklists): a final segment that
        # (a) reaches or was clipped to EOF, (b) is very short in absolute word
        # count, and (c) spans a long stretch of (mostly silent) audio is a
        # classic trailing-silence fabrication and is dropped.
        removed_trailing = 0
        if enable_trailing_filter and kept:
            last = kept[-1]
            span = last.raw_end - last.raw_start
            word_count = len(last.text.split())
            reaches_eof = last.raw_end >= real_duration - 1.0
            if (
                reaches_eof
                and span >= TRAILING_MIN_DURATION
                and word_count <= TRAILING_MAX_WORDS
            ):
                removed = kept.pop()
                removed_trailing += 1
                self.last_trailing_removed = removed  # for diagnostics

        return TrackTranscription(
            source=source,
            speaker=speaker,
            audio_file=audio_path.name,
            real_duration_seconds=real_duration,
            processing_seconds=processing_seconds,
            detected_language=kept[0].language if kept else (
                getattr(info, "language", None) if "info" in locals() else None
            ),
            language_probability=kept[0].language_probability if kept else None,
            segments=kept,
            raw_segments=raw_segments,
            discarded_beyond_eof=discarded,
            clipped_to_eof=clipped,
            dropped_empty=dropped_empty,
            removed_trailing_hallucinations=removed_trailing,
        )
