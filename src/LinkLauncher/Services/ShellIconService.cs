using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkLauncher.Models;

namespace LinkLauncher.Services;

internal readonly record struct ShellIconRequest(string Path, LinkKind Kind)
{
    internal string Key => ((int)Kind).ToString() + ":" + Path;

    internal static ShellIconRequest? Create(string target, LinkKind kind)
    {
        if (kind == LinkKind.Web || string.IsNullOrWhiteSpace(target) || !System.IO.Path.IsPathFullyQualified(target))
            return null;
        try { return new ShellIconRequest(System.IO.Path.GetFullPath(target), kind); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

/// <summary>Lazy, bounded, in-memory icon loading. No directory traversal, watcher, or web requests.</summary>
internal sealed class ShellIconService : IDisposable
{
    private const int CacheLimit = 256;
    private const int QueueLimit = 64;
    private readonly object _gate = new();
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Dictionary<string, Work> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Work> _queue = new(QueueLimit);
    private readonly Func<ShellIconRequest, ImageSource?> _load;
    private bool _started;
    private bool _disposed;
    private int _generation;

    internal ShellIconService(Func<ShellIconRequest, ImageSource?>? load = null) => _load = load ?? WindowsShellIcon.Load;

    internal Task<ImageSource?> GetAsync(string target, LinkKind kind)
    {
        ShellIconRequest? request = ShellIconRequest.Create(target, kind);
        if (request == null) return Task.FromResult<ImageSource?>(null);
        string key = request.Value.Key;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<ImageSource?>(null);
            if (_cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);
            if (_pending.TryGetValue(key, out Work? existing)) return existing.Completion.Task;
            var work = new Work(request.Value, _generation);
            if (!_queue.TryAdd(work)) return Task.FromResult<ImageSource?>(null);
            _pending.Add(key, work);
            if (!_started)
            {
                _started = true;
                var worker = new Thread(Run) { IsBackground = true, Name = "LinkLauncher shell icons" };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            return work.Completion.Task;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _cache.Clear();
            _cacheOrder.Clear();
            foreach (Work work in _pending.Values) work.Completion.TrySetResult(null);
            _pending.Clear();
            while (_queue.TryTake(out _)) { }
        }
    }

    private void Run()
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 2); // COINIT_APARTMENTTHREADED
        try
        {
            foreach (Work work in _queue.GetConsumingEnumerable())
            {
                lock (_gate) { if (_disposed) break; if (work.Generation != _generation) continue; }
                ImageSource? icon = null;
                try
                {
                    if (initialized >= 0) icon = _load(work.Request);
                    if (icon != null && !icon.IsFrozen)
                    {
                        if (icon.CanFreeze) icon.Freeze();
                        else icon = null;
                    }
                }
                catch (Exception) { icon = null; } // Shell handlers and inaccessible targets must not break the list.
                lock (_gate)
                {
                    if (_disposed || work.Generation != _generation) continue;
                    if (_pending.TryGetValue(work.Request.Key, out var current) && ReferenceEquals(current, work))
                        _pending.Remove(work.Request.Key);
                    if (_cache.Count == CacheLimit) _cache.Remove(_cacheOrder.Dequeue());
                    _cache.Add(work.Request.Key, icon);
                    _cacheOrder.Enqueue(work.Request.Key);
                    work.Completion.TrySetResult(icon);
                }
            }
        }
        finally
        {
            if (initialized >= 0) CoUninitialize();
            lock (_gate) { _queue.Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
            _queue.CompleteAdding();
            // Do not join: a slow third-party Shell handler must not delay application exit.
            if (!_started) _queue.Dispose();
        }
    }

    private sealed record Work(ShellIconRequest Request, int Generation)
    {
        internal TaskCompletionSource<ImageSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}

internal static class WindowsShellIcon
{
    private const uint Icon = 0x100;
    private const uint UseAttributes = 0x10;
    private const uint Normal = 0x80;
    private const uint Directory = 0x10;

    internal static ImageSource? Load(ShellIconRequest request)
    {
        string root = Path.GetPathRoot(request.Path) ?? "";
        uint drive = request.Path.StartsWith(@"\\", StringComparison.Ordinal) ? 4 : GetDriveType(root);
        if (drive is 2 or 3 or 6)
        {
            ImageSource? actual = Read(request.Path, 0, Icon);
            if (actual != null) return actual;
        }
        // For unavailable and network paths, ask for the registered type without accessing that path.
        string extension = Path.GetExtension(request.Path);
        string typePath = request.Kind == LinkKind.Folder ? "folder" : "file" + extension;
        return Read(typePath, request.Kind == LinkKind.Folder ? Directory : Normal, Icon | UseAttributes);
    }

    private static ImageSource? Read(string path, uint attributes, uint flags)
    {
        var info = new ShellFileInfo();
        try
        {
            if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags) == UIntPtr.Zero ||
                info.Icon == IntPtr.Zero) return null;
            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal IntPtr Icon;
        internal int IconIndex;
        internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern UIntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint bytes, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string root);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
