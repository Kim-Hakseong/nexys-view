namespace NexysView.App.Export;

/// <summary>TDMS writer entry (writer ported in P6).</summary>
public static partial class TdmsExport
{
    static partial void SaveAllImpl(Host.AppState state, string path, ref int count);
    public static int SaveAll(Host.AppState state, string path) { int n = 0; SaveAllImpl(state, path, ref n); return n; }
}
