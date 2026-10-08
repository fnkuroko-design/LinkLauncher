using System.Text;
using LinkLauncher.Models;
using LinkLauncher.Services;

string testRoot = Path.Combine(Path.GetTempPath(), "LinkLauncher-order-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int passed = 0;
try
{
    Check("Orderのない旧JSONを従来順で表示し、初期化後も同じ順を保存", () =>
    {
        var store = new LibraryStore(testRoot);
        string legacy = """
            {
              "SchemaVersion": 1,
              "Categories": [
                { "Id": "root-z", "Name": "Z" },
                { "Id": "root-a", "Name": "A" },
                { "Id": "child-z", "ParentId": "root-a", "Name": "B" },
                { "Id": "child-a", "ParentId": "root-a", "Name": "A" }
              ],
              "Links": [
                { "Id": "normal", "CategoryId": "root-a", "Name": "Alpha", "Target": "https://example.com/a", "Kind": "Web" },
                { "Id": "favorite", "CategoryId": "root-a", "Name": "Zulu", "Target": "https://example.com/z", "Kind": "Web", "IsFavorite": true }
              ],
              "Settings": {}
            }
            """;
        File.WriteAllText(store.FilePath, legacy, new UTF8Encoding(false));

        Library loaded = store.Load();
        Require(loaded.Categories.All(category => category.Order is null));
        Require(loaded.Links.All(link => link.Order is null));
        Require(LibraryOrder.GetCategories(loaded, null).Select(category => category.Id).SequenceEqual(new[] { "root-a", "root-z" }));
        Require(LibraryOrder.GetCategories(loaded, "root-a").Select(category => category.Id).SequenceEqual(new[] { "child-a", "child-z" }));
        Require(LibraryOrder.GetLinks(loaded).Select(link => link.Id).SequenceEqual(new[] { "favorite", "normal" }));

        LibraryOrder.EnsureInitialized(loaded);
        store.Save(loaded);
        Library reloaded = store.Load();
        Require(LibraryOrder.GetCategories(reloaded, null).Select(category => category.Id).SequenceEqual(new[] { "root-a", "root-z" }));
        Require(LibraryOrder.GetLinks(reloaded).Select(link => link.Id).SequenceEqual(new[] { "favorite", "normal" }));
        Require(File.ReadAllText(store.FilePath).Contains("\"Order\": 0", StringComparison.Ordinal));
    });

    Check("カテゴリ移動は同じ親の順序だけを変え、子孫の親IDを保つ", () =>
    {
        var library = new Library
        {
            Categories = new()
            {
                new() { Id = "parent", Name = "親" },
                new() { Id = "other-root", Name = "別の親" },
                new() { Id = "alpha", ParentId = "parent", Name = "A" },
                new() { Id = "beta", ParentId = "parent", Name = "B" },
                new() { Id = "grandchild", ParentId = "beta", Name = "孫" }
            }
        };
        string[] rootsBefore = LibraryOrder.GetCategories(library, null).Select(category => category.Id).ToArray();

        Require(LibraryOrder.MoveCategory(library, "beta", 0));
        Require(LibraryOrder.GetCategories(library, "parent").Select(category => category.Id).SequenceEqual(new[] { "beta", "alpha" }));
        Require(LibraryOrder.GetCategories(library, null).Select(category => category.Id).SequenceEqual(rootsBefore));
        Require(library.Categories.Single(category => category.Id == "beta").ParentId == "parent");
        Require(library.Categories.Single(category => category.Id == "grandchild").ParentId == "beta");
    });

    Check("表示中のリンク移動は非表示リンク同士の順を維持", () =>
    {
        var library = NewLinkLibrary("a", "hidden-1", "b", "hidden-2", "c", "hidden-3");
        Require(LibraryOrder.MoveVisibleLinks(library, new[] { "a", "b", "c" }, "b", 0));
        Require(LibraryOrder.GetLinks(library).Select(link => link.Id).SequenceEqual(new[]
        {
            "b", "hidden-1", "a", "hidden-2", "c", "hidden-3"
        }));
        Require(LibraryOrder.GetLinks(library).Where(link => link.Id.StartsWith("hidden-", StringComparison.Ordinal))
            .Select(link => link.Id).SequenceEqual(new[] { "hidden-1", "hidden-2", "hidden-3" }));
    });

    Check("編集は順番を保ち、親変更と新規追加は末尾に整列", () =>
    {
        var library = new Library
        {
            Categories = new()
            {
                new() { Id = "root-a", Name = "A" },
                new() { Id = "root-b", Name = "B" },
                new() { Id = "child-a", ParentId = "root-a", Name = "A" },
                new() { Id = "child-b", ParentId = "root-a", Name = "B" },
                new() { Id = "destination-existing", ParentId = "root-b", Name = "既存" },
                new() { Id = "descendant", ParentId = "child-a", Name = "孫" }
            },
            Links = new()
            {
                new() { Id = "first", CategoryId = "root-a", Name = "A", Target = "https://example.com/a", Kind = LinkKind.Web },
                new() { Id = "second", CategoryId = "root-a", Name = "B", Target = "https://example.com/b", Kind = LinkKind.Web }
            }
        };

        LibraryOrder.EnsureInitialized(library);
        int originalCategoryOrder = LibraryOrder.GetCategories(library, "root-a").Single(category => category.Id == "child-a").Order ?? -1;
        int originalLinkOrder = LibraryOrder.GetLinks(library).Single(link => link.Id == "first").Order ?? -1;
        LibraryOrder.UpsertCategory(library, new Category { Id = "child-a", ParentId = "root-a", Name = "名前変更" });
        LibraryOrder.UpsertLink(library, new LinkItem
        {
            Id = "first", CategoryId = "root-b", Name = "リンク名変更", Target = "https://example.com/a", Kind = LinkKind.Web
        });
        Require(library.Categories.Single(category => category.Id == "child-a").Order == originalCategoryOrder);
        Require(library.Links.Single(link => link.Id == "first").Order == originalLinkOrder);

        LibraryOrder.UpsertCategory(library, new Category { Id = "child-b", ParentId = "root-b", Name = "移動" });
        Require(LibraryOrder.GetCategories(library, "root-b").Select(category => category.Id).SequenceEqual(new[] { "destination-existing", "child-b" }));
        Require(library.Categories.Single(category => category.Id == "descendant").ParentId == "child-a");

        LibraryOrder.UpsertLink(library, new LinkItem
        {
            Id = "new", CategoryId = "root-a", Name = "新規", Target = "https://example.com/new", Kind = LinkKind.Web
        });
        Require(LibraryOrder.GetLinks(library).Last().Id == "new");
    });

    Check("変更した順序を再読込し、バックアップと不正順序の拒否を確認", () =>
    {
        var store = new LibraryStore(Path.Combine(testRoot, "saved-order"));
        var library = NewLinkLibrary("a", "b", "c");
        library.Categories[0].Name = "A";
        library.Categories.Add(new Category { Id = "other", Name = "B" });
        store.Save(library);
        Require(LibraryOrder.MoveCategory(library, "other", 0));
        Require(LibraryOrder.MoveVisibleLinks(library, new[] { "a", "b", "c" }, "c", 0));
        store.Save(library);
        var restored = store.Load();
        Require(LibraryOrder.GetCategories(restored, null).Select(c => c.Id).SequenceEqual(new[] { "other", "category" }));
        Require(LibraryOrder.GetLinks(restored).Select(l => l.Id).SequenceEqual(new[] { "c", "a", "b" }));
        Require(LibraryOrder.GetLinks(store.ReadImport(store.FilePath + ".bak")).Select(l => l.Id).SequenceEqual(new[] { "a", "b", "c" }));
        byte[] saved = File.ReadAllBytes(store.FilePath);
        restored.Links[0].Order = -1;
        try { store.Save(restored); throw new InvalidOperationException("負数の順序を受け入れました。"); }
        catch (InvalidDataException) { }
        Require(saved.SequenceEqual(File.ReadAllBytes(store.FilePath)));
    });

    Console.WriteLine($"PASS {passed}/5");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    string resolvedRoot = Path.GetFullPath(testRoot);
    string tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (resolvedRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(resolvedRoot).StartsWith("LinkLauncher-order-checks-", StringComparison.Ordinal))
    {
        Directory.Delete(resolvedRoot, recursive: true);
    }
}

void Check(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine("PASS " + name);
}

void Require(bool condition)
{
    if (!condition) throw new InvalidOperationException("受入条件を満たしません。");
}

static Library NewLinkLibrary(params string[] ids)
{
    var library = new Library
    {
        Categories = new() { new Category { Id = "category", Name = "カテゴリ" } },
        Links = ids.Select((id, index) => new LinkItem
        {
            Id = id,
            CategoryId = "category",
            Name = id,
            Target = $"https://example.com/{index}",
            Kind = LinkKind.Web,
            Order = index
        }).ToList()
    };
    return library;
}
