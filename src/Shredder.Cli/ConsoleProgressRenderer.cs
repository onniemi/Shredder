using System.Globalization;
using Shredder.Core.Models;

namespace Shredder.Cli;

/// <summary>
/// 控制台进度渲染器:把 <see cref="ShredProgress"/> 节流后用 <c>\r</c> 重写当前行。
/// <list type="bullet">
///   <item>Quiet 模式下只在 <see cref="BeginJob"/> / <see cref="EndJob"/> 输出关键里程碑,不画进度条。</item>
///   <item>节流间隔默认 150ms,避免在 NVMe 上每个 chunk 触发 stdout 写,导致进度本身成为瓶颈。</item>
///   <item>实现 <see cref="IDisposable"/> 用于 EndJob 之后还原光标到下一行。</item>
/// </list>
/// </summary>
internal sealed class ConsoleProgressRenderer : IProgress<ShredProgress>, IDisposable
{
    private readonly bool _quiet;
    private readonly int _throttleMs;
    private readonly object _gate = new();
    private long _lastTickMs;
    private int _lastLineLen;
    private bool _inJob;
    private string _currentLabel = string.Empty;

    public ConsoleProgressRenderer(bool quiet, int throttleMs = 150)
    {
        _quiet = quiet;
        _throttleMs = Math.Max(50, throttleMs);
    }

    public void BeginJob(string label)
    {
        lock (_gate)
        {
            FinalizeCurrentLine();
            _currentLabel = label ?? string.Empty;
            _inJob = true;
            _lastTickMs = 0;
            _lastLineLen = 0;
            if (_quiet) return;
            Console.WriteLine();
            Console.WriteLine($"▶ {_currentLabel}");
        }
    }

    public void EndJob(bool success, string? message)
    {
        lock (_gate)
        {
            FinalizeCurrentLine();
            _inJob = false;
            if (success)
            {
                Console.WriteLine($"  [OK] {_currentLabel}");
            }
            else
            {
                Console.WriteLine($"  [FAIL] {_currentLabel}{(string.IsNullOrEmpty(message) ? string.Empty : " — " + message)}");
            }
        }
    }

    public void Report(ShredProgress value)
    {
        if (value is null) return;
        if (_quiet) return;
        lock (_gate)
        {
            if (!_inJob) return;
            var now = Environment.TickCount64;
            if (_lastTickMs != 0 && now - _lastTickMs < _throttleMs) return;
            _lastTickMs = now;

            var line = FormatLine(value);
            // 写入前先用空格覆盖上次更长的尾巴
            var pad = Math.Max(0, _lastLineLen - line.Length);
            Console.Write('\r');
            Console.Write(line);
            if (pad > 0) Console.Write(new string(' ', pad));
            _lastLineLen = line.Length;
        }
    }

    internal static string FormatLine(ShredProgress p)
    {
        double pct = p.TotalBytes > 0
            ? Math.Clamp((double)p.BytesWritten / p.TotalBytes * 100.0, 0, 100)
            : 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "  pass {0}/{1}  {2,6:0.0}%  {3} / {4}",
            p.PassIndex, Math.Max(1, p.PassCount),
            pct,
            FormatBytes(p.BytesWritten),
            FormatBytes(p.TotalBytes));
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", v, units[i]);
    }

    private void FinalizeCurrentLine()
    {
        if (_lastLineLen == 0) return;
        // 用空格覆盖剩余字符再回车,留一个干净的行供后续 WriteLine
        Console.Write('\r');
        Console.Write(new string(' ', _lastLineLen));
        Console.Write('\r');
        _lastLineLen = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            FinalizeCurrentLine();
        }
    }
}
