using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NexysView.App.Plugins;

// Minimal Windows Script Engine (IActiveScript) hosting for VBScript, used by VbsDataPlugin.

[ComImport, Guid("BB1A2AE1-A4F9-11cf-8F20-00805F2CD064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActiveScript
{
    void SetScriptSite(IActiveScriptSite site);
    void GetScriptSite(ref Guid riid, out IntPtr site);
    void SetScriptState(uint state);
    void GetScriptState(out uint state);
    void Close();
    void AddNamedItem([MarshalAs(UnmanagedType.LPWStr)] string name, uint flags);
    void AddTypeLib(ref Guid typeLib, uint major, uint minor, uint flags);
    void GetScriptDispatch([MarshalAs(UnmanagedType.LPWStr)] string? itemName, [MarshalAs(UnmanagedType.IDispatch)] out object dispatch);
    void GetCurrentScriptThreadID(out uint thread);
    void GetScriptThreadID(uint win32Thread, out uint thread);
    void GetScriptThreadState(uint thread, out uint state);
    void InterruptScriptThread(uint thread, IntPtr excepInfo, uint flags);
    void Clone(out IActiveScript script);
}

[ComImport, Guid("DB01A1E3-A42B-11cf-8F20-00805F2CD064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActiveScriptSite
{
    void GetLCID(out uint lcid);
    // raw pointers: the engine passes NULL for the pointer it does not request (an [out] stub would return E_POINTER)
    [PreserveSig] int GetItemInfo([MarshalAs(UnmanagedType.LPWStr)] string name, uint returnMask, IntPtr ppUnkItem, IntPtr ppTypeInfo);
    void GetDocVersionString([MarshalAs(UnmanagedType.BStr)] out string version);
    void OnScriptTerminate(IntPtr result, IntPtr excepInfo);
    void OnStateChange(uint state);
    void OnScriptError(IActiveScriptError error);
    void OnEnterScript();
    void OnLeaveScript();
}

[ComImport, Guid("EAE1BA61-A4ED-11cf-8F20-00805F2CD064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActiveScriptError
{
    void GetExceptionInfo(out EXCEPINFO info);
    void GetSourcePosition(out uint context, out uint line, out int column);
    void GetSourceLineText([MarshalAs(UnmanagedType.BStr)] out string text);
}

[ComImport, Guid("C7EF7658-E1EE-480E-97EA-D52CB4D76D17"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActiveScriptParse64
{
    void InitNew();
    void AddScriptlet([MarshalAs(UnmanagedType.LPWStr)] string defaultName, [MarshalAs(UnmanagedType.LPWStr)] string code, [MarshalAs(UnmanagedType.LPWStr)] string itemName, [MarshalAs(UnmanagedType.LPWStr)] string subItemName, [MarshalAs(UnmanagedType.LPWStr)] string eventName, [MarshalAs(UnmanagedType.LPWStr)] string delimiter, ulong sourceContext, uint startingLine, uint flags, [MarshalAs(UnmanagedType.BStr)] out string name, out EXCEPINFO excepInfo);
    void ParseScriptText([MarshalAs(UnmanagedType.LPWStr)] string code, [MarshalAs(UnmanagedType.LPWStr)] string? itemName, IntPtr context, [MarshalAs(UnmanagedType.LPWStr)] string? delimiter, ulong sourceContext, uint startingLine, uint flags, IntPtr result, IntPtr excepInfo);
}

public sealed class VbsScriptException : Exception { public VbsScriptException(string m) : base(m) { } }

/// <summary>Hosts one VBScript engine with named COM-visible objects; runs code and invokes Subs by name.</summary>
public sealed class VbsScriptHost : IActiveScriptSite, IDisposable
{
    const uint SCRIPTITEM_ISVISIBLE = 0x2, SCRIPTITEM_ISSOURCE = 0x4, SCRIPTITEM_GLOBALMEMBERS = 0x8;
    const uint SCRIPTSTATE_CONNECTED = 2, SCRIPTSTATE_CLOSED = 4;
    static readonly Guid VbScript = new("B54F3741-5B07-11cf-A4B0-00AA004A55E8");
    readonly IActiveScript _engine;
    readonly IActiveScriptParse64 _parse;
    readonly Dictionary<string, object> _items = new(StringComparer.OrdinalIgnoreCase);
    string? _lastError;

    public VbsScriptHost()
    {
        var t = Type.GetTypeFromCLSID(VbScript) ?? throw new VbsScriptException("VBScript engine not available");
        _engine = (IActiveScript)Activator.CreateInstance(t)!;
        _parse = (IActiveScriptParse64)_engine;
        _parse.InitNew();
        _engine.SetScriptSite(this);
    }

    public void AddObject(string name, object comVisible, bool globalMembers = false)
    {
        _items[name] = comVisible;
        _engine.AddNamedItem(name, SCRIPTITEM_ISVISIBLE | (globalMembers ? SCRIPTITEM_GLOBALMEMBERS : 0));
    }

    public void Run(string code)
    {
        _lastError = null;
        try { _parse.ParseScriptText(code, null, IntPtr.Zero, null, 0, 1, 0, IntPtr.Zero, IntPtr.Zero); }
        catch (COMException ex) { throw new VbsScriptException(_lastError ?? ex.Message); }
        _engine.SetScriptState(SCRIPTSTATE_CONNECTED);
        if (_lastError != null) throw new VbsScriptException(_lastError);
    }

    public object? Invoke(string sub, params object[] args)
    {
        _lastError = null;
        _engine.GetScriptDispatch(null, out var disp);
        try { return disp.GetType().InvokeMember(sub, System.Reflection.BindingFlags.InvokeMethod, null, disp, args); }
        catch (Exception ex) when (ex is COMException || ex.InnerException is COMException || ex is System.Reflection.TargetInvocationException)
        {
            throw new VbsScriptException(_lastError ?? (ex.InnerException ?? ex).Message);
        }
    }

    // ---- IActiveScriptSite
    public void GetLCID(out uint lcid) => lcid = 1033;
    public int GetItemInfo(string name, uint mask, IntPtr ppUnkItem, IntPtr ppTypeInfo)
    {
        if (ppTypeInfo != IntPtr.Zero) Marshal.WriteIntPtr(ppTypeInfo, IntPtr.Zero);
        if ((mask & 1) != 0)
        {
            if (!_items.TryGetValue(name, out var o)) return unchecked((int)0x80020006); // TYPE_E_ELEMENTNOTFOUND
            if (ppUnkItem != IntPtr.Zero) Marshal.WriteIntPtr(ppUnkItem, Marshal.GetIUnknownForObject(o));
        }
        return (mask & 2) != 0 ? unchecked((int)0x80020006) : 0;
    }
    public void GetDocVersionString(out string v) => v = "1";
    public void OnScriptTerminate(IntPtr r, IntPtr e) { }
    public void OnStateChange(uint s) { }
    public void OnScriptError(IActiveScriptError error)
    {
        try
        {
            error.GetExceptionInfo(out var info);
            error.GetSourcePosition(out _, out var line, out var col);
            string src = ""; try { error.GetSourceLineText(out src); } catch { }
            _lastError = $"{info.bstrDescription} [hr=0x{(uint)info.scode:X8} code={info.wCode} src={info.bstrSource}] (line {line + 1}, col {col}): {src?.Trim()}";
        }
        catch { _lastError = "script error"; }
    }
    public void OnEnterScript() { }
    public void OnLeaveScript() { }

    public void Dispose()
    {
        try { _engine.SetScriptState(SCRIPTSTATE_CLOSED); _engine.Close(); } catch { }
        Marshal.FinalReleaseComObject(_engine);
    }
}
