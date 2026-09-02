using System.ComponentModel;
using System.Runtime.InteropServices;
using Transcriber.Recorder.Audio;
using Transcriber.Recorder.Diagnostics;
using Transcriber.Core.Interop;

namespace Transcriber.Recorder.ProcessAudio;

/// <summary>
/// Captures audio rendered by a specific process tree using Windows process
/// loopback (virtual device "VAD\Process_Loopback"). This is NOT ordinary
/// output-device loopback: unrelated processes' audio is excluded by the OS.
/// Capture format follows the Microsoft sample: 16-bit PCM 44.1 kHz stereo
/// with AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM (the engine converts the mix).
/// </summary>
public static class ProcessLoopbackRecorder
{
    // Fixed capture format per the current Microsoft Application Loopback sample.
    private static readonly AudioFormat CaptureFormat = new()
    {
        FormatTag = Win32.WAVE_FORMAT_PCM,
        Channels = 2,
        SampleRate = 44100,
        BitsPerSample = 16,
        BlockAlign = 4,
        AvgBytesPerSec = 44100 * 4,
        CbSize = 0,
        IsFloat = false,
    };

    public sealed record Result(
        int Pid,
        bool IncludeProcessTree,
        AudioFormat Format,
        string OutputPath,
        DateTime StartedAtUtc,
        DateTime StoppedAtUtc,
        TimeSpan Duration,
        long FirstPacketQpc,
        DateTime FirstPacketAtUtc,
        long BytesWritten);

    public static Result Record(int pid, bool includeProcessTree, string outputPath, CancellationToken stopToken)
    {
        int initHr = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED);
        bool comInitialized = initHr == 0;
        if (initHr != 0 && initHr != unchecked((int)Win32.RPC_E_CHANGED_MODE))
        {
            Win32.HResult.Check(initHr, "CoInitializeEx");
        }

        try
        {
            Log.Info($"Starting process loopback: PID={pid} (includeProcessTree={includeProcessTree})");

            var activation = ProcessLoopbackInterop.ActivateProcessLoopback((uint)pid, includeProcessTree);
            try
            {
                if (!activation.Handler.Done.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException(
                        "Timed out waiting for ActivateAudioInterfaceAsync completion (15 s)");
                }

                if (activation.Handler.GetActivateResultHr < 0)
                {
                    throw new InvalidOperationException(
                        $"Process loopback activation failed: IActivateAudioInterfaceAsyncOperation::GetActivateResult " +
                        $"HRESULT 0x{activation.Handler.GetActivateResultHr:X8}");
                }
                if (activation.Handler.ActivationHr < 0)
                {
                    throw new InvalidOperationException(
                        $"Process loopback activation failed: HRESULT 0x{activation.Handler.ActivationHr:X8} " +
                        $"({Marshal.GetExceptionForHR(activation.Handler.ActivationHr)?.Message})");
                }
                if (activation.Handler.ActivatedInterface is not Win32.IAudioClient audioClient)
                {
                    throw new InvalidOperationException(
                        "Process loopback activation did not return an IAudioClient");
                }

                try
                {
                    return RunCapture(audioClient, pid, includeProcessTree, outputPath, stopToken);
                }
                finally
                {
                    Marshal.ReleaseComObject(audioClient);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(activation.ActivationParamsPtr);
            }
        }
        finally
        {
            if (comInitialized)
            {
                Win32.CoUninitialize();
            }
        }
    }

    private static Result RunCapture(
        Win32.IAudioClient audioClient,
        int pid,
        bool includeProcessTree,
        string outputPath,
        CancellationToken stopToken)
    {
        IntPtr eventHandle = IntPtr.Zero;
        try
        {
            IntPtr formatPtr = Marshal.AllocHGlobal(18);
            try
            {
                Marshal.WriteInt16(formatPtr, 0, (short)CaptureFormat.FormatTag);
                Marshal.WriteInt16(formatPtr, 2, (short)CaptureFormat.Channels);
                Marshal.WriteInt32(formatPtr, 4, (int)CaptureFormat.SampleRate);
                Marshal.WriteInt32(formatPtr, 8, (int)CaptureFormat.AvgBytesPerSec);
                Marshal.WriteInt16(formatPtr, 12, (short)CaptureFormat.BlockAlign);
                Marshal.WriteInt16(formatPtr, 14, (short)CaptureFormat.BitsPerSample);
                Marshal.WriteInt16(formatPtr, 16, 0);

                Win32.HResult.Check(
                    audioClient.Initialize(
                        Win32.AUDCLNT_SHAREMODE_SHARED,
                        Win32.AUDCLNT_STREAMFLAGS_LOOPBACK
                        | Win32.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                        | Win32.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                        0,
                        0,
                        formatPtr,
                        IntPtr.Zero),
                    "IAudioClient::Initialize(shared, LOOPBACK|EVENTCALLBACK|AUTOCONVERTPCM)");
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            Win32.HResult.Check(audioClient.GetBufferSize(out uint bufferFrames), "IAudioClient::GetBufferSize");
            var iidCapture = Win32.IID_IAudioCaptureClient;
            Win32.HResult.Check(
                audioClient.GetService(ref iidCapture, out object captureObj),
                "IAudioClient::GetService(IAudioCaptureClient)");
            var capture = (Win32.IAudioCaptureClient)captureObj;
            try
            {
                eventHandle = Win32.CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
                if (eventHandle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEvent");
                }
                Win32.HResult.Check(audioClient.SetEventHandle(eventHandle), "IAudioClient::SetEventHandle");

                Log.Info($"Capture format: {CaptureFormat}");

                var writer = new WavFileWriter(outputPath, CaptureFormat);
                byte[] scratch = new byte[bufferFrames * CaptureFormat.BlockAlign];
                try
                {
                    Win32.HResult.Check(audioClient.Start(), "IAudioClient::Start");
                    Log.Info("Process capture started");

                    DateTime startedAtUtc = DateTime.UtcNow;
                    long firstQpc = 0;
                    DateTime firstPacketAtUtc = startedAtUtc;
                    bool gotFirstPacket = false;

                    while (!stopToken.IsCancellationRequested)
                    {
                        uint wait = Win32.WaitForSingleObject(eventHandle, 200);
                        if (wait == Win32.WAIT_TIMEOUT)
                        {
                            continue;
                        }
                        if (wait != Win32.WAIT_OBJECT_0)
                        {
                            throw new InvalidOperationException(
                                $"WaitForSingleObject failed: 0x{wait:X8} (GetLastError={Marshal.GetLastWin32Error()})");
                        }

                        while (true)
                        {
                            Win32.HResult.Check(
                                capture.GetNextPacketSize(out uint framesAvail),
                                "IAudioCaptureClient::GetNextPacketSize");
                            if (framesAvail == 0)
                            {
                                break;
                            }

                            Win32.HResult.Check(
                                capture.GetBuffer(
                                    out IntPtr data,
                                    out uint frames,
                                    out uint flags,
                                    out ulong devPos,
                                    out ulong qpcPos),
                                "IAudioCaptureClient::GetBuffer");

                            int bytes = (int)(frames * CaptureFormat.BlockAlign);
                            if (!gotFirstPacket)
                            {
                                gotFirstPacket = true;
                                firstQpc = (long)qpcPos;
                                firstPacketAtUtc = DateTime.UtcNow;
                            }
                            Marshal.Copy(data, scratch, 0, bytes);
                            Win32.HResult.Check(capture.ReleaseBuffer(frames), "IAudioCaptureClient::ReleaseBuffer");
                            writer.Write(scratch, 0, bytes);
                        }
                    }

                    Log.Info("Stop requested");
                    Win32.HResult.Check(audioClient.Stop(), "IAudioClient::Stop");
                    writer.Complete();
                    DateTime stoppedAtUtc = DateTime.UtcNow;
                    Log.Info("Process capture stopped");

                    double seconds = (double)writer.BytesWritten / CaptureFormat.AvgBytesPerSec;
                    Log.Info($"Recorded {TimeSpan.FromSeconds(seconds):g} of audio");
                    Log.Info($"Output: {outputPath}");

                    return new Result(
                        pid,
                        includeProcessTree,
                        CaptureFormat,
                        outputPath,
                        startedAtUtc,
                        stoppedAtUtc,
                        stoppedAtUtc - startedAtUtc,
                        firstQpc,
                        firstPacketAtUtc,
                        writer.BytesWritten);
                }
                finally
                {
                    writer.Dispose(); // idempotent: patches header if not completed
                }
            }
            finally
            {
                Marshal.ReleaseComObject(capture);
            }
        }
        finally
        {
            if (eventHandle != IntPtr.Zero)
            {
                Win32.CloseHandle(eventHandle);
            }
        }
    }
}
