using System.Runtime.InteropServices;
using Transcriber.Core.Interop;

namespace Transcriber.Core.Audio;

public sealed class AudioEndpointInfo
{
    public int Index { get; init; }
    public string Id { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string DeviceDesc { get; init; } = "";
    public bool IsDefault { get; init; }
}

/// <summary>
/// WASAPI capture endpoint enumeration and resolution (IMMDeviceEnumerator).
/// Devices are selected by stable endpoint ID or friendly name, never by
/// fragile array index in the internal implementation.
/// </summary>
public static class AudioEndpointEnumerator
{
    /// <summary>
    /// COM (MTA) must be initialized on the calling thread for WASAPI device
    /// enumeration. Initializes when needed and never uninitializes a
    /// pre-existing apartment (RPC_E_CHANGED_MODE).
    /// </summary>
    private static (bool initialized, int hr) EnsureCom()
    {
        int hr = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED);
        return (hr == 0, hr);
    }

    public static List<AudioEndpointInfo> ListCaptureEndpoints()
    {
        var (comInitialized, hr) = EnsureCom();
        if (hr != 0 && hr != unchecked((int)Win32.RPC_E_CHANGED_MODE))
        {
            Win32.HResult.Check(hr, "CoInitializeEx");
        }
        var list = new List<AudioEndpointInfo>();
        try
        {
            var enumerator = CreateEnumerator();
            try
            {
            string? defaultId = GetDefaultCaptureId(enumerator);
            Win32.HResult.Check(
                enumerator.EnumAudioEndpoints(Win32.EDataFlow_eCapture, Win32.DEVICE_STATE_ACTIVE, out var devices),
                "IMMDeviceEnumerator::EnumAudioEndpoints");
            try
            {
                Win32.HResult.Check(devices.GetCount(out uint count), "IMMDeviceCollection::GetCount");
                for (uint i = 0; i < count; i++)
                {
                    Win32.HResult.Check(devices.Item(i, out var device), $"IMMDeviceCollection::Item({i})");
                    try
                    {
                        list.Add(ReadInfo(device, (int)i, defaultId));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devices);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return list;
        }
        finally
        {
            if (comInitialized)
            {
                Win32.CoUninitialize();
            }
        }
    }

    /// <summary>
    /// Resolves "default", an exact endpoint ID, or a friendly-name substring.
    /// </summary>
    public static (Win32.IMMDevice Device, AudioEndpointInfo Info) ResolveCaptureDevice(string selector)
    {
        var (comInitialized, hr) = EnsureCom();
        if (hr != 0 && hr != unchecked((int)Win32.RPC_E_CHANGED_MODE))
        {
            Win32.HResult.Check(hr, "CoInitializeEx");
        }
        try
        {
        var enumerator = CreateEnumerator();
        try
        {
            if (selector.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                Win32.HResult.Check(
                    enumerator.GetDefaultAudioEndpoint(Win32.EDataFlow_eCapture, Win32.ERole_eConsole, out var def),
                    "IMMDeviceEnumerator::GetDefaultAudioEndpoint");
                return (def, ReadInfo(def, -1, null));
            }

            // Try exact endpoint ID first.
            try
            {
                Win32.HResult.Check(enumerator.GetDevice(selector, out var byId), "IMMDeviceEnumerator::GetDevice");
                return (byId, ReadInfo(byId, -1, null));
            }
            catch (InvalidOperationException)
            {
                // Not a device id; fall through to friendly-name matching.
            }

            var all = ListCaptureEndpoints();
            var matches = all
                .Where(e => e.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Microphone not found: '{selector}'. Available input devices:\n" +
                    string.Join("\n", all.Select(e => $"  [{e.Index}] {e.FriendlyName}")));
            }

            var chosen = matches
                .OrderByDescending(e => e.FriendlyName.Equals(selector, StringComparison.OrdinalIgnoreCase))
                .ThenBy(e => e.Index)
                .First();

            Win32.HResult.Check(enumerator.GetDevice(chosen.Id, out var dev), "IMMDeviceEnumerator::GetDevice");
            return (dev, ReadInfo(dev, chosen.Index, null));
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
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

    private static Win32.IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = Win32.CLSID_MMDeviceEnumerator;
        var iid = Win32.IID_IMMDeviceEnumerator;
        Win32.HResult.Check(
            Win32.CoCreateInstance(
                ref clsid,
                IntPtr.Zero,
                Win32.CLSCTX_ALL,
                ref iid,
                out object obj),
            "CoCreateInstance(MMDeviceEnumerator)");
        return (Win32.IMMDeviceEnumerator)obj;
    }

    private static string? GetDefaultCaptureId(Win32.IMMDeviceEnumerator enumerator)
    {
        int hr = enumerator.GetDefaultAudioEndpoint(Win32.EDataFlow_eCapture, Win32.ERole_eConsole, out var def);
        if (hr < 0 || def is null)
        {
            return null;
        }
        try
        {
            return ReadInfo(def, -1, null).Id;
        }
        finally
        {
            Marshal.ReleaseComObject(def);
        }
    }

    private static AudioEndpointInfo ReadInfo(Win32.IMMDevice device, int index, string? defaultId)
    {
        Win32.HResult.Check(device.GetId(out string id), "IMMDevice::GetId");

        string friendly = "";
        string desc = "";
        int hr = device.OpenPropertyStore(Win32.STGM_READ, out var store);
        if (hr == 0 && store is not null)
        {
            try
            {
                friendly = ReadPropertyString(store, Win32.PKEY_Device_FriendlyName);
                desc = ReadPropertyString(store, Win32.PKEY_Device_DeviceDesc);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        return new AudioEndpointInfo
        {
            Index = index,
            Id = id,
            FriendlyName = friendly,
            DeviceDesc = desc,
            IsDefault = defaultId is not null && id == defaultId,
        };
    }

    private static string ReadPropertyString(Win32.IPropertyStore store, Win32.PROPERTYKEY key)
    {
        var pv = new Win32.PROPVARIANT();
        int hr = store.GetValue(ref key, out pv);
        if (hr != 0)
        {
            return "";
        }
        try
        {
            if (pv.vt == Win32.VT_LPWSTR && pv.p1 != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(pv.p1) ?? "";
            }
            return "";
        }
        finally
        {
            Win32.PropVariantClear(ref pv);
        }
    }
}
