using System.Runtime.InteropServices;
using Transcriber.Recorder.Diagnostics;
using Transcriber.Core.Interop;

namespace Transcriber.Recorder.ProcessAudio;

/// <summary>
/// Native interop for Windows process-loopback activation:
/// ActivateAudioInterfaceAsync + AUDIOCLIENT_ACTIVATION_PARAMS
/// + AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS, per the current Microsoft
/// Application Loopback sample (Windows-classic-samples).
/// </summary>
public static class ProcessLoopbackInterop
{
    /// <summary>
    /// COM completion handler for ActivateAudioInterfaceAsync. Windows calls
    /// ActivateCompleted on an MTA worker thread when activation finishes.
    /// </summary>
    [ComVisible(true)]
    public sealed class ActivationHandler : Win32.IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Done = new(false);

        public volatile int ActivationHr = unchecked((int)0x80004005); // E_FAIL
        public volatile int GetActivateResultHr = unchecked((int)0x80004005);
        public object? ActivatedInterface;

        public int ActivateCompleted(Win32.IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                if (activateOperation is null)
                {
                    ActivationHr = unchecked((int)0x80004005); // E_FAIL
                    Log.Error("ActivateCompleted called with null operation");
                }
                else
                {
                    GetActivateResultHr = activateOperation.GetActivateResult(out int hr, out object? unk);
                    ActivationHr = hr;
                    ActivatedInterface = unk;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"ActivateCompleted threw: {ex.Message}");
                ActivationHr = unchecked((int)0x80004005);
            }
            finally
            {
                Done.Set();
            }
            return 0; // S_OK
        }
    }

    public sealed record ActivationResult(ActivationHandler Handler, IntPtr ActivationParamsPtr);

    /// <summary>
    /// Starts the async activation of the process-loopback virtual device for
    /// the given process tree. Caller must keep <see cref="ActivationResult.ActivationParamsPtr"/>
    /// alive until <see cref="ActivationHandler.Done"/> is set, then free it.
    /// </summary>
    public static ActivationResult ActivateProcessLoopback(uint pid, bool includeProcessTree)
    {
        var activationParams = new Win32.AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = Win32.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
        };
        activationParams.ProcessLoopbackParams.TargetProcessId = pid;
        activationParams.ProcessLoopbackParams.ProcessLoopbackMode = includeProcessTree
            ? Win32.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            : Win32.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;

        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Win32.AUDIOCLIENT_ACTIVATION_PARAMS>());
        Marshal.StructureToPtr(activationParams, paramsPtr, false);

        var pv = new Win32.PROPVARIANT_BLOB
        {
            vt = Win32.VT_BLOB,
            cbSize = (uint)Marshal.SizeOf<Win32.AUDIOCLIENT_ACTIVATION_PARAMS>(),
            pBlobData = paramsPtr,
        };

        var handler = new ActivationHandler();
        try
        {
            var iidClient = Win32.IID_IAudioClient;
            Win32.HResult.Check(
                Win32.ActivateAudioInterfaceAsync(
                    Win32.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                    ref iidClient,
                    ref pv,
                    handler,
                    out _),
                "ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK)");
        }
        catch
        {
            Marshal.FreeHGlobal(paramsPtr);
            throw;
        }

        return new ActivationResult(handler, paramsPtr);
    }
}
