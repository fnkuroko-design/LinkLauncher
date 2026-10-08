using System;
using System.Collections.Generic;
using System.Linq;
using LinkLauncher.Models;

namespace LinkLauncher.Services;

/// <summary>
/// ライブラリの手動並び順と、並び順がまだ保存されていない旧データの表示順を扱います。
/// 参照用メソッドはライブラリを変更せず、書込みメソッドだけが Order を初期化します。
/// </summary>
public static class LibraryOrder
{
    public static IReadOnlyList<Category> GetCategories(Library library, string? parentId)
    {
        ArgumentNullException.ThrowIfNull(library);
        IEnumerable<Category> siblings = (library.Categories ?? new List<Category>())
            .Where(category => category is not null && SameParent(category.ParentId, parentId));
        return SortCategories(siblings).ToArray();
    }

    /// <summary>
    /// source を指定しない場合は従来の空検索順を使います。
    /// source を指定した場合、全件が未初期化なら source 順を保ち、保存済み順があればそれを適用します。
    /// </summary>
    public static IReadOnlyList<LinkItem> GetLinks(Library library, IEnumerable<LinkItem>? source = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        List<LinkItem> links = (source ?? SearchEngine.Search(library, string.Empty)).ToList();
        if (links.All(link => link.Order is null))
        {
            return links;
        }

        return links
            .Select((link, index) => new { Link = link, Index = index })
            .OrderBy(row => row.Link.Order.HasValue ? 0 : 1)
            .ThenBy(row => row.Link.Order ?? int.MaxValue)
            .ThenBy(row => row.Index)
            .Select(row => row.Link)
            .ToArray();
    }

    /// <summary>
    /// 未設定の全カテゴリとリンクに、現行表示を保つ0始まりの順番を割り当てます。
    /// 一部だけ Order がある場合は、設定済み項目をその順で先に並べ、未設定項目を旧表示順で後ろへ置きます。
    /// </summary>
    public static void EnsureInitialized(Library library)
    {
        ArgumentNullException.ThrowIfNull(library);

        library.Categories ??= new List<Category>();
        library.Links ??= new List<LinkItem>();

        foreach (IGrouping<string, Category> siblings in library.Categories
                     .Where(category => category is not null)
                     .GroupBy(category => NormalizeParent(category.ParentId) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            int order = 0;
            foreach (Category category in SortCategories(siblings))
            {
                category.Order = order++;
            }
        }

        IReadOnlyList<LinkItem> orderedLinks = GetLinks(library);
        for (int index = 0; index < orderedLinks.Count; index++)
        {
            orderedLinks[index].Order = index;
        }
    }

    /// <summary>カテゴリを同じ親の兄弟内で指定位置へ移動します。targetSiblingIndex は0始まりです。</summary>
    public static bool MoveCategory(Library library, string categoryId, int targetSiblingIndex)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        EnsureInitialized(library);

        Category? category = library.Categories.FirstOrDefault(item =>
            item.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
        if (category is null)
        {
            return false;
        }

        List<Category> siblings = GetCategories(library, category.ParentId).ToList();
        int currentIndex = siblings.FindIndex(item => item.Id.Equals(category.Id, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0 || siblings.Count < 2)
        {
            return false;
        }

        int destination = Math.Clamp(targetSiblingIndex, 0, siblings.Count - 1);
        if (currentIndex == destination)
        {
            return false;
        }

        siblings.RemoveAt(currentIndex);
        siblings.Insert(destination, category);
        AssignCategoryOrders(siblings);
        return true;
    }

    /// <summary>
    /// 表示中のIDを現在の行順とし、移動したリンクを指定位置へ置きます。
    /// 全体順で表示リンクが占めていた枠だけを入れ替えるため、非表示リンク同士の相対順は保たれます。
    /// targetIndex は移動後の表示一覧における0始まり位置です。
    /// </summary>
    public static bool MoveVisibleLinks(
        Library library,
        IReadOnlyList<string> displayedIds,
        string movedId,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(displayedIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(movedId);
        EnsureInitialized(library);

        if (displayedIds.Count == 0)
        {
            return false;
        }

        var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linksById = new Dictionary<string, LinkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (LinkItem link in library.Links)
        {
            linksById.TryAdd(link.Id, link);
        }

        foreach (string? id in displayedIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !visibleIds.Add(id) || !linksById.ContainsKey(id))
            {
                return false;
            }
        }

        int sourceIndex = -1;
        for (int index = 0; index < displayedIds.Count; index++)
        {
            if (displayedIds[index].Equals(movedId, StringComparison.OrdinalIgnoreCase))
            {
                sourceIndex = index;
                break;
            }
        }

        if (sourceIndex < 0)
        {
            return false;
        }

        int destination = Math.Clamp(targetIndex, 0, displayedIds.Count - 1);
        if (sourceIndex == destination)
        {
            return false;
        }

        List<LinkItem> reorderedVisible = displayedIds.Select(id => linksById[id]).ToList();
        LinkItem moved = reorderedVisible[sourceIndex];
        reorderedVisible.RemoveAt(sourceIndex);
        reorderedVisible.Insert(destination, moved);

        List<LinkItem> allInOrder = GetLinks(library).ToList();
        int replacementIndex = 0;
        for (int index = 0; index < allInOrder.Count; index++)
        {
            if (visibleIds.Contains(allInOrder[index].Id))
            {
                allInOrder[index] = reorderedVisible[replacementIndex++];
            }
        }

        for (int index = 0; index < allInOrder.Count; index++)
        {
            allInOrder[index].Order = index;
        }

        return true;
    }

    /// <summary>
    /// 既存カテゴリは親が同じなら順番を維持し、親が変われば新しい兄弟の末尾へ移します。
    /// 新しいカテゴリも兄弟の末尾へ追加します。
    /// </summary>
    public static void UpsertCategory(Library library, Category category)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(category);
        EnsureInitialized(library);

        int existingIndex = library.Categories.FindIndex(item =>
            item.Id.Equals(category.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex < 0)
        {
            category.Order = GetCategories(library, category.ParentId).Count;
            library.Categories.Add(category);
            AssignCategoryOrders(GetCategories(library, category.ParentId));
            return;
        }

        Category existing = library.Categories[existingIndex];
        string? oldParentId = existing.ParentId;
        if (SameParent(oldParentId, category.ParentId))
        {
            category.Order = existing.Order;
            library.Categories[existingIndex] = category;
            AssignCategoryOrders(GetCategories(library, category.ParentId));
            return;
        }

        category.Order = GetCategories(library, category.ParentId).Count;
        library.Categories[existingIndex] = category;
        AssignCategoryOrders(GetCategories(library, oldParentId));
        AssignCategoryOrders(GetCategories(library, category.ParentId));
    }

    /// <summary>既存リンクのグローバル順を維持し、新しいリンクは末尾へ追加します。</summary>
    public static void UpsertLink(Library library, LinkItem link)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(link);
        EnsureInitialized(library);

        int existingIndex = library.Links.FindIndex(item =>
            item.Id.Equals(link.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            link.Order = library.Links[existingIndex].Order;
            library.Links[existingIndex] = link;
            return;
        }

        link.Order = library.Links.Count;
        library.Links.Add(link);
    }

    private static IEnumerable<Category> SortCategories(IEnumerable<Category> source)
    {
        Category[] categories = source.ToArray();
        if (categories.All(category => category.Order is null))
        {
            // MainWindow の旧表示は同じ親のカテゴリを Name 昇順で並べていた。
            return categories.OrderBy(category => category.Name, Comparer<string>.Default);
        }

        return categories
            .Select((category, index) => new { Category = category, Index = index })
            .OrderBy(row => row.Category.Order.HasValue ? 0 : 1)
            .ThenBy(row => row.Category.Order ?? int.MaxValue)
            .ThenBy(row => row.Category.Order is null ? row.Category.Name : string.Empty, Comparer<string>.Default)
            .ThenBy(row => row.Index)
            .Select(row => row.Category);
    }

    private static void AssignCategoryOrders(IEnumerable<Category> categories)
    {
        int order = 0;
        foreach (Category category in categories)
        {
            category.Order = order++;
        }
    }

    private static string? NormalizeParent(string? parentId) =>
        string.IsNullOrWhiteSpace(parentId) ? null : parentId;

    private static bool SameParent(string? left, string? right) =>
        StringComparer.OrdinalIgnoreCase.Equals(NormalizeParent(left), NormalizeParent(right));
}
