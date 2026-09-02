"""Command-line interface for the Phase 3 transcription pipeline.

Usage:
    python -m pipeline.cli <call-directory> [options]
    python -m pipeline <call-directory> [options]

The call directory must contain me.wav, remote.wav and session.json.
Creates transcript.json and transcript.md inside the same directory.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path
from typing import Any, Optional

from .audio import AudioError, get_audio_duration, probe_audio, validate_audio
from .diagnostics import render_diagnostics
from .markdown import render_markdown
from .merger import apply_offset, merge_tracks
from .models import TrackTranscription
from .synchronization import SessionError, load_session
from .transcriber import Transcriber, TranscriptionError

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")  # Windows console fix for Cyrillic

TRACKS = ("me", "remote")
DEFAULT_SPEAKERS = {"me": "Andrei", "remote": "Remote"}


class PipelineError(Exception):
    pass


def parse_args(argv: Optional[list[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="pipeline.cli",
        description="Transcribe a recorded call (me.wav + remote.wav) into transcript.json/md",
    )
    parser.add_argument("call_dir", help="directory containing me.wav, remote.wav, session.json")
    parser.add_argument("--beam-size", type=int, default=5)
    parser.add_argument("--vad", dest="vad_filter", action="store_true", default=True)
    parser.add_argument("--no-vad", dest="vad_filter", action="store_false")
    parser.add_argument("--word-timestamps", dest="word_timestamps", action="store_true", default=False)
    parser.add_argument("--no-word-timestamps", dest="word_timestamps", action="store_false")
    parser.add_argument(
        "--hallucination-silence-threshold",
        type=float,
        default=None,
        help="faster-whisper silence-skip threshold (only active with word "
             "timestamps). Tested empirically: changes segment timing; default off.",
    )
    parser.add_argument(
        "--condition-on-previous-text", dest="condition_on_previous_text",
        action="store_true", default=True,
    )
    parser.add_argument(
        "--no-condition-on-previous-text", dest="condition_on_previous_text",
        action="store_false",
    )
    parser.add_argument(
        "--trailing-filter", dest="trailing_filter", action="store_true", default=True,
        help="drop final segments that are timing/rate anomalies at EOF (default on)",
    )
    parser.add_argument(
        "--no-trailing-filter", dest="trailing_filter", action="store_false",
    )
    parser.add_argument("--language", default=None, help="force Whisper language (e.g. ru, en)")
    parser.add_argument("--speaker-me", default=DEFAULT_SPEAKERS["me"])
    parser.add_argument("--speaker-remote", default=DEFAULT_SPEAKERS["remote"])
    parser.add_argument("--no-session", action="store_true",
                        help="run without session.json (offsets assumed zero)")
    args = parser.parse_args(argv)

    if args.hallucination_silence_threshold is not None and args.hallucination_silence_threshold < 0:
        parser.error("--hallucination-silence-threshold must be >= 0")
    return args


def build_transcript_json(
    timing,
    tracks: dict[str, TrackTranscription],
    merged_segments: list[Any],
) -> dict[str, Any]:
    return {
        "version": 1,
        "session": {
            "source": timing.source if timing is not None else "unknown",
            "startedAtUtc": timing.started_at_utc if timing is not None else None,
            "stoppedAtUtc": timing.stopped_at_utc if timing is not None else None,
            "durationSeconds": (
                timing.session_duration_seconds if timing is not None else None
            ),
            "timingMetadata": timing.to_dict()["timingMetadata"] if timing is not None else {},
        },
        "tracks": {key: t.to_dict() for key, t in tracks.items()},
        "segments": [seg.to_dict() for seg in merged_segments],
    }


def run(args: argparse.Namespace) -> int:
    wall_started = time.perf_counter()
    call_dir = Path(args.call_dir)

    # ---- validate call directory + inputs ----
    if not call_dir.exists():
        raise PipelineError(
            f"call directory does not exist: '{call_dir}' (operation: validate call dir)"
        )
    if not call_dir.is_dir():
        raise PipelineError(f"'{call_dir}' is not a directory (operation: validate call dir)")

    for fname in ("me.wav", "remote.wav"):
        f = call_dir / fname
        if not f.exists():
            raise PipelineError(
                f"missing audio track '{fname}' in '{call_dir}' (operation: validate inputs)"
            )

    # ---- timing metadata ----
    timing = None
    if args.no_session:
        from .models import SessionTiming

        timing = SessionTiming(
            offsets={"me": 0.0, "remote": 0.0},
            notes=["--no-session: offsets assumed zero"],
        )
    else:
        timing = load_session(call_dir)

    # ---- probe tracks up front (fail fast with clear errors) ----
    for key in TRACKS:
        fname = timing.track_files.get(key)
        path = call_dir / (fname or f"{key}.wav")
        if not path.exists():
            raise PipelineError(
                f"track '{key}': file '{path}' not found (session.json declares "
                f"'{fname}', operation: validate track file)"
            )
        validate_audio(path)
        # cross-check session duration when available
        recorded = timing.track_durations.get(key)
        if recorded is not None:
            actual = get_audio_duration(path)
            diff = abs(actual - recorded)
            if diff > 1.0:
                print(
                    f"[WARN] track '{key}': session.json duration {recorded:.2f}s "
                    f"differs from decoded duration {actual:.2f}s by {diff:.2f}s"
                )

    # ---- transcription ----
    print("Loading faster-whisper turbo...")
    transcriber = Transcriber()
    transcriber.load()
    print(f"Model loaded: {transcriber.load_seconds:.1f}s")
    print()

    tracks: dict[str, TrackTranscription] = {}
    total_transcription = 0.0
    for key in TRACKS:
        fname = timing.track_files.get(key) or f"{key}.wav"
        path = call_dir / fname
        speaker = args.speaker_me if key == "me" else args.speaker_remote
        print(f"Track: {fname} (speaker {speaker})")

        threshold = args.hallucination_silence_threshold
        track = transcriber.transcribe_track(
            path,
            speaker=speaker,
            source=key,
            beam_size=args.beam_size,
            vad_filter=args.vad_filter,
            word_timestamps=args.word_timestamps,
            hallucination_silence_threshold=threshold if args.word_timestamps else None,
            condition_on_previous_text=args.condition_on_previous_text,
            vad_parameters={"speech_pad_ms": 200} if args.vad_filter else None,
            enable_trailing_filter=args.trailing_filter,
        )

        offset = timing.offsets.get(key, 0.0)
        apply_offset(track, offset)
        tracks[key] = track
        total_transcription += track.processing_seconds

        print(f"  Duration: {track.real_duration_seconds:.2f}s")
        print(f"  Transcription: {track.processing_seconds:.1f}s")
        print(f"  RTF: {track.rtf:.3f}")
        print(f"  Speed: {track.speed:.2f}x realtime")
        print(f"  Segments: {len(track.segments)} (raw {len(track.raw_segments)})")
        print(f"  Discarded beyond EOF: {track.discarded_beyond_eof}, "
              f"clipped to EOF: {track.clipped_to_eof}, dropped empty: {track.dropped_empty}")
        if track.removed_trailing_hallucinations:
            rem = transcriber.last_trailing_removed
            desc = f" [{rem.raw_start:.2f}->{rem.raw_end:.2f}] '{rem.text}'" if rem else ""
            print(f"  Trailing-silence hallucination removed: {track.removed_trailing_hallucinations}{desc}")
        lang = track.detected_language or "n/a"
        prob = f" ({track.language_probability:.3f})" if track.language_probability is not None else ""
        print(f"  Detected language: {lang}{prob}")
        print()

    # ---- merge ----
    merged = merge_tracks(tracks.values())
    print(f"Synchronization:")
    for key, offset in sorted(timing.offsets.items()):
        print(f"  {key} offset: {offset * 1000:+.0f} ms")
    for note in timing.notes:
        print(f"  note: {note}")
    print()
    print(f"Merge: {len(merged)} segments")
    print()

    # ---- write outputs ----
    transcript_json = build_transcript_json(timing, tracks, merged)
    json_path = call_dir / "transcript.json"
    md_path = call_dir / "transcript.md"

    duration = max((t.real_duration_seconds for t in tracks.values()), default=0.0)
    started_at = timing.started_at_utc if timing is not None else None
    source = timing.source if timing is not None else "unknown"
    md_text = render_markdown(merged, started_at, duration, source)

    for path, text in ((json_path, json.dumps(transcript_json, ensure_ascii=False, indent=2)),
                       (md_path, md_text)):
        if path.exists():
            print(f"[INFO] Overwriting existing {path.name}")
        try:
            path.write_text(text, encoding="utf-8")
        except OSError as exc:
            raise PipelineError(
                f"cannot write output file '{path}': {exc} (operation: write output)"
            ) from exc

    # ---- diagnostics ----
    wall_seconds = time.perf_counter() - wall_started
    print(render_diagnostics(
        model_load_seconds=transcriber.load_seconds,
        tracks=tracks,
        timing=timing,
        total_transcription_seconds=total_transcription,
        pipeline_wall_seconds=wall_seconds,
        total_merged_segments=len(merged),
    ))
    print()
    print("Written:")
    print(f"  {json_path}")
    print(f"  {md_path}")
    return 0


def main(argv: Optional[list[str]] = None) -> int:
    args = parse_args(argv)
    try:
        return run(args)
    except (PipelineError, AudioError, SessionError, TranscriptionError, OSError) as exc:
        print(f"[ERROR] {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("[ERROR] interrupted", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
