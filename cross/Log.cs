using System;
using System.IO;

namespace AltSnip;

// 临时诊断日志（写到 %TEMP%/altsnip.log），定位覆盖层问题用。
static class Log
{
    static readonly string F = Path.Combine(Path.GetTempPath(), "altsnip.log");
    public static void Clear() { try { File.WriteAllText(F, ""); } catch { } }
    public static void W(string s)
    {
        try { File.AppendAllText(F, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine); } catch { }
    }
}
