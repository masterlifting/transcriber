"""Analyze a WAV file: duration, RMS, peak, and dominant frequency via FFT.

Usage: python analyze.py <wav> [--quiet]
"""
import sys
import numpy as np
import av

path = sys.argv[1]
quiet = "--quiet" in sys.argv

container = av.open(path)
stream = container.streams.audio[0]
channels = stream.codec_context.channels
samples = []
for frame in container.decode(stream):
    a = frame.to_ndarray()
    dt = a.dtype
    arr = a.astype(np.float32)
    if dt == np.int16:
        arr /= 32768.0
    elif dt == np.int32:
        arr /= 2147483648.0
    if arr.ndim == 2:
        if arr.shape[0] == 1:
            # packed/interleaved: (1, samples*channels)
            arr = arr[0].reshape(-1, channels).mean(axis=1)
        else:
            # planar: (channels, samples)
            arr = arr.mean(axis=0)
    samples.append(arr)
x = np.concatenate(samples)
sr = stream.codec_context.sample_rate
duration = len(x) / sr

rms = float(np.sqrt(np.mean(x ** 2))) if len(x) else 0.0
peak = float(np.max(np.abs(x))) if len(x) else 0.0

# dominant frequencies: top-3 spectral peaks
block = int(sr * 0.5)
n_blocks = max(1, len(x) // block)
spec = np.zeros(block // 2 + 1)
for i in range(n_blocks):
    seg = x[i * block:(i + 1) * block]
    if len(seg) < block:
        continue
    spec += np.abs(np.fft.rfft(seg * np.hanning(len(seg))))
freqs = np.fft.rfftfreq(block, 1.0 / sr)
peaks = []
for _ in range(3):
    idx = int(np.argmax(spec))
    peaks.append(float(freqs[idx]))
    lo = max(0, idx - 20)
    hi = min(len(spec), idx + 20)
    spec[lo:hi] = 0
dom = peaks[0] if spec.max() > 0 else 0.0

rms_db = 20 * np.log10(rms + 1e-9)
peak_db = 20 * np.log10(peak + 1e-9)

if not quiet:
    print(f"file     : {path}")
    print(f"format   : {stream.codec_context.name} {sr} Hz {stream.codec_context.channels} ch")
    print(f"duration : {duration:.2f}s")
    print(f"rms      : {rms_db:.1f} dBFS")
    print(f"peak     : {peak_db:.1f} dBFS")
    print(f"top freqs: " + ", ".join(f"{p:.1f} Hz" for p in peaks))
