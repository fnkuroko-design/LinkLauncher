using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using LinkLauncher;
using LinkLauncher.Models;
using LinkLauncher.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: ShellIconChecks <repo> <new-check-directory>"); return 2; }
        string root = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Keep previous results; use a new directory.");
        Directory.CreateDirectory(output);
        int passed = 0;
        try
        {
            Check("Web/invalid targets never reach Shell", () =>
            {
                int calls = 0;
                using var service = new ShellIconService(_ => { calls++; return null; });
                Assert(Get(service, "https://example.com", LinkKind.Web) == null, "Web fallback");
                Assert(Get(service, "relative.exe", LinkKind.File) == null, "Relative path fallback");
                Assert(calls == 0, "No Shell requests");
            }, ref passed);

            Check("Nonblocking STA loading, deduplication and frozen cache", () =>
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                int calls = 0;
                ApartmentState apartment = ApartmentState.Unknown;
                using var service = new ShellIconService(_ =>
                {
                    Interlocked.Increment(ref calls);
                    apartment = Thread.CurrentThread.GetApartmentState();
                    entered.Set();
                    Assert(release.Wait(TimeSpan.FromSeconds(10)), "Release slow fake provider");
                    return Picture(Colors.Red);
                });
                try
                {
                    Task<ImageSource?> first = service.GetAsync(@"C:\test\app.exe", LinkKind.File);
                    Assert(entered.Wait(TimeSpan.FromSeconds(5)) && !first.IsCompleted, "Caller is not blocked by extraction");
                    Assert(ReferenceEquals(first, service.GetAsync(@"c:\TEST\app.exe", LinkKind.File)), "Share pending extraction");
                    release.Set();
                    ImageSource? icon = Await(first);
                    Assert(icon != null && icon.IsFrozen && apartment == ApartmentState.STA, "Thread-safe STA result");
                    Assert(ReferenceEquals(icon, Get(service, @"C:\test\app.exe", LinkKind.File)) && calls == 1, "Reuse cache");
                }
                finally { release.Set(); }
            }, ref passed);

            Check("Bounded cache and failure fallback", () =>
            {
                int calls = 0;
                using var service = new ShellIconService(_ => { calls++; return Picture(Colors.Blue); });
                for (int i = 0; i < 257; i++) Get(service, @"C:\test\" + i + ".exe", LinkKind.File);
                Get(service, @"C:\test\256.exe", LinkKind.File);
                Assert(calls == 257, "Retain recent result");
                Get(service, @"C:\test\0.exe", LinkKind.File);
                Assert(calls == 258, "Evict oldest beyond 256");
                int failures = 0;
                using var failing = new ShellIconService(_ => { failures++; throw new IOException("Unavailable"); });
                Assert(Get(failing, @"C:\missing\file.exe", LinkKind.File) == null, "Failure returns fallback");
                Get(failing, @"C:\missing\file.exe", LinkKind.File);
                Assert(failures == 1, "Negative cache prevents repeated reads");
            }, ref passed);

            Check("Editing during loading does not restore a stale icon", () =>
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                int calls = 0;
                using var service = new ShellIconService(_ =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); return Picture(Colors.Red); }
                    return Picture(Colors.Green);
                });
                try
                {
                    var old = service.GetAsync(@"C:\test\same.exe", LinkKind.File);
                    Assert(entered.Wait(TimeSpan.FromSeconds(5)), "Old request began");
                    service.Clear();
                    Assert(Await(old) == null, "Old request retired");
                    var current = service.GetAsync(@"C:\test\same.exe", LinkKind.File);
                    release.Set();
                    var icon = Await(current);
                    Assert(icon != null && Digest(icon) == Digest(Picture(Colors.Green)), "New result wins");
                    Assert(ReferenceEquals(icon, Get(service, @"C:\test\same.exe", LinkKind.File)), "Cache is current");
                }
                finally { release.Set(); }
            }, ref passed);

            Check("Closing does not wait for a slow Shell handler", () =>
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var service = new ShellIconService(_ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); return Picture(Colors.Red); });
                try
                {
                    var active = service.GetAsync(@"C:\test\active.exe", LinkKind.File);
                    Assert(entered.Wait(TimeSpan.FromSeconds(5)), "Provider entered");
                    var queued = Enumerable.Range(0, 80).Select(i => service.GetAsync(@"C:\test\queued" + i + ".exe", LinkKind.File)).ToList();
                    Assert(queued.Count(t => !t.IsCompleted) == 64, "Bound queue while handler is blocked");
                    service.Dispose();
                    Assert(active.IsCompleted && queued.All(t => t.IsCompleted), "Complete outstanding requests on close");
                    Assert(Get(service, @"C:\test\new.exe", LinkKind.File) == null, "Ignore requests after close");
                }
                finally { release.Set(); }
            }, ref passed);

            var icons = new List<(string Label, LinkItem Item, ImageSource? Image)>();
            Check("Real Windows EXE, custom shortcut, file and folder icons", () =>
            {
                string exe = Path.Combine(root, "src/LinkLauncher/bin/Release/net10.0-windows/win-x64/LinkLauncher.exe");
                string text = Path.Combine(output, "sample.txt");
                File.WriteAllText(text, "Icon check only.");
                string shortcut = Path.Combine(output, "custom-app.lnk");
                MakeShortcut(shortcut, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), exe);
                using var service = new ShellIconService();
                ImageSource? exeIcon = Get(service, exe, LinkKind.File);
                ImageSource? linkIcon = Get(service, shortcut, LinkKind.File);
                if (exeIcon is BitmapSource exeBitmap) WritePng(exeBitmap, Path.Combine(output, "exe.png"));
                if (linkIcon is BitmapSource linkBitmap) WritePng(linkBitmap, Path.Combine(output, "shortcut.png"));
                // Windows applies the shortcut arrow to the lower-left corner even without ADDOVERLAYS.
                Assert(exeIcon != null && linkIcon != null && SameUpperHalf(exeIcon, linkIcon), "Shortcut honors its custom app icon");
                foreach (var value in new[] { ("EXE", exe, LinkKind.File), ("Shortcut", shortcut, LinkKind.File),
                    ("Text file", text, LinkKind.File), ("Folder", output, LinkKind.Folder),
                    ("Missing file", Path.Combine(output, "absent.txt"), LinkKind.File),
                    ("Network type", @"\\unavailable-icon-test\share\sample.txt", LinkKind.File) })
                {
                    ImageSource? image = Get(service, value.Item2, value.Item3);
                    Assert(image != null && image.IsFrozen, "Real/fallback icon: " + value.Item1);
                    icons.Add((value.Item1, new LinkItem { Name = value.Item1, Target = value.Item2, Kind = value.Item3 }, image));
                }
                Assert(Digest(icons[2].Image!) == Digest(icons[4].Image!) && Digest(icons[2].Image!) == Digest(icons[5].Image!),
                    "Missing/network targets use type icon without accessing network path");
                icons.Add(("Web fallback", new LinkItem { Name = "Web fallback", Kind = LinkKind.Web, Target = "https://example.com" }, null));
            }, ref passed);

            Check("Production icon markup, asynchronous binding and both themes", () =>
            {
                XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var app = new ResourceOnlyApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                XElement resources = XDocument.Load(Path.Combine(root, "src/LinkLauncher/App.xaml")).Root!.Element(ns + "Application.Resources")!;
                var dictionary = new XElement(ns + "ResourceDictionary",
                    new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), resources.Elements());
                app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
                Assert(ReferenceEquals(Application.Current, app) && !app.Dispatcher.HasShutdownStarted, "Application resources are live");
                XElement markup = XDocument.Load(Path.Combine(root, "src/LinkLauncher/MainWindow.xaml"))
                    .Descendants(ns + "Grid").Single(x => (string?)x.Attribute("Loaded") == "LinkIcon_Loaded");
                foreach (string attr in new[] { "Loaded", "DataContextChanged", "IsVisibleChanged" }) markup.Attribute(attr)?.Remove();
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeManager.Apply(theme);
                    Assert(ThemeManager.CurrentTheme == theme, "Theme applied: " + theme);
                    var panel = new StackPanel { Width = 240, Background = (Brush)app.FindResource("Surface") };
                    foreach (var item in icons)
                    {
                        var row = new LinkRow(item.Item, "Icon check");
                        var iconGrid = (Grid)XamlReader.Parse(markup.ToString());
                        iconGrid.DataContext = row;
                        iconGrid.Measure(new Size(21, 21));
                        iconGrid.Arrange(new Rect(0, 0, 21, 21));
                        Border fallback = iconGrid.Children.OfType<Border>().Single();
                        Assert(fallback.Visibility == Visibility.Visible, "Fallback before completion");
                        row.Icon = item.Image;
                        PumpBindings();
                        Assert(fallback.Visibility == (row.HasIcon ? Visibility.Collapsed : Visibility.Visible), "Fallback after completion");
                        Assert(ReferenceEquals(iconGrid.Children.OfType<Image>().Single().Source, item.Image), "Image binding updates");
                        var line = new DockPanel { Height = 32, Margin = new Thickness(8, 0, 8, 0) };
                        iconGrid.Margin = new Thickness(0, 0, 8, 0);
                        line.Children.Add(iconGrid);
                        line.Children.Add(new TextBlock { Text = item.Label, FontSize = 13, Foreground = (Brush)app.FindResource("Ink"), VerticalAlignment = VerticalAlignment.Center });
                        panel.Children.Add(line);
                    }
                    panel.Measure(new Size(240, 224));
                    panel.Arrange(new Rect(0, 0, 240, 224));
                    panel.UpdateLayout();
                    var picture = new RenderTargetBitmap(240, 224, 96, 96, PixelFormats.Pbgra32);
                    picture.Render(panel);
                    WritePng(picture, Path.Combine(output, "icons-" + theme.ToLowerInvariant() + ".png"));
                }
                ThemeManager.Dispose();
                app.Shutdown();
            }, ref passed);
            Console.WriteLine($"PASS {passed}/{passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static ImageSource? Get(ShellIconService service, string path, LinkKind kind) => Await(service.GetAsync(path, kind));
    private sealed class ResourceOnlyApp : Application
    {
        // Application schedules Startup even without Run() when a Dispatcher is pumped.
        // Do not create MainWindow, register hooks, open user data, or signal the resident instance.
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private static ImageSource? Await(Task<ImageSource?> task)
    {
        Assert(task.Wait(TimeSpan.FromSeconds(10)), "Request completes within test bound");
        return task.GetAwaiter().GetResult();
    }
    private static ImageSource Picture(Color color) => BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
        new byte[] { color.B, color.G, color.R, color.A }, 4);
    private static string Digest(ImageSource image)
    {
        var bitmap = (BitmapSource)image;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static bool SameUpperHalf(ImageSource left, ImageSource right)
    {
        var a = new FormatConvertedBitmap((BitmapSource)left, PixelFormats.Bgra32, null, 0);
        var b = new FormatConvertedBitmap((BitmapSource)right, PixelFormats.Bgra32, null, 0);
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        var rect = new Int32Rect(0, 0, a.PixelWidth, a.PixelHeight / 2);
        int stride = a.PixelWidth * 4;
        var first = new byte[stride * rect.Height];
        var second = new byte[first.Length];
        a.CopyPixels(rect, first, stride, 0); b.CopyPixels(rect, second, stride, 0);
        return first.SequenceEqual(second);
    }
    private static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void MakeShortcut(string path, string target, string icon)
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = shell!.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            shortcut!.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { target });
            shortcut.GetType().InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { icon + ",0" });
            shortcut.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }
    private static void PumpBindings() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
        System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));
    private static void Check(string name, Action action, ref int passed) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
