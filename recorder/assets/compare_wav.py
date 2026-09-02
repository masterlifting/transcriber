"""Compare two WAVs: proper normalization, resample, FFT cross-correlation.

Usage: python compare_wav.py <ref.wav> <test.wav> [--max-seconds N]
"""
import sys
import numpy as np
import av

REF = sys.argv[1]
TEST = sys.argv[2]
MAX_S = float(sys.argv[sys.argv.index("--max-seconds") + 1]) if "--max-seconds" in sys.argv else 30.0


def load(path, max_s):
    c = av.open(path)
    s = c.streams.audio[0]
    ch = s.codec_context.channels
    parts = []
    for f in c.decode(s):
        a = f.to_ndarray()
        dt = a.dtype
        arr = a.astype(np.float32)
        if dt == np.int16:
            arr /= 32768.0
        elif dt == np.int32:
            arr /= 2147483648.0
        if arr.ndim == 2 and arr.shape[0] == 1:
            arr = arr[0].reshape(-1, ch).mean(axis=1)
        elif arr.ndim == 2 and arr.shape[0] > 1:
            arr = arr.mean(axis=0)
        parts.append(arr.ravel())
    x = np.concatenate(parts)
    x = x[: int(max_s * s.codec_context.sample_rate)]
    return x, s.codec_context.sample_rate


def resample(x, sr_from, sr_to):
    if sr_from == sr_to:
        return x
    n = len(x)
    t = np.arange(n) / sr_from
    idx = (t * sr_to).astype(np.int64)
    idx = np.minimum(idx, max(0, int(n * sr_to / sr_from) - 1))
    # linear interp is fine for a diagnostic
    out = np.zeros(int(n * sr_to / sr_from), dtype=np.float32)
    idx = np.minimum(idx, len(out) - 1)
    out[idx] = x
    return out


def main():
    a, sr_a = load(REF, MAX_S)
    b, sr_b = load(TEST, MAX_S)
    print(f"ref  {REF}: sr={sr_a}, {len(a)/sr_a:.1f}s, rms={20*np.log10(np.sqrt(np.mean(a**2))+1e-12):.1f} dBFS, peak={20*np.log10(np.max(np.abs(a))+1e-12):.1f} dBFS")
    print(f"test {TEST}: sr={sr_b}, {len(b)/sr_b:.1f}s, rms={20*np.log10(np.sqrt(np.mean(b**2))+1e-12):.1f} dBFS, peak={20*np.log10(np.max(np.abs(b))+1e-12):.1f} dBFS")

    sr = 16000
    a = resample(a, sr_a, sr)[: int(MAX_S * sr)]
    b = resample(b, sr_b, sr)[: int(MAX_S * sr)]
    n = min(len(a), len(b))
    a, b = a[:n], b[:n]

    an = a / (np.sqrt(np.mean(a**2)) + 1e-12)
    bn = b / (np.sqrt(np.mean(b**2)) + 1e-12)

    # FFT cross-correlation
    nfft = 1 << (2 * n - 1).bit_length()
    fa = np.fft.rfft(an, nfft)
    fb = np.fft.rfft(bn, nfft)
    corr = np.fft.irfft(fa * np.conj(fb), nfft)
    half = nfft // 2
    # wrap: lags in [-half, half]
    lags = np.arange(nfft)
    lags = np.where(lags > half, lags - nfft, lags)
    peak_idx = int(np.argmax(np.abs(corr[: nfft])))
    peak_corr = float(np.abs(corr[peak_idx])) / n
    print(f"best cross-correlation: {peak_corr:.4f} at lag {lags[peak_idx]} samples ({lags[peak_idx]/sr:.3f}s)")


if __name__ == "__main__":
    main()
