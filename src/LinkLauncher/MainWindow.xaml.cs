using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LinkLauncher.Models;
using LinkLauncher.Services;
using LinkLauncher.Views;
using Microsoft.Win32;

namespace LinkLauncher;

public sealed class CategoryNode : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isExpanded;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public int Count { get; init; }
    public bool IsSelected
    {
        get => _isSelected;
        set => SetState(ref _isSelected, value, nameof(IsSelected));
    }
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetState(ref _isExpanded, value, nameof(IsExpanded));
    }
    public List<CategoryNode> Children { get; init; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetState(ref bool field, bool value, string propertyName)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class LinkRow
{
    public LinkItem Item { get; }
    public string Name => Item.Name;
    public string Target => Item.Target;
    public string Note => Item.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Item.Note);
    public string CategoryName { get; }
    public string Detail { get; }
    public string Glyph => Item.Kind switch { LinkKind.Web => "↗", LinkKind.Folder => "▱", _ => "≡" };
    public string KindLabel => Item.Kind switch { LinkKind.Web => "WEB", LinkKind.Folder => "FOLDER", _ => "FILE" };
    public string IconBackground => ThemeManager.IsDark
        ? Item.Kind switch { LinkKind.Web => "#23473D", LinkKind.Folder => "#493F2E", _ => "#303D57" }
        : Item.Kind switch { LinkKind.Web => "#E6F3EF", LinkKind.Folder => "#FFF2DA", _ => "#EBEFFB" };
    public string IconForeground => ThemeManager.IsDark
        ? Item.Kind switch { LinkKind.Web => "#A0DBC7", LinkKind.Folder => "#E3C488", _ => "#ABBBEA" }
        : Item.Kind switch { LinkKind.Web => "#26836D", LinkKind.Folder => "#BA8A39", _ => "#677CB5" };
    public string FavoriteGlyph => Item.IsFavorite ? "★" : "☆";
    public string FavoriteForeground => Item.IsFavorite ? "#C8A051" : "#ADBBC3";
    public LinkRow(LinkItem item, string categoryPath)
    {
        Item = item;
        CategoryName = categoryPath.Split(" / ", StringSplitOptions.None).LastOrDefault() ?? "";
        Detail = categoryPath +
            (string.IsNullOrWhiteSpace(item.Tags) ? "" : "    ·    " + item.Tags);
    }
}

public partial class MainWindow : Window
{
    private readonly LibraryStore _store;
    private readonly DesktopIntegration _desktop;
    private readonly StartupRegistration _startup = new();
    private Library _library;
    private string _view = "all";
    private string? _categoryId;
    private string _type = "All";
    private int _modalDepth;
    private bool _pinned;
    private bool _exiting;
    private bool _ready;
    private bool _rebuildingTree;
    private readonly HashSet<ContextMenu> _openMenus = new();
    private bool _menuOpen => _openMenus.Count > 0;
    private bool _launching;
    private bool _categoriesVisible = true;
    private bool _showingLauncher;
    private bool _externalDismissPending;
    private long _visibilityGeneration;
    private long? _queuedDismissGeneration;
    private string? _pressedLinkId;
    private Point _linkPressStart;

    public MainWindow(string? dataDirectory = null)
    {
        _store = new LibraryStore(dataDirectory);
        _library = _store.Load();
        ThemeManager.Apply("System");
        InitializeComponent();
        BuildLabel.Text = AppInfo.BuildLabel;
        ShowInTaskbar = true;
        Icon = BitmapFrameFromResource();
        _desktop = new DesktopIntegration(this, ToggleLauncher, ShowSettings, Exit, ShowLauncher);
        _desktop.Warning += warning => ShowNotice(warning, true);
        _desktop.ExternalButtonDown += OnExternalButtonDown;
        IsVisibleChanged += (_, _) =>
        {
            _visibilityGeneration++;
            _externalDismissPending = false;
        };
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        SourceInitialized += (_, _) =>
        {
            _desktop.Configure(_library.Settings.Hotkey, _library.Settings.MousePattern, _library.Settings.DismissOnDeactivate);
            UpdateHints();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ThemeMessage);
        };
        Loaded += (_, _) =>
        {
            _ready = true;
            Refresh(true);
            SearchBox.Focus();
            if (_store.RecoveryNotice != null) ShowNotice(_store.RecoveryNotice, true);
            else if (!File.Exists(_store.FilePath))
            {
                try { _store.Save(_library); }
                catch (Exception ex) { SaveStatus.Text = "●  未保存"; ShowNotice("初期データを保存できませんでした。" + ex.Message, true); }
            }
        };
    }

    private static ImageSource? BitmapFrameFromResource()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/LinkLauncher.ico"));
        if (resource == null) return null;
        using var stream = resource.Stream;
        return System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
    }

    public void ShowLauncher()
    {
        if (_exiting) return;
        _visibilityGeneration++;
        _externalDismissPending = false;
        _showingLauncher = true;
        try
        {
            if (_modalDepth > 0)
            {
                WindowActivation.TryActivate(new WindowInteropHelper(this).Handle, Topmost);
                return;
            }
            WindowState = WindowState.Normal;
            Show();
            // SetWindowPos uses physical pixels and the target monitor's work area.
            // This also works across displays with different DPI values.
            if (DesktopIntegration.TryGetCursor(out var cursor))
            {
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)cursor.X, (int)cursor.Y));
                var area = screen.WorkingArea;
                var hwnd = new WindowInteropHelper(this).Handle;
                if (GetWindowRect(hwnd, out var bounds))
                {
                    int width = bounds.Right - bounds.Left;
                    int height = bounds.Bottom - bounds.Top;
                    int x = Math.Clamp((int)cursor.X - 80, area.Left, Math.Max(area.Left, area.Right - width));
                    int y = Math.Clamp((int)cursor.Y - 95, area.Top, Math.Max(area.Top, area.Bottom - height));
                    SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004);
                }
            }
            Activate();
            var handle = new WindowInteropHelper(this).Handle;
            WindowActivation.TryActivate(handle, Topmost);
            SearchBox.Focus();
            SearchBox.SelectAll();
            long generation = _visibilityGeneration;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_exiting || !IsVisible || generation != _visibilityGeneration || _modalDepth > 0) return;
                if (!WindowActivation.IsProcessForeground()) WindowActivation.TryActivate(handle, Topmost);
                if (WindowActivation.IsProcessForeground()) SearchBox.Focus();
            }));
        }
        finally { _showingLauncher = false; }
    }

    private void ToggleLauncher()
    {
        if (_modalDepth > 0) { ShowLauncher(); return; }
        if (IsVisible && IsActive) Hide();
        else ShowLauncher();
    }

    private bool ApplyChange(Action<Library> change, bool rebuildTree = true)
    {
        try
        {
            var next = JsonSerializer.Deserialize<Library>(JsonSerializer.Serialize(_library))!;
            change(next);
            _store.Save(next);
            _library = next;
            Refresh(rebuildTree);
            SaveStatus.Text = "●  ローカルに保存";
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
        {
            SaveStatus.Text = "●  保存できません";
            ShowNotice("変更を保存できませんでした。" + ex.Message, true);
            return false;
        }
    }

    private void Refresh(bool rebuildTree = false)
    {
        if (!_ready) return;
        if (_categoryId != null && !_library.Categories.Any(c => c.Id == _categoryId)) { _categoryId = null; _view = "all"; }
        if (rebuildTree) RebuildTree();
        SyncCategorySelection();
        bool searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
        IEnumerable<LinkItem> scope = _library.Links;
        if (_view == "favorite") scope = scope.Where(l => l.IsFavorite);
        else if (_view == "recent") scope = scope.Where(l => l.LastOpenedUtc.HasValue);
        else if (_categoryId != null)
        {
            var ids = SearchEngine.DescendantIds(_library, _categoryId);
            scope = scope.Where(l => ids.Contains(l.CategoryId));
        }
        if (_type != "All" && Enum.TryParse<LinkKind>(_type, out var kind)) scope = scope.Where(l => l.Kind == kind);
        var results = SearchEngine.Search(_library, SearchBox.Text, scope);
        if (!searching && _view == "recent") results = results.OrderByDescending(l => l.LastOpenedUtc).ToList();
        else if (_view != "recent") results = LibraryOrder.GetLinks(_library, results);
        var selectedId = (LinkList.SelectedItem as LinkRow)?.Item.Id;
        var paths = SearchEngine.GetCategoryPaths(_library);
        var rows = results.Select(l => new LinkRow(l, paths.GetValueOrDefault(l.CategoryId, ""))).ToList();
        LinkList.ItemsSource = rows;
        LinkList.SelectedItem = rows.FirstOrDefault(r => r.Item.Id == selectedId) ?? rows.FirstOrDefault();
        string scopeLabel = _view switch
        {
            "favorite" => "お気に入り", "recent" => "最近使ったリンク",
            _ => _library.Categories.FirstOrDefault(c => c.Id == _categoryId)?.Name ?? "すべてのリンク"
        };
        ViewTitle.Text = searching ? scopeLabel + "の検索結果" : scopeLabel;
        SearchPlaceholder.Text = _categoryId != null ? "このカテゴリ内を検索" : _view switch
        { "favorite" => "お気に入りの中を検索", "recent" => "最近使ったリンクの中を検索", _ => "名前・タグ・案件名を検索" };
        System.Windows.Automation.AutomationProperties.SetName(SearchBox, scopeLabel + "を検索");
        ViewTitle.ToolTip = _categoryId != null
            ? SearchEngine.GetCategoryPath(_library, _categoryId) + "  ·  下の階層のリンクも表示"
            : searching ? scopeLabel + "の中から検索しています。"
            : _view switch { "favorite" => "よく使うリンクを、いつでも手元に。", "recent" => "前に開いた場所へ、すぐに戻れます。", _ => "よく使うファイルとWebを、ひとつの場所に。" };
        ResultCount.Text = rows.Count + " 件";
        EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = searching ? "一致するリンクがありません" : _view switch
        { "favorite" => "お気に入りを登録しましょう", "recent" => "開いたリンクがここに並びます", _ => "いつものリンクを登録しましょう" };
        EmptyDescription.Text = searching ? "名前・タグ・カテゴリ名を変えて検索してください。" : _view == "favorite"
            ? "リンクの ☆ をクリックすると追加できます。" : "ファイルやフォルダをここにドロップできます。";
        SetNavState(AllButton, _view == "all" && _categoryId == null);
        SetNavState(FavoritesButton, _view == "favorite");
        SetNavState(RecentButton, _view == "recent");
        foreach (var button in new[] { TypeAll, TypeWeb, TypeFile, TypeFolder })
        {
            bool active = (string)button.Tag == _type;
            button.Background = ThemeManager.GetBrush(active ? "FilterActiveSurface" : "WindowBackground");
            button.BorderBrush = ThemeManager.GetBrush(active ? "FilterActiveBorder" : "Border");
            button.Foreground = ThemeManager.GetBrush(active ? "FilterActiveInk" : "FilterInk");
        }
    }

    private void RebuildTree()
    {
        _rebuildingTree = true;
        try
        {
            string? selectedCategoryId = _view == "all" ? _categoryId : null;
            var expanded = new Dictionary<string, bool>();
            void Remember(IEnumerable<CategoryNode> nodes)
            {
                foreach (var node in nodes) { expanded[node.Id] = node.IsExpanded; Remember(node.Children); }
            }
            if (CategoryTree.ItemsSource is IEnumerable<CategoryNode> previous) Remember(previous);
            var ancestors = new HashSet<string>();
            var selected = _library.Categories.FirstOrDefault(c => c.Id == selectedCategoryId);
            while (selected?.ParentId is string parentId && ancestors.Add(parentId))
                selected = _library.Categories.FirstOrDefault(c => c.Id == parentId);
            var counts = _library.Links.GroupBy(l => l.CategoryId).ToDictionary(g => g.Key, g => g.Count());
            var paths = SearchEngine.GetCategoryPaths(_library);
            var children = _library.Categories.GroupBy(c => c.ParentId ?? "").ToDictionary(g => g.Key,
                g => LibraryOrder.GetCategories(_library, g.Key.Length == 0 ? null : g.Key));
            CategoryNode Build(Category c)
            {
                var nodes = children.TryGetValue(c.Id, out var list) ? list.Select(Build).ToList() : new List<CategoryNode>();
                return new CategoryNode { Id = c.Id, Name = c.Name, Path = paths.GetValueOrDefault(c.Id, ""), IsSelected = c.Id == selectedCategoryId,
                    IsExpanded = ancestors.Contains(c.Id) || expanded.GetValueOrDefault(c.Id, c.ParentId == null),
                    Children = nodes, Count = counts.GetValueOrDefault(c.Id) + nodes.Sum(n => n.Count) };
            }
            CategoryTree.ItemsSource = children.TryGetValue("", out var roots) ? roots.Select(Build).ToList() : new List<CategoryNode>();
        }
        finally { _rebuildingTree = false; }
    }

    private void SyncCategorySelection()
    {
        bool wasRebuildingTree = _rebuildingTree;
        _rebuildingTree = true;
        try
        {
            string? selectedCategoryId = _view == "all" ? _categoryId : null;
            void Sync(IEnumerable<CategoryNode> nodes)
            {
                foreach (var node in nodes)
                {
                    node.IsSelected = node.Id == selectedCategoryId;
                    Sync(node.Children);
                }
            }
            if (CategoryTree.ItemsSource is IEnumerable<CategoryNode> roots) Sync(roots);
        }
        finally { _rebuildingTree = wasRebuildingTree; }
    }

    private static void SetNavState(Button button, bool active)
    {
        button.SetResourceReference(Control.BackgroundProperty, active ? "SelectedSurface" : "SoftSurface");
        button.SetResourceReference(Control.ForegroundProperty, active ? "Accent" : "Ink");
    }

    private void UpdateHints()
    {
        string hotkey = string.IsNullOrEmpty(_desktop.HotkeyLabel) ? "ショートカット未登録" : _desktop.HotkeyLabel;
        var mouse = MouseActivationChoice.For(_library.Settings.MousePattern);
        ActivationHint.Text = hotkey + (mouse.Hint.Length > 0 ? "  ·  " + mouse.Hint : "");
        ActivationHint.ToolTip = hotkey + " で呼び出し" +
            (mouse.Hint.Length > 0 ? "\n" + mouse.Label + "で呼び出し" : "") + "\n↑↓ 選択 / Enter 開く / Esc 閉じる";
    }

    private void ShowNotice(string message, bool error = false)
    {
        NoticePanel.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, error ? "NoticeErrorSurface" : "NoticeSurface");
        NoticeText.Text = message; NoticePanel.Visibility = Visibility.Visible;
    }

    private T Modal<T>(Func<T> action)
    {
        _modalDepth++;
        try { return action(); }
        finally { _modalDepth--; RequestAutoDismiss(); }
    }

    private void AddLink_Click(object sender, RoutedEventArgs e) => EditLink(null);
    private void EditLink(LinkItem? item, string? initialTarget = null)
    {
        if (_library.Categories.Count == 0) { AddCategory_Click(this, new RoutedEventArgs()); if (_library.Categories.Count == 0) return; }
        var dialog = new LinkEditor(_library, _categoryId ?? _library.Categories[0].Id, item, initialTarget) { Owner = this };
        if (Modal(() => dialog.ShowDialog()) != true || dialog.Result == null) return;
        var result = dialog.Result;
        if (ApplyChange(l => LibraryOrder.UpsertLink(l, result))) ShowNotice("「" + result.Name + "」を保存しました。");
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e) => EditCategory(null, _categoryId);
    private void EditCategory(Category? category, string? parentId)
    {
        var dialog = new CategoryEditor(_library, parentId, category) { Owner = this };
        if (Modal(() => dialog.ShowDialog()) != true || dialog.Result == null) return;
        var result = dialog.Result;
        if (ApplyChange(l => LibraryOrder.UpsertCategory(l, result)))
        {
            _view = "all"; _categoryId = result.Id; SearchBox.Clear(); Refresh(true); ShowNotice("「" + result.Name + "」を保存しました。");
        }
    }

    private void DeleteLink(LinkItem item)
    {
        if (Modal(() => MessageBox.Show(this, "「" + item.Name + "」を登録から削除しますか？\nリンク先のファイルは削除されません。",
            "リンクを削除", MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK)
            ApplyChange(l => l.Links.RemoveAll(x => x.Id == item.Id));
    }

    private void DeleteCategory(Category category)
    {
        var ids = SearchEngine.DescendantIds(_library, category.Id);
        int linkCount = _library.Links.Count(l => ids.Contains(l.CategoryId));
        if (Modal(() => MessageBox.Show(this, "「" + category.Name + "」と下の階層を削除しますか？\n" +
            $"{ids.Count}カテゴリ・{linkCount}リンクの登録が削除されます。\nリンク先のファイルは削除されません。",
            "カテゴリを削除", MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK)
            ApplyChange(l => { l.Links.RemoveAll(x => ids.Contains(x.CategoryId)); l.Categories.RemoveAll(c => ids.Contains(c.Id)); });
    }

    private async Task Launch(LinkItem item)
    {
        if (_launching) return;
        _launching = true;
        try
        {
            var target = LibraryStore.NormalizeTarget(item.Target);
            // A slow UNC target must not stall the UI or the low-level mouse hook.
            await Task.Run(() => { using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); });
            ApplyChange(l =>
            {
                var link = l.Links.FirstOrDefault(x => x.Id == item.Id);
                if (link == null) return;
                link.LastOpenedUtc = DateTimeOffset.UtcNow; link.OpenCount = Math.Min(int.MaxValue - 1, link.OpenCount) + 1;
            }, false);
            if (_library.Settings.HideAfterLaunch) Hide();
            else ShowNotice("「" + item.Name + "」を開きました。");
        }
        catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidOperationException || ex is ArgumentException)
        { ShowNotice("「" + item.Name + "」を開けませんでした。リンク先を確認してください。 " + ex.Message, true); }
        finally { _launching = false; }
    }

    private void LinkList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedLinkId = null;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is LinkRow row)
        { _pressedLinkId = row.Item.Id; _linkPressStart = e.GetPosition(LinkList); }
    }

    private async void LinkList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        string? pressedId = _pressedLinkId;
        _pressedLinkId = null;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var point = e.GetPosition(LinkList);
        if (container?.DataContext is LinkRow row && row.Item.Id == pressedId &&
            Math.Abs(point.X - _linkPressStart.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _linkPressStart.Y) <= SystemParameters.MinimumVerticalDragDistance)
        { e.Handled = true; await Launch(row.Item); }
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is LinkRow row)
            ApplyChange(l => { var link = l.Links.First(x => x.Id == row.Item.Id); link.IsFavorite = !link.IsFavorite; }, false);
    }

    private ContextMenu Menu(FrameworkElement target, params (string Label, Action Action)[] actions)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        menu.SetResourceReference(FrameworkElement.StyleProperty, typeof(ContextMenu));
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Label };
            item.SetResourceReference(FrameworkElement.StyleProperty, typeof(MenuItem));
            item.Click += (_, _) => action.Action(); menu.Items.Add(item);
        }
        menu.Opened += (_, _) => _openMenus.Add(menu);
        menu.Closed += (_, _) => { _openMenus.Remove(menu); RequestAutoDismiss(); };
        menu.IsOpen = true;
        return menu;
    }

    private void OpenLinkMenu(FrameworkElement target, LinkItem link)
    {
        var menu = Menu(target, ("開く", () => _ = Launch(link)), ("編集・カテゴリを変更", () => EditLink(link)),
            ("リンク先をコピー", () => { Clipboard.SetText(link.Target); ShowNotice("リンク先をコピーしました。"); }),
            ("登録を削除", () => DeleteLink(link)));
        AddOrderMenuItems(menu, () => MoveLinkOneStep(link.Id, -1), () => MoveLinkOneStep(link.Id, 1),
            CanMoveLinkOneStep(link.Id, -1), CanMoveLinkOneStep(link.Id, 1));
    }

    private void LinkMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement element && element.DataContext is LinkRow row) OpenLinkMenu(element, row.Item);
    }

    private void LinkList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is LinkRow row) { LinkList.SelectedItem = row; OpenLinkMenu(item, row.Item); e.Handled = true; }
    }

    private void Category_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not CategoryNode node) return;
        var category = _library.Categories.First(c => c.Id == node.Id);
        var menu = Menu(element, ("この中にカテゴリを追加", () => EditCategory(null, node.Id)),
            ("編集・移動", () => EditCategory(category, category.ParentId)), ("カテゴリを削除", () => DeleteCategory(category)));
        AddOrderMenuItems(menu, () => MoveCategoryOneStep(node.Id, -1), () => MoveCategoryOneStep(node.Id, 1),
            CanMoveCategoryOneStep(node.Id, -1), CanMoveCategoryOneStep(node.Id, 1));
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T result) return result;
            element = element is Visual || element is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private static void AddOrderMenuItems(ContextMenu menu, Action moveUp, Action moveDown, bool canMoveUp, bool canMoveDown)
    {
        var separator = new Separator();
        separator.SetResourceReference(FrameworkElement.StyleProperty, typeof(Separator));
        menu.Items.Add(separator);
        foreach (var (label, action, enabled) in new[] { ("上へ移動", moveUp, canMoveUp), ("下へ移動", moveDown, canMoveDown) })
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.SetResourceReference(FrameworkElement.StyleProperty, typeof(MenuItem));
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_rebuildingTree || e.NewValue is not CategoryNode node || (_view == "all" && _categoryId == node.Id)) return;
        _categoryId = node.Id; _view = "all"; SearchBox.Clear(); Refresh();
    }
    private void SelectView(string view) { _view = view; _categoryId = null; SearchBox.Clear(); Refresh(); }
    private void All_Click(object sender, RoutedEventArgs e) => SelectView("all");
    private void Favorites_Click(object sender, RoutedEventArgs e) => SelectView("favorite");
    private void Recent_Click(object sender, RoutedEventArgs e) => SelectView("recent");
    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void SearchFrame_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) == null) SearchBox.Focus();
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void Type_Click(object sender, RoutedEventArgs e) { _type = (string)((Button)sender).Tag; Refresh(); }
    private void Categories_Click(object sender, RoutedEventArgs e)
    {
        double currentWidth = ActualWidth;
        _categoriesVisible = !_categoriesVisible;
        CategoryPane.Visibility = _categoriesVisible ? Visibility.Visible : Visibility.Collapsed;
        CategoryColumn.Width = new GridLength(_categoriesVisible ? 164 : 0);
        MinWidth = _categoriesVisible ? 520 : 356;
        Width = Math.Max(MinWidth, currentWidth + (_categoriesVisible ? 164 : -164));
    }
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned; PinButton.Content = _pinned ? "固定中" : "固定";
        PinButton.SetResourceReference(Control.ForegroundProperty, _pinned ? "Accent" : "Muted");
        if (!_pinned) RequestAutoDismiss();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void ShowSettings()
    {
        if (_modalDepth > 0) return;
        if (!IsVisible) ShowLauncher();
        bool startupEnabled;
        try { startupEnabled = _startup.IsEnabled; }
        catch (Exception ex)
        { ShowNotice("スタートアップ設定を読み込めませんでした。" + ex.Message, true); return; }
        SettingsDialog? dialog = null;
        dialog = new SettingsDialog(_library.Settings, startupEnabled, _store.FilePath, ApplySettings,
            () => Import(dialog!), () => Export(dialog!)) { Owner = this };
        Modal(() => dialog.ShowDialog());
    }

    private string? ApplySettings(LauncherSettings next, bool startupEnabled)
    {
        var previous = _library.Settings;
        string? previousStartup;
        try { previousStartup = _startup.ReadCommand(); }
        catch (Exception ex) { return "スタートアップ設定を読み込めませんでした。" + ex.Message; }
        if (!_desktop.Configure(next.Hotkey, next.MousePattern, next.DismissOnDeactivate))
            return "呼び出し設定を変更できませんでした。ショートカットの競合を確認してください。";
        try { _startup.SetEnabled(startupEnabled); }
        catch (Exception ex)
        {
            return "スタートアップ登録を変更できませんでした。" + ex.Message + RestoreSettings(previous, previousStartup);
        }
        if (!ApplyChange(l => l.Settings = next, false))
            return "設定を保存できませんでした。保存先を確認してください。" + RestoreSettings(previous, previousStartup);
        UpdateHints();
        return null;
    }

    private string RestoreSettings(LauncherSettings settings, string? startupCommand)
    {
        bool restored = _desktop.Configure(settings.Hotkey, settings.MousePattern, settings.DismissOnDeactivate);
        try { _startup.RestoreCommand(startupCommand); }
        catch (Exception ex) { return "\n元のスタートアップ設定に戻せませんでした。" + ex.Message; }
        return restored ? "" : "\n元の呼び出し設定に戻せませんでした。設定を確認してください。";
    }

    private void Import(Window owner)
    {
        var picker = new OpenFileDialog { Title = "リンクデータを取り込む", Filter = "LinkLauncher JSON|*.json", CheckFileExists = true };
        if (picker.ShowDialog(owner) != true) return;
        try
        {
            var incoming = _store.ReadImport(picker.FileName);
            var answer = MessageBox.Show(owner, $"{incoming.Categories.Count}カテゴリ・{incoming.Links.Count}リンクに置き換えますか？\n現在の登録データは .bak に保持されます。",
                "JSONの取り込み", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            // The machine's input settings stay with this machine.
            incoming.Settings = _library.Settings;
            _store.Save(incoming); _library = incoming;
            _categoryId = null; _view = "all"; SearchBox.Clear(); Refresh(true);
            ShowNotice("リンクデータを取り込みました。");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is JsonException || ex is InvalidOperationException)
        { MessageBox.Show(owner, "取り込めませんでした。\n" + ex.Message, "LinkLauncher", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Export(Window owner)
    {
        var picker = new SaveFileDialog { Title = "リンクデータを書き出す", Filter = "LinkLauncher JSON|*.json",
            FileName = "LinkLauncher-links-" + DateTime.Now.ToString("yyyyMMdd") + ".json" };
        if (picker.ShowDialog(owner) != true) return;
        try { _store.Export(_library, picker.FileName); ShowNotice("リンクデータを書き出しました。"); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
        { MessageBox.Show(owner, "書き出せませんでした。\n" + ex.Message, "LinkLauncher", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_modalDepth > 0) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            if (files.Length == 1) { EditLink(null, files[0]); return; }
            if (files.Length > 200) { ShowNotice("一度に登録できるファイルは200件までです。", true); return; }
            if (_library.Categories.Count == 0) { AddCategory_Click(this, new RoutedEventArgs()); if (_library.Categories.Count == 0) return; }
            var categoryId = _categoryId ?? _library.Categories[0].Id;
            var newLinks = new List<LinkItem>();
            try
            {
                foreach (var file in files)
                {
                    var target = LibraryStore.NormalizeTarget(file);
                    if (_library.Links.Any(l => l.Target.Equals(target, StringComparison.OrdinalIgnoreCase) && l.CategoryId == categoryId)) continue;
                    newLinks.Add(new LinkItem { Name = Path.GetFileName(file.TrimEnd('\\')) is { Length: > 0 } name ? name : file,
                        Target = target, Kind = LibraryStore.DetectKind(target), CategoryId = categoryId });
                }
                if (ApplyChange(l => l.Links.AddRange(newLinks))) ShowNotice(newLinks.Count + "件のリンクを登録しました。");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException) { ShowNotice(ex.Message, true); }
        }
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text)
        {
            try { EditLink(null, LibraryStore.NormalizeTarget(text.Trim())); }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException) { ShowNotice("URLまたはファイルの絶対パスをドロップしてください。 " + ex.Message, true); }
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_modalDepth > 0 || _menuOpen || IsOrderDragInProgress) return;
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.N)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) AddCategory_Click(this, new RoutedEventArgs()); else EditLink(null);
            e.Handled = true; return;
        }
        if (e.Key == Key.System && e.SystemKey == Key.Left && _categoryId != null)
        {
            _categoryId = _library.Categories.First(c => c.Id == _categoryId).ParentId; _view = "all"; Refresh(); e.Handled = true; return;
        }
        if (SearchBox.IsKeyboardFocusWithin || LinkList.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                int index = LinkList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                if (LinkList.Items.Count > 0)
                { LinkList.SelectedIndex = Math.Clamp(index, 0, LinkList.Items.Count - 1); LinkList.ScrollIntoView(LinkList.SelectedItem); }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && LinkList.SelectedItem is LinkRow selected) { e.Handled = true; await Launch(selected.Item); }
            else if (e.Key == Key.F2 && LinkList.SelectedItem is LinkRow edit) { e.Handled = true; EditLink(edit.Item); }
            else if (e.Key == Key.Delete && LinkList.IsKeyboardFocusWithin && LinkList.SelectedItem is LinkRow delete) { e.Handled = true; DeleteLink(delete.Item); }
        }
    }

    private void Title_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) == null && e.ChangedButton == MouseButton.Left) DragMove();
    }
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => NoticePanel.Visibility = Visibility.Collapsed;
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        _pressedLinkId = null;
        RequestAutoDismiss();
    }

    private void OnExternalButtonDown()
    {
        if (!_ready || !IsVisible || _showingLauncher || _modalDepth > 0 || _pinned || IsOrderDragInProgress ||
            !_library.Settings.DismissOnDeactivate) return;
        _externalDismissPending = true;
        RequestAutoDismiss();
    }

    private void RequestAutoDismiss()
    {
        if (!_ready || !IsVisible || _exiting) return;
        long generation = _visibilityGeneration;
        if (_queuedDismissGeneration == generation) return;
        _queuedDismissGeneration = generation;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_queuedDismissGeneration == generation) _queuedDismissGeneration = null;
            if (generation != _visibilityGeneration || !IsVisible || _exiting || _showingLauncher || _modalDepth > 0 ||
                _pinned || _menuOpen || IsOrderDragInProgress || !_library.Settings.DismissOnDeactivate) return;
            if (_externalDismissPending || !WindowActivation.IsProcessForeground()) Hide();
        }));
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true; Hide();
    }
    private void Exit()
    {
        if (_modalDepth > 0) return;
        _exiting = true; _desktop.Dispose(); Close(); Application.Current.Shutdown();
    }

    private void OnThemeChanged() => Refresh();
    private IntPtr ThemeMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x001A) ThemeManager.Apply("System");
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
