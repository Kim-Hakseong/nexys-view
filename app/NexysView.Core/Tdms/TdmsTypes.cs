namespace NexysView.Core.Tdms;

/// <summary>TDMS data type ids (the vendor "TDMS File Format Internal Structure").</summary>
public enum TdsType : uint
{
    Void = 0, I8 = 1, I16 = 2, I32 = 3, I64 = 4, U8 = 5, U16 = 6, U32 = 7, U64 = 8,
    Single = 9, Double = 10, Extended = 11, SingleUnit = 0x19, DoubleUnit = 0x1A,
    String = 0x20, Boolean = 0x21, Timestamp = 0x44, FixedPoint = 0x4F,
    ComplexSingle = 0x08000C, ComplexDouble = 0x10000D, DaqmxRaw = 0xFFFFFFFF,
}

[Flags]
public enum Toc : uint
{
    MetaData = 1 << 1, NewObjList = 1 << 2, RawData = 1 << 3,
    Interleaved = 1 << 5, BigEndian = 1 << 6, DaqmxRaw = 1 << 7,
}

/// <summary>1904-epoch timestamp: seconds since 1904-01-01 UTC + 2^-64 fractions.</summary>
public readonly record struct TdmsTimestamp(long Seconds, ulong Fraction)
{
    public static readonly DateTime Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public DateTime ToDateTime() => Epoch.AddSeconds(Seconds).AddTicks((long)(Fraction / 18446744073709551616.0 * 1e7));
    public double ToUnixSeconds() => Seconds - 2082844800.0 + Fraction / 18446744073709551616.0;
    public static TdmsTimestamp FromDateTime(DateTime dt)
    {
        var d = dt.ToUniversalTime() - Epoch;
        long sec = (long)Math.Floor(d.TotalSeconds);
        double frac = d.TotalSeconds - sec;
        return new TdmsTimestamp(sec, (ulong)(frac * 18446744073709551616.0));
    }
    public override string ToString() => ToDateTime().ToString("O");
}

public static class TdsTypes
{
    public static int Size(TdsType t) => t switch
    {
        TdsType.I8 or TdsType.U8 or TdsType.Boolean => 1,
        TdsType.I16 or TdsType.U16 => 2,
        TdsType.I32 or TdsType.U32 or TdsType.Single or TdsType.SingleUnit => 4,
        TdsType.I64 or TdsType.U64 or TdsType.Double or TdsType.DoubleUnit or TdsType.ComplexSingle => 8,
        TdsType.Timestamp or TdsType.ComplexDouble => 16,
        _ => 0,
    };

    public static bool IsNumeric(TdsType t) => t is TdsType.I8 or TdsType.I16 or TdsType.I32 or TdsType.I64 or TdsType.U8
        or TdsType.U16 or TdsType.U32 or TdsType.U64 or TdsType.Single or TdsType.Double or TdsType.SingleUnit or TdsType.DoubleUnit or TdsType.Boolean;

    public static string Name(TdsType t) => t switch
    {
        TdsType.Single or TdsType.SingleUnit => "SGL", TdsType.Double or TdsType.DoubleUnit => "DBL",
        TdsType.DaqmxRaw => "DAQmxRaw", _ => t.ToString(),
    };
}
