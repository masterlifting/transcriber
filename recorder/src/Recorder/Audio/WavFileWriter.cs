using System.Text;
using Transcriber.Core.Interop;

namespace Transcriber.Recorder.Audio;

/// <summary>
/// Minimal RIFF/WAVE writer. Supports PCM16, IEEE float32 and extensible
/// headers. The header sizes are patched on Complete() so the output is a
/// valid WAV even if the process is stopped normally.
/// </summary>
public sealed class WavFileWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly int _fmtChunkSize;
    private long _dataBytes;
    private long _dataSizeOffset; // position of the data-chunk size field
    private bool _completed;

    public long BytesWritten => _dataBytes;

    public WavFileWriter(string path, AudioFormat format)
    {
        _fmtChunkSize = format.FormatTag switch
        {
            Win32.WAVE_FORMAT_EXTENSIBLE => 40,
            Win32.WAVE_FORMAT_IEEE_FLOAT => 18,
            _ => 16,
        };

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        WriteHeader(format);
    }

    private void WriteHeader(AudioFormat f)
    {
        var w = _writer;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0u); // RIFF size, patched in Complete()
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(_fmtChunkSize);
        w.Write(f.FormatTag);
        w.Write(f.Channels);
        w.Write(f.SampleRate);
        w.Write(f.AvgBytesPerSec);
        w.Write(f.BlockAlign);
        w.Write(f.BitsPerSample);
        if (_fmtChunkSize > 16)
        {
            w.Write((ushort)0); // cbSize
            if (_fmtChunkSize == 40)
            {
                w.Write(f.BitsPerSample); // wValidBitsPerSample
                w.Write(0u); // dwChannelMask
                var sub = f.IsFloat ? Win32.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT : Win32.KSDATAFORMAT_SUBTYPE_PCM;
                w.Write(sub.ToByteArray());
            }
        }
        w.Write(Encoding.ASCII.GetBytes("data"));
        _dataSizeOffset = w.BaseStream.Position;
        w.Write(0u); // data size, patched in Complete()
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Cannot write to a completed WAV file");
        }
        _writer.Write(buffer, offset, count);
        _dataBytes += count;
    }

    /// <summary>Patches RIFF/data sizes and flushes the file. Idempotent.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        long headerLen = _dataSizeOffset + 4;
        long pos = _writer.BaseStream.Position;
        _writer.Seek(4, SeekOrigin.Begin);
        _writer.Write((uint)(headerLen - 8 + _dataBytes)); // RIFF chunk size
        _writer.Seek((int)_dataSizeOffset, SeekOrigin.Begin);
        _writer.Write((uint)_dataBytes); // data chunk size
        _writer.Seek((int)pos, SeekOrigin.Begin);
        _writer.Flush();
        _writer.Dispose();
    }

    public void Dispose() => Complete();
}
