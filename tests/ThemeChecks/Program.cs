using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkLauncher.Services;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Resource/control checks run without showing windows or changing Windows settings.
        var app = new LinkLauncher.App();
        app.InitializeComponent();
        try
        {
            var input = new TextBox { Style = (Style)app.FindResource(typeof(TextBox)) };
            var combo = new ComboBox { Style = (Style)app.FindResource(typeof(ComboBox)) };
            var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(combo);
            var window = new Window { Content = panel };
            input.ApplyTemplate(); combo.ApplyTemplate();
            foreach (var mode in new[] { "Light", "Dark" })
            {
                ThemeManager.Apply(mode);
                Require(Color(input.Foreground) == Color(ThemeManager.GetBrush("Ink")));
                Require(Color(input.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Color(combo.Foreground) == Color(ThemeManager.GetBrush("Ink")));
                Require(Color(combo.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Contrast(Color(input.Foreground), Color(input.Background)) > 7);
                Console.WriteLine("PASS " + mode + " リソース・入力欄・選択欄・文字の視認性");
            }
            ThemeManager.Apply("System");
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            Require(ThemeManager.IsDark == (Convert.ToInt32(value) == 0));
            Console.WriteLine("PASS Windows設定の読取り（OS設定は変更しない）");
            window.Close();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { ThemeManager.Dispose(); app.Shutdown(); }
    }
    private static Color Color(Brush brush) => ((SolidColorBrush)brush).Color;
    private static void Require(bool ok) { if (!ok) throw new InvalidOperationException("テーマ条件を満たしません。"); }
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte x) { double v = x / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
