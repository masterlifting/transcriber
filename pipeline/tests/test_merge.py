"""Quick sanity checks for merge/overlap/offset semantics (no model needed)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent))

from pipeline.models import Segment, TrackTranscription
from pipeline.merger import apply_offset, merge_tracks


def track(source: str, speaker: str, segs: list[Segment]) -> TrackTranscription:
    return TrackTranscription(
        source=source, speaker=speaker, audio_file=f"{source}.wav",
        real_duration_seconds=100.0, processing_seconds=1.0,
        detected_language="ru", language_probability=0.9,
        segments=segs, raw_segments=segs,
    )


# --- overlap preservation ---
me = track("me", "Andrei", [Segment("Andrei", "me", 12.2, 15.1, "a", raw_start=12.0, raw_end=15.0)])
rem = track("remote", "Remote", [Segment("Remote", "remote", 14.6, 18.0, "b", raw_start=14.6, raw_end=18.0)])
merged = merge_tracks([me, rem])
assert [s.speaker for s in merged] == ["Andrei", "Remote"], [s.speaker for s in merged]
assert len(merged) == 2, "overlapping segments must both survive"
print("overlap preserved, order by start:", [f"{s.speaker}@{s.start:.1f}" for s in merged])

# --- deterministic tie-break on equal starts (end asc, then source order) ---
me2 = track("me", "Andrei", [Segment("Andrei", "me", 5.0, 7.0, "x", raw_start=5.0, raw_end=7.0)])
rem2 = track("remote", "Remote", [Segment("Remote", "remote", 5.0, 6.0, "y", raw_start=5.0, raw_end=6.0)])
m2 = merge_tracks([me2, rem2])
assert len(m2) == 2, "both equal-start segments must survive"
assert [s.speaker for s in m2] == ["Remote", "Andrei"], [s.speaker for s in m2]  # end 6.0 < 7.0
m2b = merge_tracks([me2, rem2])
assert [s.speaker for s in m2b] == [s.speaker for s in m2], "ordering must be deterministic"
print("equal-start tie-break deterministic (end asc):", [s.speaker for s in m2])

# --- offset preserves clipped end ---
seg = Segment("X", "me", 12.0, 100.0, "t", raw_start=12.0, raw_end=200.0)  # end was clipped to 100
t3 = track("me", "X", [seg])
apply_offset(t3, 0.198)
assert abs(seg.end - 100.198) < 1e-9, f"clip undone by offset: end={seg.end}"
assert abs(seg.start - 12.198) < 1e-9
assert abs(seg.raw_end - 200.0) < 1e-9, "raw values must stay untouched"
print("offset preserves EOF clip: ok")

print("ALL CHECKS PASSED")
