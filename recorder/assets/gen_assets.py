"""Generate test audio assets for the two-track recorder prototype.

- speech.wav   : phase-1 Russian speech (test.m4a) decoded to 16 kHz mono PCM16
- tone440.wav  : 30 s 440 Hz sine (process A / isolation target)
- tone1000.wav : 30 s 1000 Hz sine (process B / unrelated audio)
"""
import sys
import numpy as np
import av

SR = 16000


def m4a_to_wav(src: str, dst: str) -> None:
    container = av.open(src)
    stream = container.streams.audio[0]
    samples = []
    for frame in container.decode(stream):
        arr = frame.to_ndarray()
        if arr.ndim == 2:
            arr = arr.mean(axis=0)  # downmix to mono
        samples.append(arr)
    x = np.concatenate(samples).astype(np.float32)
    write_wav(dst, x, stream.codec_context.sample_rate)
    print(f"speech: {dst} {len(x) / stream.codec_context.sample_rate:.1f}s")


def tone_wav(dst: str, freq: float, duration: float) -> None:
    t = np.arange(int(SR * duration)) / SR
    x = (0.5 * np.sin(2 * np.pi * freq * t)).astype(np.float32)
    write_wav(dst, x, SR)
    print(f"tone:   {dst} {duration:.0f}s @ {freq:.0f} Hz")


def write_wav(path: str, x: np.ndarray, rate: int) -> None:
    x16 = (np.clip(x, -1.0, 1.0) * 32767).astype(np.int16)
    container = av.open(path, "w")
    stream = container.add_stream("pcm_s16le", rate=rate)
    stream.layout = "mono"
    frame = av.AudioFrame.from_ndarray(x16.reshape(1, -1), format="s16", layout="mono")
    frame.sample_rate = rate
    for packet in stream.encode(frame):
        container.mux(packet)
    for packet in stream.encode(None):
        container.mux(packet)
    container.close()


if __name__ == "__main__":
    out_dir = sys.argv[1]
    m4a_to_wav(sys.argv[2], f"{out_dir}\\speech.wav")
    tone_wav(f"{out_dir}\\tone440.wav", 440.0, 30.0)
    tone_wav(f"{out_dir}\\tone1000.wav", 1000.0, 30.0)
