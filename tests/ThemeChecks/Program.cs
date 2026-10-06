using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LinkLauncher.Services;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // An unshown root measures menu items; popup-root controls are instantiated without opening them.
        var app = new LinkLauncher.App();
        app.InitializeComponent();
        Window? window = null;
        try
        {
            var contextMenuStyle = (Style)app.FindResource(typeof(ContextMenu));
            var menuItemStyle = (Style)app.FindResource(typeof(MenuItem));
            var separatorStyle = (Style)app.FindResource(typeof(Separator));
            var toolTipStyle = (Style)app.FindResource(typeof(ToolTip));
            var input = new TextBox { Style = (Style)app.FindResource(typeof(TextBox)) };
            var combo = new ComboBox { Style = (Style)app.FindResource(typeof(ComboBox)) };
            var parentItem = new MenuItem { Header = "親メニュー", Style = menuItemStyle };
            parentItem.Items.Add(new MenuItem { Header = "子メニュー", Style = menuItemStyle });
            var childItem = new MenuItem { Header = "サブメニュー項目", Style = menuItemStyle };
            var separator = new Separator { Style = separatorStyle };
            var liveMenu = new Menu();
            liveMenu.Items.Add(parentItem);
            liveMenu.Items.Add(childItem);
            liveMenu.Items.Add(separator);
            var panel = new StackPanel();
            panel.Children.Add(input); panel.Children.Add(combo);
            panel.Children.Add(liveMenu);
            window = new Window { Content = panel, Width = 480, Height = 500 };
            panel.Measure(new Size(480, 500));
            panel.Arrange(new Rect(0, 0, 480, 500));
            panel.UpdateLayout();
            parentItem.ApplyTemplate(); childItem.ApplyTemplate();
            separator.ApplyTemplate();
            Border itemFrame = TemplatePart<Border>(parentItem, "MenuFrame");
            Border gutterFrame = TemplatePart<Border>(parentItem, "GutterFrame");
            Border childItemFrame = TemplatePart<Border>(childItem, "MenuFrame");
            var submenuPopup = TemplatePart<Popup>(parentItem, "PART_Popup");
            var submenuFrame = submenuPopup.Child as Border
                ?? throw new InvalidOperationException("サブメニューのテーマ枠がありません。");
            Border separatorLine = TemplatePart<Border>(separator, "SeparatorLine");
            foreach (var mode in new[] { "Light", "Dark" })
            {
                ThemeManager.Apply(mode);
                var contextMenu = new ContextMenu { Style = contextMenuStyle };
                var contextItem = new MenuItem { Header = "右クリック項目", Style = menuItemStyle };
                contextItem.Items.Add(new MenuItem { Header = "子項目", Style = menuItemStyle });
                contextMenu.Items.Add(contextItem);
                contextMenu.ApplyTemplate();
                contextItem.ApplyTemplate();
                Border menuFrame = TemplatePart<Border>(contextMenu, "MenuFrame");
                Border contextItemFrame = TemplatePart<Border>(contextItem, "MenuFrame");
                Border contextGutterFrame = TemplatePart<Border>(contextItem, "GutterFrame");
                var contextPopup = TemplatePart<Popup>(contextItem, "PART_Popup");
                var contextSubmenuFrame = contextPopup.Child as Border
                    ?? throw new InvalidOperationException("右クリックメニューのサブメニュー枠がありません。");
                var toolTip = new ToolTip { Content = "説明", Style = toolTipStyle };
                toolTip.ApplyTemplate();
                Border toolTipFrame = TemplatePart<Border>(toolTip, "ToolTipFrame");
                Require(Color(input.Foreground) == Color(ThemeManager.GetBrush("Ink")));
                Require(Color(input.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Color(combo.Foreground) == Color(ThemeManager.GetBrush("Ink")));
                Require(Color(combo.Background) == Color(ThemeManager.GetBrush("Surface")));
                RequireColor(menuFrame.Background, "Surface");
                Require(Color(menuFrame.BorderBrush) == Color(ThemeManager.GetBrush("Border")));
                Require(Color(contextItemFrame.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Color(contextGutterFrame.Background) == Color(ThemeManager.GetBrush("SoftSurface")));
                Require(Color(contextSubmenuFrame.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Color(contextSubmenuFrame.BorderBrush) == Color(ThemeManager.GetBrush("Border")));
                Require(Color(itemFrame.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Color(gutterFrame.Background) == Color(ThemeManager.GetBrush("SoftSurface")));
                Require(Color(childItemFrame.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Color(submenuFrame.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Color(submenuFrame.BorderBrush) == Color(ThemeManager.GetBrush("Border")));
                Require(Color(separatorLine.Background) == Color(ThemeManager.GetBrush("Border")));
                Require(Color(toolTip.Foreground) == Color(ThemeManager.GetBrush("Ink")));
                Require(Color(toolTipFrame.Background) == Color(ThemeManager.GetBrush("Surface")));
                Require(Color(toolTipFrame.BorderBrush) == Color(ThemeManager.GetBrush("Border")));
                Require(Contrast(Color(input.Foreground), Color(input.Background)) > 7);
                Require(Contrast(Color(toolTip.Foreground), Color(toolTipFrame.Background)) > 7);
                Console.WriteLine("PASS " + mode + " リソース・入力欄・選択欄・メニュー・ツールチップ");
            }
            ThemeManager.Apply("System");
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            Require(ThemeManager.IsDark == (Convert.ToInt32(value) == 0));
            Console.WriteLine("PASS Windows設定の読取り（OS設定は変更しない）");
            window.Close();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (window?.IsVisible == true) window.Close();
            ThemeManager.Dispose();
            app.Shutdown();
        }
    }
    private static T TemplatePart<T>(Control control, string name) where T : FrameworkElement
    {
        return control.Template?.FindName(name, control) as T
            ?? throw new InvalidOperationException("テンプレート部品がありません: " + name);
    }
    private static Color Color(Brush brush) => ((SolidColorBrush)brush).Color;
    private static void RequireColor(Brush actual, string key)
    {
        Color actualColor = Color(actual);
        Color expectedColor = Color(ThemeManager.GetBrush(key));
        Require(actualColor == expectedColor,
            $"テーマ色が一致しません ({key}): actual={actualColor}, expected={expectedColor}");
    }
    private static void Require(bool ok, string? message = null)
    {
        if (!ok) throw new InvalidOperationException(message ?? "テーマ条件を満たしません。");
    }
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte x) { double v = x / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
