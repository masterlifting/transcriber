using System.Runtime.InteropServices;
using Transcriber.Core.Interop;

namespace Transcriber.Recorder.Audio;

/// <summary>
/// Parsed audio format. The raw WAVEFORMATEX/WAVEFORMATEXTENSIBLE pointer
/// returned by IAudioClient::GetMixFormat is decoded into plain container
/// fields (tag normalized to plain PCM / IEEE float for the WAV header).
/// </summary>
public sealed class AudioFormat
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SampleRate;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort CbSize; // raw cbSize from the native struct
    public bool IsFloat;

    public static AudioFormat FromWaveFormatEx(IntPtr p)
    {
        var f = new AudioFormat
        {
            FormatTag = (ushort)Marshal.ReadInt16(p, 0),
            Channels = (ushort)Marshal.ReadInt16(p, 2),
            SampleRate = (uint)Marshal.ReadInt32(p, 4),
            AvgBytesPerSec = (uint)Marshal.ReadInt32(p, 8),
            BlockAlign = (ushort)Marshal.ReadInt16(p, 12),
            BitsPerSample = (ushort)Marshal.ReadInt16(p, 14),
            CbSize = (ushort)Marshal.ReadInt16(p, 16),
        };

        if (f.FormatTag == Win32.WAVE_FORMAT_IEEE_FLOAT)
        {
            f.IsFloat = true;
        }
        else if (f.FormatTag == Win32.WAVE_FORMAT_EXTENSIBLE)
        {
            // WAVEFORMATEXTENSIBLE: WAVEFORMATEX (18) + wValidBitsPerSample (2)
            // + dwChannelMask (4) + SubFormat GUID (16) -> GUID at offset 24.
            byte[] guidBytes = new byte[16];
            Marshal.Copy(p + 24, guidBytes, 0, 16);
            var sub = new Guid(guidBytes);
            f.IsFloat = sub == Win32.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            // Normalize the container tag for a simple WAV header.
            f.FormatTag = f.IsFloat ? Win32.WAVE_FORMAT_IEEE_FLOAT : Win32.WAVE_FORMAT_PCM;
        }

        return f;
    }

    /// <summary>
    /// Byte copy of the original native format struct (18 bytes + cbSize),
    /// suitable to pass back to IAudioClient::Initialize unchanged.
    /// </summary>
    public static IntPtr CopyRaw(IntPtr src)
    {
        ushort cbSize = (ushort)Marshal.ReadInt16(src, 16);
        int size = 18 + cbSize;
        IntPtr dst = Marshal.AllocHGlobal(size);
        byte[] tmp = new byte[size];
        Marshal.Copy(src, tmp, 0, size);
        Marshal.Copy(tmp, 0, dst, size);
        return dst;
    }

    public override string ToString()
    {
        string sampleType = IsFloat ? "IEEE float 32" : $"PCM {BitsPerSample}-bit";
        return $"{SampleRate} Hz, {Channels} ch, {sampleType}";
    }
}
