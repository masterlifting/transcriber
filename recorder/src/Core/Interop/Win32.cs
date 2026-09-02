using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalTranscriber.Core.Interop;

/// <summary>
/// Constants, structs, P/Invoke declarations and COM interfaces for the WASAPI
/// audio capture used by this prototype. All values verified against the
/// Windows SDK 10.0.26100.0 headers installed on this machine
/// (audioclient.h, mmdeviceapi.h, audioclientactivationparams.h,
/// functiondiscoverykeys_devpkey.h).
/// </summary>
public static class Win32
{
    // ---------------- device & COM constants ----------------
    public const int DEVICE_STATE_ACTIVE = 0x00000001;
    public const int EDataFlow_eRender = 0;
    public const int EDataFlow_eCapture = 1;
    public const int ERole_eConsole = 0;
    public const uint CLSCTX_ALL = 0x17;
    public const uint COINIT_MULTITHREADED = 0;
    public const int STGM_READ = 0;
    public const ushort VT_LPWSTR = 31;
    public const ushort VT_BLOB = 65;
    public const uint RPC_E_CHANGED_MODE = 0x80010106;

    // ---------------- WASAPI stream constants (audioclient.h) ----------------
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);

    // ---------------- wave format tags ----------------
    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    // ---------------- process-loopback activation (audioclientactivationparams.h) ----------------
    public const int AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0;
    public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    public const int PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;
    public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = @"VAD\Process_Loopback";

    // ---------------- wait constants ----------------
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;
    public const uint INFINITE = 0xFFFFFFFF;

    // ---------------- GUIDs (verified against local SDK headers) ----------------
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IMMDeviceCollection = new("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E");
    public static readonly Guid IID_IMMDevice = new("D666063F-1587-4E43-81F1-B948E807363F");
    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IID_IActivateAudioInterfaceAsyncOperation = new("72A22D78-CDE4-431D-B8CC-843A71199B6D");
    public static readonly Guid IID_IActivateAudioInterfaceCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");

    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00AA00389B71");

    // ---------------- property keys (functiondiscoverykeys_devpkey.h) ----------------
    public static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    public static readonly PROPERTYKEY PKEY_Device_DeviceDesc = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);

    // ---------------- structs ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    /// <summary>24-byte layout: vt + 3 reserved + 16-byte union (x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p1; // union start (e.g. pwszVal for VT_LPWSTR)
        public IntPtr p2;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        [FieldOffset(0)] public uint TargetProcessId;
        [FieldOffset(4)] public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        [FieldOffset(0)] public int ActivationType;
        [FieldOffset(4)] public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    /// <summary>PROPVARIANT carrying a VT_BLOB (used for activation params).</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    // ---------------- P/Invoke ----------------
    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(
        [In] ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("mmdevapi.dll", ExactSpelling = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] ref PROPVARIANT_BLOB activationParams,
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
        [MarshalAs(UnmanagedType.Interface)] out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    public static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out PROCESS_BASIC_INFORMATION processInformation,
        uint processInformationLength,
        out uint returnLength);

    // ---------------- COM interfaces ----------------
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(
            int dataFlow,
            int deviceStateMask,
            [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection devices);

        [PreserveSig] int GetDefaultAudioEndpoint(
            int dataFlow,
            int role,
            [MarshalAs(UnmanagedType.Interface)] out IMMDevice endpoint);

        [PreserveSig] int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string pwstrId,
            [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);

        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(
            [In] ref Guid iid,
            uint clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);

        [PreserveSig] int OpenPropertyStore(int stgmAccess, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue([In] ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue([In] ref PROPERTYKEY key, [In] ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(
            int shareMode,
            int streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            IntPtr pFormat,
            IntPtr audioSessionGuid);

        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(
            [In] ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(
            out IntPtr data,
            out uint framesAvailable,
            out uint flags,
            out ulong devicePosition,
            out ulong qpcPosition);

        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint framesAvailable);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(
            [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    // ---------------- helpers ----------------
    public static class HResult
    {
        /// <summary>Throws when hr indicates failure, keeping the raw HRESULT in the message.</summary>
        public static int Check(int hr, string operation)
        {
            if (hr < 0)
            {
                string detail;
                try
                {
                    detail = Marshal.GetExceptionForHR(hr)?.Message ?? "unknown error";
                }
                catch
                {
                    detail = "unknown error";
                }
                throw new InvalidOperationException(
                    $"{operation} failed with HRESULT 0x{hr:X8} ({detail})");
            }
            return hr;
        }
    }
}
