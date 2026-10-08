using System;
using System.Collections.Generic;
using System.IO;

namespace LinkLauncher.Models;

public enum LinkKind
{
    Web,
    File,
    Folder
}

public enum MouseActivationPattern
{
    None,
    MiddleClick,
    MiddleThenRight,
    RightThenLeft,
    XButton1,
    XButton2
}

public sealed class LinkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CategoryId { get; set; } = string.Empty;
    public int? Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public LinkKind Kind { get; set; }
    public string Tags { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public DateTimeOffset? LastOpenedUtc { get; set; }
    public int OpenCount { get; set; }
}

public sealed class Category
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentId { get; set; }
    public int? Order { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LauncherSettings
{
    public string Hotkey { get; set; } = "Ctrl + Alt + Space";
    public MouseActivationPattern MousePattern { get; set; } = MouseActivationPattern.MiddleThenRight;
    public bool HideAfterLaunch { get; set; } = true;
    public bool DismissOnDeactivate { get; set; } = true;
}

public sealed class Library
{
    public int SchemaVersion { get; set; } = 1;
    public List<Category> Categories { get; set; } = new();
    public List<LinkItem> Links { get; set; } = new();
    public LauncherSettings Settings { get; set; } = new();

    public static Library CreateDefault()
    {
        var sample = new Category { Name = "サンプル" };
        var web = new Category { ParentId = sample.Id, Name = "Web" };
        var folders = new Category { ParentId = sample.Id, Name = "フォルダ" };
        var projects = new Category { Name = "案件" };
        var library = new Library
        {
            Categories = new List<Category> { sample, web, folders, projects },
            Links = new List<LinkItem>
            {
                new()
                {
                    CategoryId = web.Id,
                    Name = "Microsoft Learn（サンプル）",
                    Target = "https://learn.microsoft.com/",
                    Kind = LinkKind.Web,
                    Tags = "Microsoft, ドキュメント"
                },
                new()
                {
                    CategoryId = web.Id,
                    Name = "GitHub（サンプル）",
                    Target = "https://github.com/",
                    Kind = LinkKind.Web,
                    Tags = "開発, Git"
                }
            }
        };

        AddKnownFolderSample(library, folders, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ドキュメント");

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            AddKnownFolderSample(library, folders, Path.Combine(profile, "Downloads"), "ダウンロード");
        }

        return library;
    }

    private static void AddKnownFolderSample(Library library, Category category, string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        library.Links.Add(new LinkItem
        {
            CategoryId = category.Id,
            Name = $"{name}（サンプル）",
            Target = path,
            Kind = LinkKind.Folder,
            Tags = "フォルダ, サンプル"
        });
    }
}
