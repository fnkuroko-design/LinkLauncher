using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkLauncher.Models;
using LinkLauncher.Services;
using Microsoft.Win32;

namespace LinkLauncher.Views;

internal record CategoryChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

internal static class DialogUi
{
    public static StackPanel Panel(Window window, string title, string subtitle, double width, double height)
    {
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false;
        WindowAppearance.Bind(window);
        var panel = new StackPanel { Margin = new Thickness(28, 20, 28, 24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Description(subtitle, 12, new Thickness(0, 7, 0, 16)));
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return panel;
    }

    public static TextBlock Description(string text, double fontSize, Thickness margin)
    {
        var block = new TextBlock { Text = text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap, Margin = margin };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return block;
    }

    public static void Label(Panel panel, string text)
        => panel.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 6) });

    public static void Buttons(Window window, Panel panel, Action save, string saveText = "保存")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => window.DialogResult = false;
        var accept = new Button { Content = saveText, IsDefault = true, Style = (Style)window.FindResource("PrimaryButton") };
        accept.Click += (_, _) => save();
        row.Children.Add(cancel);
        row.Children.Add(accept);
        panel.Children.Add(row);
    }

    public static List<CategoryChoice> Choices(Library library, HashSet<string>? exclude = null, bool includeRoot = false)
    {
        var paths = SearchEngine.GetCategoryPaths(library);
        var choices = library.Categories.Where(c => exclude == null || !exclude.Contains(c.Id))
            .Select(c => new CategoryChoice(c.Id, paths.GetValueOrDefault(c.Id, "")))
            .OrderBy(c => c.Label, StringComparer.CurrentCulture).ToList();
        if (includeRoot) choices.Insert(0, new CategoryChoice("", "（最上位）"));
        return choices;
    }
}

internal sealed class LinkEditor : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _target = new();
    private readonly TextBox _tags = new();
    private readonly TextBox _note = new();
    private readonly ComboBox _category = new();
    private readonly CheckBox _favorite = new() { Content = "お気に入りに登録" };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly LinkItem? _original;
    public LinkItem? Result { get; private set; }

    public LinkEditor(Library library, string categoryId, LinkItem? original = null, string? initialTarget = null)
    {
        _original = original;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk");
        var panel = DialogUi.Panel(this, original == null ? "リンクを追加" : "リンクを編集",
            "ファイル・フォルダ・WebのURLを登録できます。", 570, 685);
        DialogUi.Label(panel, "名前"); panel.Children.Add(_name);
        DialogUi.Label(panel, "リンク先"); panel.Children.Add(_target);
        var browse = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var file = new Button { Content = "ファイルを選ぶ", Padding = new Thickness(12, 5, 12, 5), FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
        file.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "登録するファイルを選択", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) SetTarget(dialog.FileName);
        };
        var folder = new Button { Content = "フォルダを選ぶ", Padding = new Thickness(12, 5, 12, 5), FontSize = 12 };
        folder.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "登録するフォルダを選択", UseDescriptionForTitle = true };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) SetTarget(dialog.SelectedPath);
        };
        browse.Children.Add(file); browse.Children.Add(folder); panel.Children.Add(browse);
        DialogUi.Label(panel, "カテゴリ"); panel.Children.Add(_category);
        var choices = DialogUi.Choices(library);
        _category.ItemsSource = choices;
        _category.SelectedItem = choices.FirstOrDefault(c => c.Id == (original?.CategoryId ?? categoryId)) ?? choices.FirstOrDefault();
        DialogUi.Label(panel, "タグ（スペースやカンマで区切る）"); panel.Children.Add(_tags);
        DialogUi.Label(panel, "メモ"); _note.Height = 62; _note.AcceptsReturn = true;
        _note.TextWrapping = TextWrapping.Wrap; _note.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; panel.Children.Add(_note);
        panel.Children.Add(_favorite); panel.Children.Add(_error);
        DialogUi.Buttons(this, panel, Save);
        if (original != null)
        {
            _name.Text = original.Name; _target.Text = original.Target; _tags.Text = original.Tags;
            _note.Text = original.Note; _favorite.IsChecked = original.IsFavorite;
        }
        else if (initialTarget != null) SetTarget(initialTarget);
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private void SetTarget(string target)
    {
        _target.Text = target;
        if (!string.IsNullOrWhiteSpace(_name.Text)) return;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            _name.Text = uri.Host;
        else _name.Text = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : target;
    }

    private void Save()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) throw new ArgumentException("名前を入力してください。");
            if (_category.SelectedItem is not CategoryChoice category) throw new ArgumentException("カテゴリを選択してください。");
            var target = LibraryStore.NormalizeTarget(_target.Text);
            Result = new LinkItem
            {
                Id = _original?.Id ?? Guid.NewGuid().ToString("N"), Name = _name.Text.Trim(), Target = target,
                CategoryId = category.Id, Tags = _tags.Text.Trim(), Note = _note.Text.Trim(),
                Kind = LibraryStore.DetectKind(target), IsFavorite = _favorite.IsChecked == true,
                LastOpenedUtc = _original?.LastOpenedUtc, OpenCount = _original?.OpenCount ?? 0
            };
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException)
        { _error.Text = ex.Message; }
    }
}

internal sealed class CategoryEditor : Window
{
    private readonly TextBox _name = new();
    private readonly ComboBox _parent = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Library _library;
    private readonly Category? _original;
    public Category? Result { get; private set; }

    public CategoryEditor(Library library, string? parentId, Category? original = null)
    {
        _library = library; _original = original;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk");
        var panel = DialogUi.Panel(this, original == null ? "カテゴリを追加" : "カテゴリを編集",
            "案件・種類で階層を作れます。親カテゴリを変えると移動します。", 530, 350);
        DialogUi.Label(panel, "カテゴリ名"); panel.Children.Add(_name);
        DialogUi.Label(panel, "親カテゴリ"); panel.Children.Add(_parent);
        var excluded = original == null ? null : SearchEngine.DescendantIds(library, original.Id);
        var choices = DialogUi.Choices(library, excluded, true);
        _parent.ItemsSource = choices;
        _parent.SelectedItem = choices.FirstOrDefault(c => c.Id == (original?.ParentId ?? parentId ?? "")) ?? choices[0];
        _name.Text = original?.Name ?? "";
        panel.Children.Add(_error);
        DialogUi.Buttons(this, panel, Save);
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        var parent = (_parent.SelectedItem as CategoryChoice)?.Id;
        if (parent == "") parent = null;
        if (name.Length == 0) { _error.Text = "カテゴリ名を入力してください。"; return; }
        if (_library.Categories.Any(c => c.Id != _original?.Id && c.ParentId == parent && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        { _error.Text = "同じ階層にその名前のカテゴリがあります。"; return; }
        Result = new Category { Id = _original?.Id ?? Guid.NewGuid().ToString("N"), Name = name, ParentId = parent };
        DialogResult = true;
    }
}

internal sealed record MouseActivationChoice(MouseActivationPattern Pattern, string Label, string Hint, string Description)
{
    public override string ToString() => Label;
    public static readonly MouseActivationChoice[] Choices =
    {
        new(MouseActivationPattern.MiddleThenRight, "ホイールを押しながら右クリック", "ホイール＋右",
            "ホイールボタンを押したまま、動かさずに右ボタンを押すとすぐ呼び出します。\nホイールだけで動かした場合は通常のドラッグ、離した場合は通常のクリックになります。"),
        new(MouseActivationPattern.RightThenLeft, "右ボタンを押しながら左クリック", "右＋左",
            "右ボタンを先に押し、動かさずに左ボタンを押すとすぐ呼び出します。\n右だけで動かした場合は通常のドラッグ、離した場合は通常のクリックになります。"),
        new(MouseActivationPattern.MiddleClick, "ホイールクリック", "ホイールクリック",
            "ホイールボタンを押すとすぐ呼び出します。\n他アプリのホイールクリック（リンクを別タブで開く、自動スクロール等）は使えなくなります。"),
        new(MouseActivationPattern.XButton1, "マウスの戻るボタン", "戻るボタン",
            "戻るボタンを押すとすぐ呼び出します。\n他アプリの「戻る」操作を置き換えます。対応するマウスが必要です。"),
        new(MouseActivationPattern.XButton2, "マウスの進むボタン", "進むボタン",
            "進むボタンを押すとすぐ呼び出します。\n他アプリの「進む」操作を置き換えます。対応するマウスが必要です。"),
        new(MouseActivationPattern.None, "使わない", "", "ショートカットまたはタスクトレイから呼び出します。")
    };
    public static MouseActivationChoice For(MouseActivationPattern pattern) => Choices.First(c => c.Pattern == pattern);
}

internal sealed class SettingsDialog : Window
{
    private readonly ComboBox _hotkey = new();
    private readonly ComboBox _mouse = new();
    private readonly CheckBox _startup = new() { Content = "Windowsへのサインイン時に起動する" };
    private readonly CheckBox _hideAfterLaunch = new() { Content = "リンクを開いたらランチャーを閉じる" };
    private readonly CheckBox _dismiss = new() { Content = "他の画面をクリックしたら閉じる" };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Func<LauncherSettings, bool, string?> _apply;

    public SettingsDialog(LauncherSettings settings, bool startupEnabled, string dataPath, Func<LauncherSettings, bool, string?> apply,
        Action import, Action export)
    {
        _apply = apply;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk");
        var panel = DialogUi.Panel(this, "設定", "呼び出し方と、リンクの保存を管理します。", 585, 720);
        DialogUi.Label(panel, "マウスで呼び出す");
        _mouse.ItemsSource = MouseActivationChoice.Choices;
        panel.Children.Add(_mouse);
        var mouseDescription = DialogUi.Description("", 11, new Thickness(0, 7, 0, 5));
        panel.Children.Add(mouseDescription);
        _mouse.SelectionChanged += (_, _) => mouseDescription.Text = (_mouse.SelectedItem as MouseActivationChoice)?.Description ?? "";
        _mouse.SelectedItem = MouseActivationChoice.For(settings.MousePattern);
        panel.Children.Add(DialogUi.Description("普段使うアプリと重ならない操作を選んでください。組み合わせは先に押すボタンの順序が決まっています。", 11, new Thickness(0, 0, 0, 5)));
        DialogUi.Label(panel, "ショートカット"); _hotkey.ItemsSource = DesktopIntegration.SupportedHotkeys;
        _hotkey.SelectedItem = settings.Hotkey; panel.Children.Add(_hotkey);
        DialogUi.Label(panel, "起動・画面の動作");
        _startup.IsChecked = startupEnabled;
        _startup.ToolTip = "画面を開かずタスクトレイに常駐します。このWindowsユーザーに適用します。";
        panel.Children.Add(_startup); panel.Children.Add(_hideAfterLaunch); panel.Children.Add(_dismiss);
        panel.Children.Add(DialogUi.Description("表示モードはWindowsの「アプリのモード」に自動で合わせます。", 11, new Thickness(0, 8, 0, 3)));
        DialogUi.Label(panel, "データ");
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var importButton = new Button { Content = "JSONを取り込む", FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
        importButton.Click += (_, _) => import();
        var exportButton = new Button { Content = "JSONを書き出す", FontSize = 12 };
        exportButton.Click += (_, _) => export();
        actions.Children.Add(importButton); actions.Children.Add(exportButton); panel.Children.Add(actions);
        panel.Children.Add(DialogUi.Description(dataPath, 10, new Thickness(0, 9, 0, 0)));
        panel.Children.Add(DialogUi.Description("LinkLauncher " + AppInfo.Version + "  •  MIT License  •  .NET 10", 11, new Thickness(0, 17, 0, 7)));
        panel.Children.Add(_error);
        DialogUi.Buttons(this, panel, Save);
        _hideAfterLaunch.IsChecked = settings.HideAfterLaunch; _dismiss.IsChecked = settings.DismissOnDeactivate;
    }

    private void Save()
    {
        var settings = new LauncherSettings
        {
            Hotkey = _hotkey.SelectedItem as string ?? "Ctrl + Alt + Space",
            MousePattern = (_mouse.SelectedItem as MouseActivationChoice)?.Pattern ?? MouseActivationPattern.None,
            HideAfterLaunch = _hideAfterLaunch.IsChecked == true, DismissOnDeactivate = _dismiss.IsChecked == true
        };
        string? error = _apply(settings, _startup.IsChecked == true);
        if (error == null) DialogResult = true;
        else _error.Text = error;
    }
}
