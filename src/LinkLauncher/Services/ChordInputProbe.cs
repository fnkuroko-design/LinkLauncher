#if INPUT_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace LinkLauncher.Services;

// Temporary preview-only observation. Remove before final packaging.
internal static class ChordInputProbe
{
    private static readonly Queue<string> Pending = new();
    private static readonly long Started = Stopwatch.GetTimestamp();
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("LINKLAUNCHER_INPUT_PROBE");
    private static bool _flushQueued;

    internal static void Record(string message)
    {
        if (string.IsNullOrEmpty(LogPath)) return;
        try
        {
            double elapsed = Stopwatch.GetElapsedTime(Started).TotalMilliseconds;
            Pending.Enqueue($"{elapsed:F3}ms {message}");
            if (_flushQueued) return;
            _flushQueued = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Flush));
        }
        catch { }
    }

    private static void Flush()
    {
        _flushQueued = false;
        try
        {
            var lines = Pending.ToArray();
            Pending.Clear();
            if (lines.Length != 0) File.AppendAllLines(LogPath!, lines);
        }
        catch
        {
            Pending.Clear();
        }
    }
}
#endif
