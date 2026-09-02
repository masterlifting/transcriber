"""Windowed analysis of a WAV via raw bytes (no PyAV)."""
import sys
import wave
import numpy as np

path = sys.argv[1]
w = wave.open(path, "rb")
raw = w.readframes(w.getnframes())
x = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
sr = w.getframerate()
ch = w.getnchannels()
xm = x.reshape(-1, ch).mean(axis=1) if ch > 1 else x

print(f"file: {path}  {sr} Hz {ch} ch  {len(xm)/sr:.1f}s")
print(f"overall rms: {20*np.log10(np.sqrt(np.mean(xm**2))+1e-12):.1f} dBFS  peak: {20*np.log10(np.max(np.abs(xm))+1e-12):.1f} dBFS")

win = 4 * sr
print(f"{'time':>5s} {'rms(dB)':>9s} {'peak(dB)':>9s} {'domfreq':>9s}")
for i in range(0, len(xm) - win + 1, win):
    seg = xm[i : i + win]
    rms = 20 * np.log10(np.sqrt(np.mean(seg**2)) + 1e-12)
    pk = 20 * np.log10(np.max(np.abs(seg)) + 1e-12)
    spec = np.abs(np.fft.rfft(seg * np.hanning(len(seg))))
    fr = np.fft.rfftfreq(len(seg), 1 / sr)
    dom = fr[np.argmax(spec)]
    print(f"{i//sr:5d}s {rms:9.1f} {pk:9.1f} {dom:9.1f}Hz")

# loudest 200ms window: print a coarse waveform to see tone vs speech
loud = int(np.argmax(np.abs(xm)))
start = max(0, loud - sr // 10)
seg = xm[start : start + sr // 5]
peaks = np.array_split(seg, 20)
bars = "".join("#" * int(round(30 * np.max(np.abs(p)) / (np.max(np.abs(xm)) + 1e-12))) or "." for p in peaks)
print(f"waveform around loudest sample ({loud/sr:.2f}s): {bars}")
