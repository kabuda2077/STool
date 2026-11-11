namespace STool.Modules.LanTransfer;

internal static class LanMath
{
    /// <summary>字节数累加，溢出时停在 long.MaxValue 而不是回绕。</summary>
    public static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
