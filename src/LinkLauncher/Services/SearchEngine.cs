using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LinkLauncher.Models;

namespace LinkLauncher.Services;

public static class SearchEngine
{
    public static IReadOnlyList<LinkItem> Search(Library library, string query, IEnumerable<LinkItem>? scope = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        string normalizedQuery = Normalize(query ?? string.Empty);
        string[] tokens = normalizedQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<LinkItem> source = scope ?? library.Links ?? new List<LinkItem>();

        Dictionary<string, string> categoryPaths = new(StringComparer.OrdinalIgnoreCase);
        var categoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tokens.Length > 0)
        {
            foreach (KeyValuePair<string, string> pair in GetCategoryPaths(library))
            {
                categoryPaths[pair.Key] = Normalize(pair.Value);
            }

            foreach (Category category in library.Categories ?? new List<Category>())
            {
                categoryNames[category.Id] = Normalize(category.Name);
            }
        }

        var rows = new List<SearchRow>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LinkItem? link in source)
        {
            if (link is null || !seenIds.Add(link.Id))
            {
                continue;
            }

            string name = Normalize(link.Name);
            if (tokens.Length == 0)
            {
                rows.Add(new SearchRow(link, 0, name));
                continue;
            }

            string target = Normalize(link.Target);
            string tags = Normalize(link.Tags);
            string categoryPath = categoryPaths.TryGetValue(link.CategoryId, out string? path) ? path : string.Empty;
            string categoryName = categoryNames.TryGetValue(link.CategoryId, out string? categoryNameValue) ? categoryNameValue : string.Empty;

            if (!tokens.All(token =>
                    name.Contains(token, StringComparison.Ordinal) ||
                    target.Contains(token, StringComparison.Ordinal) ||
                    tags.Contains(token, StringComparison.Ordinal) ||
                    categoryPath.Contains(token, StringComparison.Ordinal)))
            {
                continue;
            }

            int matchRank = Rank(normalizedQuery, tokens, name, target, tags, categoryName, categoryPath);
            rows.Add(new SearchRow(link, matchRank, name));
        }

        if (tokens.Length == 0)
        {
            return rows
                .OrderByDescending(row => row.Link.IsFavorite)
                .ThenBy(row => row.NormalizedName, StringComparer.Ordinal)
                .ThenBy(row => row.Link.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Link.Id, StringComparer.Ordinal)
                .Select(row => row.Link)
                .ToArray();
        }

        return rows
            .OrderBy(row => row.MatchRank)
            .ThenByDescending(row => row.Link.IsFavorite)
            .ThenByDescending(row => row.Link.OpenCount)
            .ThenByDescending(row => row.Link.LastOpenedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.NormalizedName, StringComparer.Ordinal)
            .ThenBy(row => row.Link.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Link.Id, StringComparer.Ordinal)
            .Select(row => row.Link)
            .ToArray();
    }

    public static string GetCategoryPath(Library library, string? categoryId)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (string.IsNullOrWhiteSpace(categoryId) || library.Categories is null)
        {
            return string.Empty;
        }

        Dictionary<string, string> paths = BuildCategoryPaths(library.Categories);
        return paths.TryGetValue(categoryId, out string? path) ? path : string.Empty;
    }

    public static IReadOnlyDictionary<string, string> GetCategoryPaths(Library library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return BuildCategoryPaths(library.Categories ?? new List<Category>());
    }

    public static HashSet<string> DescendantIds(Library library, string categoryId)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!(library.Categories ?? new List<Category>()).Any(category =>
                category is not null && category.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase)))
        {
            return result;
        }

        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Category category in library.Categories ?? new List<Category>())
        {
            if (string.IsNullOrWhiteSpace(category.ParentId))
            {
                continue;
            }

            if (!children.TryGetValue(category.ParentId, out List<string>? ids))
            {
                ids = new List<string>();
                children.Add(category.ParentId, ids);
            }

            ids.Add(category.Id);
        }

        var pending = new Stack<string>();
        pending.Push(categoryId);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!result.Add(current) || !children.TryGetValue(current, out List<string>? ids))
            {
                continue;
            }

            foreach (string childId in ids)
            {
                pending.Push(childId);
            }
        }

        return result;
    }

    private static int Rank(string query, string[] tokens, string name, string target, string tags, string categoryName, string categoryPath)
    {
        if (name.Equals(query, StringComparison.Ordinal)) return 0;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 1;
        if (name.Contains(query, StringComparison.Ordinal)) return 2;
        if (tokens.All(token => name.Contains(token, StringComparison.Ordinal))) return 3;
        if (tokens.All(token => categoryName.Contains(token, StringComparison.Ordinal))) return 4;
        if (tokens.All(token => categoryPath.Contains(token, StringComparison.Ordinal))) return 5;
        if (tokens.All(token => tags.Contains(token, StringComparison.Ordinal))) return 6;
        if (target.Contains(query, StringComparison.Ordinal)) return 7;
        if (tokens.All(token => target.Contains(token, StringComparison.Ordinal))) return 8;
        return 9;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }

    private static Dictionary<string, string> BuildCategoryPaths(IEnumerable<Category> source)
    {
        var categories = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        foreach (Category? category in source)
        {
            if (category is not null && !string.IsNullOrWhiteSpace(category.Id))
            {
                categories.TryAdd(category.Id, category);
            }
        }

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Category start in categories.Values)
        {
            if (paths.ContainsKey(start.Id))
            {
                continue;
            }

            var chain = new List<Category>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Category? current = start;
            while (current is not null && !paths.ContainsKey(current.Id) && seen.Add(current.Id))
            {
                chain.Add(current);
                current = string.IsNullOrWhiteSpace(current.ParentId) || !categories.TryGetValue(current.ParentId, out Category? parent)
                    ? null
                    : parent;
            }

            string prefix = current is not null && paths.TryGetValue(current.Id, out string? knownPath)
                ? knownPath
                : string.Empty;
            for (int index = chain.Count - 1; index >= 0; index--)
            {
                Category category = chain[index];
                prefix = string.IsNullOrEmpty(prefix) ? category.Name ?? string.Empty : prefix + " / " + category.Name;
                paths[category.Id] = prefix;
            }
        }

        return paths;
    }

    private sealed record SearchRow(LinkItem Link, int MatchRank, string NormalizedName);
}
