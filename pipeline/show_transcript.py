"""Print a merged transcript summary from transcript.json (diagnostic helper)."""
import json
import sys

path = sys.argv[1]
d = json.load(open(path, encoding="utf-8"))
segs = d["segments"]
print(f"total segments: {len(segs)}")
for s in segs:
    print(f"[{s['start']:7.2f}->{s['end']:7.2f}] {s['speaker']:8s} {s['text'][:60]}")
if segs:
    print(f"max start: {max(s['start'] for s in segs):.2f}  max end: {max(s['end'] for s in segs):.2f}")
