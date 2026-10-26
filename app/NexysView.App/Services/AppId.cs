using System.Runtime.InteropServices;

namespace NexysView.App.Services;

/// <summary>Explicit AppUserModelID — OPT-IN ONLY (<c>NEXYS_AUMID=1</c>), off by default since 0.9.1.
/// Beta finding (Windows 11 26H2 build 26300, 2026-10-26, docs/beta-report.md "0.9.1"): with an explicit, unregistered AUMID
/// the taskbar button and Task Manager show the generic blank-document icon for both the portable and the installed build,
/// whatever the window's RelaunchIconResource says ("exe,0", "exe,-32512", "@exe,-32512", an .ico path, even shell32.dll,-16
/// were all ignored — work/beta/icons/cmp_probe3.png); removing the ID restores the exe icon at once. Without an explicit ID
/// Windows derives the AUMID from the exe path, which groups/pins correctly and shows the embedded icon (cmp_taskbar.png).
/// The property-store code is kept for diagnosis; <see cref="ApplyWindow"/> publishes ID + relaunch command / display name /
/// icon resource only when the opt-in is set.</summary>
public static class AppId
{
    /// <summary>True only with NEXYS_AUMID=1 (diagnostic opt-in); NEXYS_NO_AUMID=1 keeps its old meaning.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("NEXYS_AUMID") == "1" && Environment.GetEnvironmentVariable("NEXYS_NO_AUMID") != "1";

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
        if (!Enabled) return;
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
        if (!Enabled) return;
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
