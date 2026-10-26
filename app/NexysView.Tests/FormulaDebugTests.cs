using NexysView.Core.Formula;
using Xunit;
using Xunit.Abstractions;

namespace NexysView.Tests;

public class FormulaDebugTests
{
    readonly ITestOutputHelper _out;
    public FormulaDebugTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Simple()
    {
        var e = new FormulaEngine();
        try { var r = e.Evaluate("R1 = 2 + 3 * 4"); _out.WriteLine("value=" + r.Value); Assert.Equal(14.0, (double)r.Value); }
        catch (Exception ex) { _out.WriteLine(ex.ToString()); throw; }
    }
}
