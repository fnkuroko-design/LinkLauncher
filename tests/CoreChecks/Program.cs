using System.Text;
using System.Text.Json;
using LinkLauncher.Models;
using LinkLauncher.Services;

// Storage/search acceptance checks use an isolated temporary directory.
// No user links are opened, no UI events are injected, no app data is changed.
var testRoot = Path.Combine(Path.GetTempPath(), "LinkLauncher-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int passed = 0;
try
{
    var store = new LibraryStore(testRoot);
    var category = new Category { Id = "project", Name = "案件Ａ" };
    var child = new Category { Id = "drawings", ParentId = category.Id, Name = "図面" };
    var library = new Library
    {
        Categories = new() { category, child },
        Links = new()
        {
            new() { Id = "drawing", CategoryId = child.Id, Name = "配線図 REVＡ", Target = @"C:\資料\配線図.pdf", Kind = LinkKind.File, Tags = "設計 電気", IsFavorite = true },
            new() { Id = "web", CategoryId = category.Id, Name = "GitHub", Target = "https://github.com/", Kind = LinkKind.Web }
        }
    };
    Check("日本語・全角と半角・複数語・階層をまたぐ検索", () =>
    {
        Require(SearchEngine.Search(library, "案件a 電気").Single().Id == "drawing");
        Require(SearchEngine.Search(library, "ｇｉｔｈｕｂ").Single().Id == "web");
        Require(SearchEngine.DescendantIds(library, category.Id).SetEquals(new[] { "project", "drawings" }));
    });
    Check("保存・再読込・前回データのバックアップ", () =>
    {
        store.Save(library);
        library.Links[0].Name = "配線図 第2版";
        store.Save(library);
        Require(store.Load().Links[0].Name == "配線図 第2版");
        Require(store.ReadImport(store.FilePath + ".bak").Links[0].Name == "配線図 REVＡ");
    });
    Check("循環カテゴリと未来形式を拒否し保存済みデータを保持", () =>
    {
        var before = File.ReadAllBytes(store.FilePath);
        category.ParentId = child.Id;
        Reject(() => store.Save(library));
        category.ParentId = null;
        library.SchemaVersion = 2;
        Reject(() => store.Save(library));
        library.SchemaVersion = 1;
        Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)));
    });
    Check("破損データを保持してバックアップを読み込む", () =>
    {
        File.WriteAllText(store.FilePath, "{broken", Encoding.UTF8);
        Require(store.Load().Links[0].Name == "配線図 REVＡ");
        Require(store.RecoveryNotice != null);
        Require(File.ReadAllText(store.FilePath).Contains("{broken"));
        Require(Directory.GetFiles(testRoot, "library.json.corrupt-*").Length == 1);
    });
    Check("エクスポート・インポートと本体への誤書出し防止", () =>
    {
        var exportPath = Path.Combine(testRoot, "export.json");
        store.Export(library, exportPath);
        Require(store.ReadImport(exportPath).Links.Count == 2);
        Reject(() => store.Export(library, store.FilePath));
        var invalid = Path.Combine(testRoot, "future.json");
        File.WriteAllText(invalid, JsonSerializer.Serialize(new Library { SchemaVersion = 999 }));
        Reject(() => store.ReadImport(invalid));
    });
    Check("絶対パス・URLの正規化とシェルコマンドの拒否", () =>
    {
        string path = @"C:\資料\配線図.pdf";
        foreach (string wrapped in new[]
        {
            "\"" + path + "\"", "'" + path + "'", "“" + path + "”", "”" + path + "”",
            "‘" + path + "’", "’" + path + "’", "＂" + path + "＂", "<" + path + ">", "＜" + path + "＞",
            "“'<" + path + ">'”"
        })
        {
            Require(LibraryStore.NormalizeTarget(wrapped) == path);
        }
        Require(LibraryStore.NormalizeTarget("<\\\\server\\共有\\資料 A.pdf>") == @"\\server\共有\資料 A.pdf");
        Require(LibraryStore.NormalizeTarget("file:///C:/docs/spec.pdf") == @"C:\docs\spec.pdf");
        Require(LibraryStore.NormalizeTarget("https://example.com/a") == "https://example.com/a");
        Require(LibraryStore.NormalizeTarget("“<https://example.com/a>”") == "https://example.com/a");
        Require(LibraryStore.NormalizeTarget("https://example.com/?q=\"quoted\"") == "https://example.com/?q=%22quoted%22");
        Reject(() => LibraryStore.NormalizeTarget("“C:\\資料\\配線図.pdf'"));
        Reject(() => LibraryStore.NormalizeTarget("＜＞"));
        Reject(() => LibraryStore.NormalizeTarget("powershell:command"));
        Reject(() => LibraryStore.NormalizeTarget("relative.pdf"));
        Reject(() => LibraryStore.NormalizeTarget("javascript:alert(1)"));
    });
    Check("旧マウス設定の移行と新パターンの保存", () =>
    {
        var legacy = Path.Combine(testRoot, "legacy.json");
        string json = JsonSerializer.Serialize(library);
        using var document = JsonDocument.Parse(json);
        string originalSettings = document.RootElement.GetProperty("Settings").GetRawText();
        string legacySettings = "{\"Hotkey\":\"Ctrl + Alt + Space\",\"GestureEnabled\":false,\"MouseChordEnabled\":false}";
        File.WriteAllText(legacy, json.Replace(originalSettings, legacySettings));
        var migrated = store.ReadImport(legacy);
        Require(migrated.Settings.MousePattern == MouseActivationPattern.None);
        Require(!migrated.Settings.HideOnPointerLeave);
        Require(migrated.Links.Count == library.Links.Count && migrated.Categories.Count == library.Categories.Count);
        File.WriteAllText(legacy, json.Replace(originalSettings, legacySettings.Replace("\"GestureEnabled\":false", "\"GestureEnabled\":true")));
        Require(store.ReadImport(legacy).Settings.MousePattern == MouseActivationPattern.MiddleThenRight);
        migrated.Settings.MousePattern = MouseActivationPattern.RightThenLeft;
        store.Export(migrated, legacy);
        Require(store.ReadImport(legacy).Settings.MousePattern == MouseActivationPattern.RightThenLeft);
        Require(!File.ReadAllText(legacy).Contains("GestureEnabled"));
        migrated.Settings.MousePattern = MouseActivationPattern.DesktopDoubleClick;
        migrated.Settings.HideOnPointerLeave = true;
        store.Export(migrated, legacy);
        var desktopSettings = store.ReadImport(legacy).Settings;
        Require(desktopSettings.MousePattern == MouseActivationPattern.DesktopDoubleClick);
        Require(desktopSettings.HideOnPointerLeave);
        migrated.Settings.MousePattern = (MouseActivationPattern)999;
        Reject(() => store.Export(migrated, legacy));
    });
    Console.WriteLine($"PASS {passed}/7");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally
{
    // This path is generated under the system temp directory by this process.
    // Every entry was created by these checks.
    var resolvedRoot = Path.GetFullPath(testRoot);
    var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (resolvedRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolvedRoot).StartsWith("LinkLauncher-checks-", StringComparison.Ordinal))
        Directory.Delete(resolvedRoot, true);
}
void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Require(bool ok) { if (!ok) throw new InvalidOperationException("受入条件を満たしません。"); }
void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException) { return; }
    throw new InvalidOperationException("不正な入力が受理されました。");
}
