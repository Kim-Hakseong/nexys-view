using System.Runtime.InteropServices;

namespace NexysView.App.Services;

/// <summary>Explicit AppUserModelID so taskbar pinning / Alt+Tab grouping and the icon are stable (design-pass §4).
/// With an explicit, unregistered AUMID the Windows 11 taskbar does not take the icon from the window or the exe
/// (both verified correct, design-report §2); it reads the window's AppUserModel property store, so
/// <see cref="ApplyWindow"/> publishes ID + relaunch command / display name / icon resource there.</summary>
public static class AppId
{
    public const string Id = "Nexys.NexysView";
    static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    const uint PidRelaunchCommand = 2, PidRelaunchIconResource = 3, PidRelaunchDisplayNameResource = 4, PidId = 5;

    [DllImport("shell32.dll", SetLastError = true)] static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    public static void Apply()
    {
        if (Environment.GetEnvironmentVariable("NEXYS_NO_AUMID") == "1") return;
        try { SetCurrentProcessExplicitAppUserModelID(Id); } catch { }
    }

    static void SetString(IPropertyStore store, uint pid, string value)
    {
        var key = new PROPERTYKEY { fmtid = AppUserModel, pid = pid };
        var v = new PROPVARIANT { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(value) };
        store.SetValue(ref key, ref v);
        PropVariantClear(ref v);
    }

    /// <summary>Call once the window handle exists (SourceInitialized).</summary>
    public static void ApplyWindow(IntPtr hwnd, string displayName)
    {
        if (Environment.GetEnvironmentVariable("NEXYS_NO_AUMID") == "1") return;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return;
            string exe = Environment.ProcessPath ?? "";
            SetString(store, PidId, Id);
            SetString(store, PidRelaunchCommand, "\"" + exe + "\"");
            SetString(store, PidRelaunchDisplayNameResource, displayName);
            SetString(store, PidRelaunchIconResource, exe + ",0");
            store.Commit();
            Marshal.ReleaseComObject(store);
        }
        catch { }
    }
}
