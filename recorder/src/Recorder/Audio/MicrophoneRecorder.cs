using System.ComponentModel;
using System.Runtime.InteropServices;
using LocalTranscriber.Recorder.Diagnostics;
using LocalTranscriber.Core.Audio;
using LocalTranscriber.Core.Interop;

namespace LocalTranscriber.Recorder.Audio;

/// <summary>
/// Records a selected WASAPI capture endpoint (e.g. Shure MV6) to a WAV file
/// in shared mode at the endpoint's native mix format. Event-driven capture;
/// stops on the cancellation token.
/// </summary>
public static class MicrophoneRecorder
{
    public sealed record Result(
        string DeviceId,
        string DeviceName,
        AudioFormat Format,
        string OutputPath,
        DateTime StartedAtUtc,
        DateTime StoppedAtUtc,
        TimeSpan Duration,
        long FirstPacketQpc,
        DateTime FirstPacketAtUtc,
        long BytesWritten);

    public static Result Record(
        string deviceSelector,
        string outputPath,
        CancellationToken stopToken,
        double? testFailAfterSeconds = null)
    {
        int initHr = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED);
        bool comInitialized = initHr == 0;
        if (initHr != 0 && initHr != unchecked((int)Win32.RPC_E_CHANGED_MODE))
        {
            Win32.HResult.Check(initHr, "CoInitializeEx");
        }

        IntPtr eventHandle = IntPtr.Zero;
        try
        {
            var (device, info) = AudioEndpointEnumerator.ResolveCaptureDevice(deviceSelector);
            try
            {
                Log.Info($"Starting microphone capture: {info.FriendlyName} (id: {info.Id})");

                var iidClient = Win32.IID_IAudioClient;
                Win32.HResult.Check(
                    device.Activate(ref iidClient, Win32.CLSCTX_ALL, IntPtr.Zero, out object clientObj),
                    "IMMDevice::Activate(IAudioClient)");
                var audioClient = (Win32.IAudioClient)clientObj;
                try
                {
                    Win32.HResult.Check(audioClient.GetMixFormat(out IntPtr mixFormatPtr), "IAudioClient::GetMixFormat");
                    var format = AudioFormat.FromWaveFormatEx(mixFormatPtr);
                    IntPtr rawFormat = AudioFormat.CopyRaw(mixFormatPtr);
                    Win32.CoTaskMemFree(mixFormatPtr);
                    try
                    {
                        Win32.HResult.Check(
                            audioClient.Initialize(
                                Win32.AUDCLNT_SHAREMODE_SHARED,
                                Win32.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                                0,
                                0,
                                rawFormat,
                                IntPtr.Zero),
                            "IAudioClient::Initialize(shared, EVENTCALLBACK)");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(rawFormat);
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

                        Log.Info($"Capture format: {format}");

                        var writer = new WavFileWriter(outputPath, format);
                        byte[] scratch = new byte[bufferFrames * format.BlockAlign];
                        try
                        {
                            Win32.HResult.Check(audioClient.Start(), "IAudioClient::Start");
                            Log.Info("Microphone capture started");

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

                                    int bytes = (int)(frames * format.BlockAlign);
                                    if (!gotFirstPacket)
                                    {
                                        gotFirstPacket = true;
                                        firstQpc = (long)qpcPos;
                                        firstPacketAtUtc = DateTime.UtcNow;
                                    }
                                    Marshal.Copy(data, scratch, 0, bytes);
                                    Win32.HResult.Check(capture.ReleaseBuffer(frames), "IAudioCaptureClient::ReleaseBuffer");
                                    writer.Write(scratch, 0, bytes);

                                    // Diagnostic-only hook: simulate a mid-session mic
                                    // failure to exercise partial-failure metadata paths.
                                    if (testFailAfterSeconds is double tf &&
                                        (DateTime.UtcNow - startedAtUtc).TotalSeconds >= tf)
                                    {
                                        throw new InvalidOperationException(
                                            $"Test failure injected after {tf:0.0}s (--test-fail-after-seconds)");
                                    }
                                }
                            }

                            Log.Info("Stop requested");
                            Win32.HResult.Check(audioClient.Stop(), "IAudioClient::Stop");
                            writer.Complete();
                            DateTime stoppedAtUtc = DateTime.UtcNow;
                            Log.Info("Microphone capture stopped");

                            double seconds = (double)writer.BytesWritten / format.AvgBytesPerSec;
                            Log.Info($"Recorded {TimeSpan.FromSeconds(seconds):g} of audio");
                            Log.Info($"Output: {outputPath}");

                            return new Result(
                                info.Id,
                                info.FriendlyName,
                                format,
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
                    Marshal.ReleaseComObject(audioClient);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            if (eventHandle != IntPtr.Zero)
            {
                Win32.CloseHandle(eventHandle);
            }
            if (comInitialized)
            {
                Win32.CoUninitialize();
            }
        }
    }
}
