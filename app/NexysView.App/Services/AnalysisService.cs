using NexysView.App.Import;
using NexysView.Core.Analysis;
using NexysView.Core.Data;
using NexysView.Core.Formula;
using NexysView.Render;

namespace NexysView.App.Services;

/// <summary>Headless ANALYSIS: function + parameters + input channels → result channels in the "Analysis/Results" file, statistics,
/// notes, and a preview chart (Engine.Preview). Runs on the pool, cancellable, 60 s limit. Port of the WPF AnalysisPanel logic.</summary>
public sealed class AnalysisService
{
    public static readonly string[] Functions = { "Statistics", "FFT", "IIR filter", "FIR filter", "Derivative", "Integral", "Resample", "Formula channel" };
    /// <summary>Legacy names accepted by SelectFunction (tests, stress): IIR low-pass / IIR high-pass / FIR low-pass.</summary>
    static string Canon(string fn, Dictionary<string, string> p)
    {
        if (fn == "IIR low-pass") { p["iir_resp"] = "low-pass"; return "IIR filter"; }
        if (fn == "IIR high-pass") { p["iir_resp"] = "high-pass"; return "IIR filter"; }
        if (fn == "FIR low-pass") return "FIR filter";
        return fn;
    }
    bool High => Params.GetValueOrDefault("iir_resp", "low-pass") == "high-pass";
    public const int RunTimeoutSeconds = 60;
    readonly Engine _e;
    public string Function { get; private set; } = "Statistics";
    public Dictionary<string, string> Params { get; } = new();
    public List<string> Inputs { get; } = new();
    public List<string> Notes { get; } = new();
    public List<object> Results { get; } = new();     // result channel dtos of the last run
    public object? LastStats { get; private set; }
    public bool Running => _cts != null;
    public double LastRunMs { get; private set; }
    public int RunsCompleted, RunsFailed;
    CancellationTokenSource? _cts;
    DataFile? _results;
    readonly List<Channel> _lastOutputs = new();

    public AnalysisService(Engine e) { _e = e; }

    public void SelectFunction(string fn) { fn = Canon(fn, Params); if (Functions.Contains(fn)) Function = fn; _e.Touch(); }
    public void SetParam(string key, string value) { Params[key] = value; _e.Touch(); }
    public void SetInputs(IEnumerable<string> ids) { Inputs.Clear(); Inputs.AddRange(ids.Where(id => _e.Find(id) is { IsText: false })); _e.Touch(); }
    public void Cancel() { try { _cts?.Cancel(); } catch { } }
    public void OnFileClosed(DataFile f) { Inputs.RemoveAll(id => _e.Find(id) == null || _e.Find(id)!.FileName == f.Name); if (f == _results) { _results = null; Results.Clear(); _lastOutputs.Clear(); } }

    double P(string k, double d) => Params.TryGetValue(k, out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;

    /// <summary>Parameter fields of a function for the web form: key, label, unit, default, options.</summary>
    public static object[] Schema(string fn) => fn switch
    {
        "FFT" => new object[] { new { key = "fft_window", label = "WINDOW", unit = "", def = "Hann", options = new[] { "Hann", "Hamming", "Blackman", "Rect" } }, new { key = "fft_n", label = "FFT SIZE", unit = "", def = "4096", options = (string[]?)null } },
        "IIR filter" => new object[] { new { key = "iir_type", label = "TYPE", unit = "", def = "Biquad", options = new[] { "Biquad" } }, new { key = "iir_resp", label = "RESPONSE", unit = "", def = "low-pass", options = new[] { "low-pass", "high-pass" } },
                                       new { key = "iir_order", label = "ORDER", unit = "", def = "2", options = new[] { "2" } }, new { key = "iir_fc", label = "CUTOFF", unit = "Hz", def = "50", options = (string[]?)null },
                                       new { key = "_fs", label = "SAMPLE RATE", unit = "Hz", def = "", options = (string[]?)null }, new { key = "iir_q", label = "Q", unit = "", def = "0.7071", options = (string[]?)null } },
        "FIR filter" => new object[] { new { key = "fir_resp", label = "RESPONSE", unit = "", def = "low-pass", options = new[] { "low-pass" } }, new { key = "fir_fc", label = "CUTOFF", unit = "Hz", def = "100", options = (string[]?)null }, new { key = "fir_taps", label = "TAPS", unit = "", def = "101", options = (string[]?)null }, new { key = "_fs", label = "SAMPLE RATE", unit = "Hz", def = "", options = (string[]?)null } },
        "Resample" => new object[] { new { key = "rs_n", label = "SAMPLES", unit = "", def = "1000", options = (string[]?)null } },
        "Formula channel" => new object[] { new { key = "formula", label = "FORMULA", unit = "", def = "Ch(\"Results/y\") = 2 * Ch(\"$1\") + 1", options = (string[]?)null } },
        _ => Array.Empty<object>(),
    };

    /// <summary>Validity line for the parameter panel (fc &lt; fs/2, taps vs length …).</summary>
    public (bool ok, string text) Validity()
    {
        var inputs = Inputs.Select(_e.Find).Where(c => c != null).Select(c => c!).ToList();
        if (Function == "Formula channel") return (true, "formula · $1 = first input · Ch(\"Group/Name\")");
        if (inputs.Count == 0) return (false, "select an input channel");
        var ch = inputs[0];
        double fs = ch.XKind == XKind.Implicit && ch.XIncrement > 0 ? 1 / ch.XIncrement : double.NaN;
        try
        {
            if (Function.StartsWith("IIR")) { Dsp.ValidateFilter(fs, P("iir_fc", 50), ch.Length, 1, P("iir_q", 0.7071)); return (true, $"valid · fc < fs/2 · {ch.Length:N0} > padding 12"); }
            if (Function.StartsWith("FIR")) { Dsp.ValidateFilter(fs, P("fir_fc", 100), ch.Length, (int)P("fir_taps", 101)); return (true, $"valid · fc < fs/2 · {ch.Length:N0} > taps {(int)P("fir_taps", 101)}"); }
        }
        catch (ArgumentException ex) { return (false, ex.Message); }
        if (double.IsNaN(fs) && Function is "FFT" or "Derivative" or "Integral") return (true, "no x increment — x axis in samples (fs = 1)");
        return (true, $"ready · {inputs.Count} input{(inputs.Count > 1 ? "s" : "")} · {ch.Length:N0} samples");
    }

    DataFile ResultsFile()
    {
        if (_results == null || !_e.State.Files.Contains(_results))
        {
            _results = new DataFile("Analysis", "Results", new Dictionary<string, object>(), null);
            _results.Groups.Add(new ChannelGroup("Results", new Dictionary<string, object>()));
            _e.State.Files.Add(_results);
        }
        return _results;
    }

    sealed record ResultChannel(string Name, double[] Values, string Unit, Channel? Like, double? XInc, string XUnit, string Meta);

    Channel AddResult(ResultChannel rc)
    {
        var r = ResultsFile(); var g = r.Groups[0];
        g.Channels.RemoveAll(c => c.Name == rc.Name);
        while (g.Channels.Count >= 50) { var old = g.Channels[0]; _e.State.Pyramids.Cancel(old); g.Channels.RemoveAt(0); }   // keep the last 50 results
        var ch = new Channel(r.Name, g.Name, rc.Name, new ArraySource(rc.Values), new Dictionary<string, object> { ["source"] = rc.Like?.DisplayName ?? "", ["analysis"] = rc.Meta }) { Unit = rc.Unit, DataType = "DBL", XUnit = rc.XUnit };
        if (rc.XInc != null) { ch.XKind = XKind.Implicit; ch.XIncrement = rc.XInc.Value; ch.XStart = 0; }
        else if (rc.Like != null && rc.Like.XKind == XKind.Implicit) { ch.XKind = XKind.Implicit; ch.XIncrement = rc.Like.XIncrement; ch.XStart = rc.Like.XStart; ch.XUnit = rc.Like.XUnit; }
        g.Channels.Add(ch);
        return ch;
    }

    public string Subtitle()
    {
        return Function switch
        {
            "FFT" => $"{Params.GetValueOrDefault("fft_window", "Hann")} · N {Params.GetValueOrDefault("fft_n", "4096")}",
            "IIR filter" => $"Biquad · {(High ? "high-pass" : "low-pass")} · 2nd order · fc {Params.GetValueOrDefault("iir_fc", "50")} Hz · Q {Params.GetValueOrDefault("iir_q", "0.7071")}",
            "FIR filter" => $"windowed-sinc · low-pass · fc {Params.GetValueOrDefault("fir_fc", "100")} Hz · {Params.GetValueOrDefault("fir_taps", "101")} taps",
            "Resample" => $"linear · {Params.GetValueOrDefault("rs_n", "1000")} samples",
            "Formula channel" => Params.GetValueOrDefault("formula", ""),
            _ => "",
        };
    }

    /// <summary>Run the selected function on the current inputs. Returns true when results were produced.</summary>
    public async Task<bool> RunAsync(string? function = null)
    {
        if (function != null) SelectFunction(function);
        if (_cts != null) { Notes.Add("A run is still in progress — cancel it first."); _e.Touch(); return false; }
        Notes.Clear(); LastStats = null; _lastOutputs.Clear();
        string fn = Function; bool high = High;
        var inputs = Inputs.Select(_e.Find).Where(c => c != null).Select(c => c!).ToList();
        if (fn != "Formula channel" && inputs.Count == 0) { Notes.Add("Select or drop an input channel first."); _e.Touch(); return false; }
        double fc = P(fn.StartsWith("FIR") ? "fir_fc" : "iir_fc", fn.StartsWith("FIR") ? 100 : 50), q = P("iir_q", 0.7071), taps = P("fir_taps", 101), fftN = P("fft_n", 4096), rsN = P("rs_n", 1000);
        string winName = Params.GetValueOrDefault("fft_window", "Hann");
        string formula = Params.GetValueOrDefault("formula", "");
        var formulaInputs = inputs.ToList();
        var allChannels = _e.State.Files.SelectMany(f => f.AllChannels).ToList();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(RunTimeoutSeconds));
        var ct = _cts.Token;
        _e.SetLed("busy"); _e.Touch();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Diag.Trace($"analysis {fn} start inputs={inputs.Count}");
        var notes = new List<string>(); var outs = new List<ResultChannel>(); var stats = new List<(Channel, Stats)>();
        (string target, object? value)? formulaResult = null;
        bool ok = false;
        string meta = Subtitle();
        try
        {
            await Task.Run(() =>
            {
                if (fn == "Formula channel")
                {
                    var eng = new FormulaEngine();
                    eng.ChannelResolver = name =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (name.StartsWith("$") && int.TryParse(name[1..], out var k) && k >= 1 && k <= formulaInputs.Count) { var c = formulaInputs[k - 1]; var a = new double[c.Length]; c.Source.Read(0, a); return a; }
                        var ch = allChannels.FirstOrDefault(c => c.DisplayName == name || c.Name == name);
                        if (ch == null) return null;
                        var arr = new double[ch.Length]; ch.Source.Read(0, arr); return arr;
                    };
                    var r = eng.Evaluate(formula);
                    formulaResult = (r.Target, r.Value);
                    if (r.Target.StartsWith("Ch(", StringComparison.OrdinalIgnoreCase) && r.Value is double[] vec)
                    {
                        string name = r.Target[4..^2].Split('/')[^1];
                        outs.Add(new ResultChannel(name, vec, "", formulaInputs.FirstOrDefault(), null, "s", "formula"));
                        notes.Add($"Channel {name}: {vec.Length:N0} values");
                    }
                    return;
                }
                foreach (var ch in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ch.Length > 50_000_000) throw new ArgumentException($"{ch.DisplayName}: {ch.Length:N0} samples exceed the 50,000,000 analysis limit — zoom/resample first.");
                    var x = new double[ch.Length]; ch.Source.Read(0, x);
                    double fs = ch.XKind == XKind.Implicit && ch.XIncrement > 0 ? 1 / ch.XIncrement : double.NaN;
                    double fsOr1 = double.IsNaN(fs) ? 1 : fs;
                    string ctx = ch.DisplayName + ": ";
                    if (double.IsNaN(fs) && fn is "FFT" or "Derivative" or "Integral") notes.Add($"{ch.DisplayName}: no x increment — x axis in samples (fs = 1).");
                    switch (fn)
                    {
                        case "Statistics": stats.Add((ch, Dsp.Statistics(x))); break;
                        case "FFT":
                            {
                                var win = Enum.Parse<Dsp.Window>(winName);
                                try { var (f, amp) = Dsp.AmplitudeSpectrum(x, fsOr1, win, (int)fftN); outs.Add(new ResultChannel(ch.Name + "_FFT", amp, ch.Unit, null, f.Length > 1 ? f[1] : 1, "Hz", $"FFT {win} · {amp.Length:N0} bins · {(f.Length > 1 ? Ticks.FormatValue(f[1], 3) : "1")} Hz")); int pk = Array.IndexOf(amp, amp.Max()); notes.Add($"{ch.Name}: peak {Ticks.FormatValue(amp[pk])} {ch.Unit} at {Ticks.FormatValue(f[pk])} Hz ({win}, N={amp.Length * 2 - 2})"); }
                                catch (ArgumentException ex) { throw new ArgumentException(ctx + ex.Message); }
                                break;
                            }
                        case "IIR filter":
                            {
                                try { Dsp.ValidateFilter(fs, fc, x.Length, 1, q); } catch (ArgumentException ex) { throw new ArgumentException(ctx + ex.Message); }
                                var (b, a) = Dsp.Biquad(high ? "highpass" : "lowpass", fc, fs, q);
                                outs.Add(new ResultChannel(ch.Name + (high ? "_HP" : "_LP"), Dsp.Lfilter(b, a, x, ct), ch.Unit, ch, null, ch.XUnit, $"IIR {(high ? "HP" : "LP")} {Ticks.FormatValue(fc, 4)} Hz")); break;
                            }
                        case "FIR filter":
                            {
                                try { Dsp.ValidateFilter(fs, fc, x.Length, (int)taps); } catch (ArgumentException ex) { throw new ArgumentException(ctx + ex.Message); }
                                var h = Dsp.FirLowpass(fc, fs, (int)taps); outs.Add(new ResultChannel(ch.Name + "_FIR", Dsp.Convolve(x, h, ct), ch.Unit, ch, null, ch.XUnit, $"FIR LP {Ticks.FormatValue(fc, 4)} Hz · {(int)taps} taps")); break;
                            }
                        case "Derivative": try { outs.Add(new ResultChannel(ch.Name + "_d", Dsp.Derivative(x, 1 / fsOr1), ch.Unit + "/s", ch, null, ch.XUnit, "derivative")); } catch (ArgumentException ex) { throw new ArgumentException(ctx + ex.Message); } break;
                        case "Integral": try { outs.Add(new ResultChannel(ch.Name + "_int", Dsp.Integral(x, 1 / fsOr1), ch.Unit + "·s", ch, null, ch.XUnit, "integral")); } catch (ArgumentException ex) { throw new ArgumentException(ctx + ex.Message); } break;
                        case "Resample":
                            {
                                int n = (int)rsN; double[] y;
                                try { y = Dsp.ResampleLinear(x, n); } catch (ArgumentException ex) { throw new ArgumentException(ctx + ex.Message); }
                                outs.Add(new ResultChannel(ch.Name + "_rs", y, ch.Unit, null, ch.XKind == XKind.Implicit ? ch.XIncrement * (x.Length - 1) / Math.Max(1, n - 1) : null, ch.XUnit, $"resample {n:N0}")); break;
                            }
                    }
                }
            }, ct);
            ok = true;
        }
        catch (OperationCanceledException) { notes.Add(sw.Elapsed.TotalSeconds >= RunTimeoutSeconds ? $"Stopped: exceeded the {RunTimeoutSeconds} s limit." : "Cancelled."); RunsFailed++; }
        catch (Exception ex) { notes.Add("Error: " + ex.Message); RunsFailed++; Diag.Trace($"analysis {fn} error {ex.GetType().Name}: {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; }
        LastRunMs = sw.Elapsed.TotalMilliseconds;
        if (stats.Count > 0)
        {
            var (ch, s) = stats[0];
            LastStats = new { channel = ch.DisplayName, unit = ch.Unit, count = s.Count, min = s.Min, max = s.Max, mean = s.Mean, rms = s.Rms, std = s.Std, sum = s.Sum,
                all = stats.Select(t => new { channel = t.Item1.DisplayName, unit = t.Item1.Unit, count = t.Item2.Count, min = t.Item2.Min, max = t.Item2.Max, mean = t.Item2.Mean, rms = t.Item2.Rms, std = t.Item2.Std, sum = t.Item2.Sum }).ToList() };
        }
        var added = new List<Channel>();
        if (ok) foreach (var rc in outs)
        {
            var c = AddResult(rc); added.Add(c);
            Results.RemoveAll(r => ((dynamic)r).name == c.Name);
            Results.Insert(0, new { id = c.Id, name = c.Name, unit = c.Unit, length = c.Length, meta = rc.Meta });
            if (Results.Count > 12) Results.RemoveAt(Results.Count - 1);
        }
        if (ok && stats.Count == 0 && outs.Count > 0)
        {
            // stats of the (first) result channel (mockup: MIN/MAX/MEAN/RMS under the result plot)
            var s = Dsp.Statistics(outs[0].Values);
            LastStats = new { channel = outs[0].Name, unit = outs[0].Unit, count = s.Count, min = s.Min, max = s.Max, mean = s.Mean, rms = s.Rms, std = s.Std, sum = s.Sum, all = new List<object>() };
        }
        Notes.AddRange(notes);
        if (ok && fn == "Formula channel" && added.Count == 0 && formulaResult != null) Notes.Add($"{formulaResult.Value.target} = {(formulaResult.Value.value is double d ? Ticks.FormatValue(d, 15) : formulaResult.Value.value)}");
        // preview chart: first input (faded) + outputs
        var pm = _e.Preview.Model;
        pm.Curves.Clear(); pm.YAxes.Clear(); pm.NextColor = 0; pm.CursorA.Enabled = pm.CursorB.Enabled = false; pm.ClearXChannel();
        if (added.Count > 0)
        {
            _lastOutputs.AddRange(added);
            if (inputs.Count > 0 && fn != "FFT" && fn != "Resample") { var cin = pm.AddCurve(inputs[0]); cin.ColorIndex = 1; }
            foreach (var c in added) { var cv = pm.AddCurve(c); cv.ColorIndex = 0; _ = _e.State.EnsurePyramid(c).ContinueWith(_ => _e.TouchChart(0)); }
            pm.Title = fn; pm.ZoomFull();
            Notes.Add($"{added.Count} result channel(s) in Analysis/Results ({LastRunMs:F0} ms)");
            _e.State.FilesChanged -= Noop; // make sure tree refreshes
            _e.Touch();
        }
        if (ok) { RunsCompleted++; _e.SetStatus($"{fn} done in {LastRunMs:F0} ms" + (added.Count > 0 ? $" · {added.Count} result channel(s)" : "")); }
        _e.SetLed(_e.State.Files.Count > 0 ? "ready" : "idle");
        _e.TouchChart(0);
        Diag.Trace($"analysis {fn} end ok={ok} ({LastRunMs:F0} ms)");
        return ok;
    }
    static void Noop() { }

    /// <summary>"SHOW IN VIEW": a new VIEW chart with the last result channels.</summary>
    public int ShowInView()
    {
        if (_lastOutputs.Count == 0) return 0;
        var chart = _e.AddChart();
        foreach (var c in _lastOutputs) _e.AddCurve(chart.Id, c.Id, record: false);
        _e.Mode = "VIEW"; _e.Touch();
        return chart.Id;
    }

    public object Dto()
    {
        var (vok, vtext) = Validity();
        return new
        {
            functions = Functions.Select(f => new { name = f, meta = f == Function ? "active" : "" }).ToList(),
            function = Function, schema = Schema(Function), @params = Params, inputs = Inputs.Select(id => { var c = _e.Find(id); return new { id, name = c?.DisplayName ?? id, length = c?.Length ?? 0 }; }).ToList(),
            fs = Inputs.Select(_e.Find).FirstOrDefault() is { XKind: XKind.Implicit, XIncrement: > 0 } c0 ? 1 / c0.XIncrement : (double?)null,
            running = Running, notes = Notes, results = Results, stats = LastStats, lastRunMs = LastRunMs, subtitle = Subtitle(), validity = new { ok = vok, text = vtext },
            hasPreview = _e.Preview.Model.Curves.Count > 0, canShow = _lastOutputs.Count > 0,
        };
    }
}
