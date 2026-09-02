"""Print the default WASAPI render endpoint (friendly name) via ctypes.

Uses IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eConsole) and
IPropertyStore::GetValue(PKEY_Device_FriendlyName).
"""
import ctypes
from ctypes import wintypes

ole32 = ctypes.windll.ole32

CLSID_MMDeviceEnumerator = "BCDE0395-E52F-467C-8E3D-C4579291692E"
IID_IMMDeviceEnumerator = "A95664D2-9614-4F35-A746-DE8DB63617E6"
IID_IMMDevice = "D666063F-1587-4E43-81F1-B948E807363F"
IID_IPropertyStore = "886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"

FMTID_A45C254E = "A45C254E-DF1C-4EFD-8020-67D146A850E0"

eRender = 0
eConsole = 0
STGM_READ = 0
VT_LPWSTR = 31

HRESULT = ctypes.c_long
LPCWSTR = wintypes.LPCWSTR


def guid_to_bytes(guid_str):
    g = guid_str.strip("{}").split("-")
    return (
        bytes.fromhex(g[0])[::-1]
        + bytes.fromhex(g[1])[::-1]
        + bytes.fromhex(g[2])[::-1]
        + bytes.fromhex(g[3])
        + bytes.fromhex(g[4])
    )


# --- COM interfaces (minimal vtable + CFUNCTYPE delegates) ---

IUNKNOWN = [
    ("QueryInterface", ctypes.c_void_p),
    ("AddRef", ctypes.c_void_p),
    ("Release", ctypes.c_void_p),
]


class IMMDeviceEnumeratorVtbl(ctypes.Structure):
    _fields_ = IUNKNOWN + [
        ("EnumAudioEndpoints", ctypes.c_void_p),
        ("GetDefaultAudioEndpoint", ctypes.c_void_p),
        ("GetDevice", ctypes.c_void_p),
        ("RegisterEndpointNotificationCallback", ctypes.c_void_p),
        ("UnregisterEndpointNotificationCallback", ctypes.c_void_p),
    ]


class IMMDeviceEnumerator(ctypes.Structure):
    _fields_ = [("vtbl", ctypes.POINTER(IMMDeviceEnumeratorVtbl))]


class IMMDeviceVtbl(ctypes.Structure):
    _fields_ = IUNKNOWN + [
        ("Activate", ctypes.c_void_p),
        ("OpenPropertyStore", ctypes.c_void_p),
        ("GetId", ctypes.c_void_p),
        ("GetState", ctypes.c_void_p),
    ]


class IMMDevice(ctypes.Structure):
    _fields_ = [("vtbl", ctypes.POINTER(IMMDeviceVtbl))]


class IPropertyStoreVtbl(ctypes.Structure):
    _fields_ = IUNKNOWN + [
        ("GetCount", ctypes.c_void_p),
        ("GetAt", ctypes.c_void_p),
        ("GetValue", ctypes.c_void_p),
        ("SetValue", ctypes.c_void_p),
        ("Commit", ctypes.c_void_p),
    ]


class IPropertyStore(ctypes.Structure):
    _fields_ = [("vtbl", ctypes.POINTER(IPropertyStoreVtbl))]


class PROPERTYKEY(ctypes.Structure):
    _fields_ = [("fmtid", ctypes.c_ubyte * 16), ("pid", wintypes.DWORD)]


class PROPVARIANT(ctypes.Structure):
    _fields_ = [
        ("vt", wintypes.USHORT),
        ("wReserved1", wintypes.USHORT),
        ("wReserved2", wintypes.USHORT),
        ("wReserved3", wintypes.USHORT),
        ("p1", ctypes.c_void_p),
        ("p2", ctypes.c_void_p),
    ]


GDAE = ctypes.CFUNCTYPE(
    HRESULT, ctypes.POINTER(IMMDeviceEnumerator), ctypes.c_int, ctypes.c_int,
    ctypes.POINTER(ctypes.POINTER(IMMDevice)))
OPS = ctypes.CFUNCTYPE(
    HRESULT, ctypes.POINTER(IMMDevice), ctypes.c_int,
    ctypes.POINTER(ctypes.POINTER(IPropertyStore)))
GV = ctypes.CFUNCTYPE(
    HRESULT, ctypes.POINTER(IPropertyStore), ctypes.POINTER(PROPERTYKEY),
    ctypes.POINTER(PROPVARIANT))


def main():
    ole32.CoInitializeEx(None, 0)
    try:
        clsid = guid_to_bytes(CLSID_MMDeviceEnumerator)
        iid_enum = guid_to_bytes(IID_IMMDeviceEnumerator)
        ppv = ctypes.POINTER(ctypes.c_void_p)()
        hr = ole32.CoCreateInstance(clsid, None, 0x17, iid_enum, ctypes.byref(ppv))
        if hr < 0:
            print(f"CoCreateInstance failed HRESULT 0x{hr & 0xFFFFFFFF:08X}")
            return
        enum = ctypes.cast(ppv, ctypes.POINTER(IMMDeviceEnumerator))

        dev = ctypes.POINTER(IMMDevice)()
        hr = GDAE(enum.contents.vtbl[0].GetDefaultAudioEndpoint)(
            enum, eRender, eConsole, ctypes.byref(dev))
        if hr < 0:
            print(f"GetDefaultAudioEndpoint failed HRESULT 0x{hr & 0xFFFFFFFF:08X}")
            return
        device = dev

        store = ctypes.POINTER(IPropertyStore)()
        hr = OPS(device.contents.vtbl[0].OpenPropertyStore)(
            device, STGM_READ, ctypes.byref(store))
        if hr < 0:
            print(f"OpenPropertyStore failed HRESULT 0x{hr & 0xFFFFFFFF:08X}")
            return

        key = PROPERTYKEY()
        key.fmtid = (ctypes.c_ubyte * 16).from_buffer_copy(guid_to_bytes(FMTID_A45C254E))
        key.pid = 14  # PKEY_Device_FriendlyName

        pv = PROPVARIANT()
        hr = GV(store.contents.vtbl[0].GetValue)(store, ctypes.byref(key), ctypes.byref(pv))
        if hr < 0:
            print(f"GetValue failed HRESULT 0x{hr & 0xFFFFFFFF:08X}")
            return
        if pv.vt == VT_LPWSTR:
            print(f"default render: {ctypes.wstring_at(pv.p1)}")
        else:
            print(f"unexpected VT {pv.vt}")
    finally:
        ole32.CoUninitialize()


if __name__ == "__main__":
    main()
