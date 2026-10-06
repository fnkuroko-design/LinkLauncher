using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinkLauncher.Models;

namespace LinkLauncher.Services;

public sealed class LibraryStore
{
    private const int MaxDocumentBytes = 10 * 1024 * 1024;
    private const int MaxCategories = 2_000;
    private const int MaxLinks = 20_000;
    private const int MaxDepth = 128;
    private const int MaxIdLength = 128;
    private const int MaxCategoryNameLength = 120;
    private const int MaxLinkNameLength = 200;
    private const int MaxTargetLength = 4_096;
    private const int MaxTagsLength = 1_000;
    private const int MaxNoteLength = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Regex SchemePattern = new(@"^([A-Za-z][A-Za-z0-9+.-]*):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private bool _preservationFailed;

    public LibraryStore(string? directory = null)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string selectedDirectory = string.IsNullOrWhiteSpace(directory)
            ? (string.IsNullOrWhiteSpace(appData) ? string.Empty : Path.Combine(appData, "LinkLauncher"))
            : directory;

        if (string.IsNullOrWhiteSpace(selectedDirectory))
        {
            throw new InvalidOperationException("アプリケーションデータの保存先を取得できません。");
        }

        DirectoryPath = Path.GetFullPath(selectedDirectory);
        FilePath = Path.Combine(DirectoryPath, "library.json");
    }

    public string DirectoryPath { get; }
    public string FilePath { get; }
    public string? RecoveryNotice { get; private set; }

    public Library Load()
    {
        RecoveryNotice = null;
        _preservationFailed = false;
        try
        {
            FileAttributes attributes = File.GetAttributes(FilePath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                return RecoverPrimary(new InvalidDataException("ライブラリの保存先に同名のフォルダがあります。"));
            }
        }
        catch (FileNotFoundException)
        {
            return LoadWhenPrimaryMissing();
        }
        catch (DirectoryNotFoundException)
        {
            return LoadWhenPrimaryMissing();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return RecoverPrimary(error);
        }

        try
        {
            return ReadValidated(FilePath);
        }
        catch (Exception error) when (IsInvalidLibraryError(error))
        {
            return RecoverPrimary(error);
        }
    }

    public void Save(Library library)
    {
        if (_preservationFailed)
        {
            throw new InvalidOperationException("読み込みに失敗した元データを保護できなかったため、上書き保存を中止しました。元ファイルのアクセス権と保存先を確認してください。");
        }

        ArgumentNullException.ThrowIfNull(library);
        Validate(library);

        byte[] data = JsonSerializer.SerializeToUtf8Bytes(library, JsonOptions);
        if (data.Length > MaxDocumentBytes)
        {
            throw new InvalidDataException($"ライブラリのサイズが上限の {MaxDocumentBytes / (1024 * 1024)} MB を超えています。");
        }

        Directory.CreateDirectory(DirectoryPath);
        WriteAtomically(FilePath, data, createBackup: true);
        RecoveryNotice = null;
    }

    public Library ReadImport(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return DeserializeValidated(ReadUtf8Limited(Path.GetFullPath(path)));
    }

    public void Export(Library library, string path)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate(library);

        string fullPath = Path.GetFullPath(path);
        if (PathsEqual(fullPath, FilePath) || PathsEqual(fullPath, FilePath + ".bak"))
        {
            throw new InvalidOperationException("エクスポート先に現在のライブラリまたはそのバックアップは指定できません。");
        }

        byte[] data = JsonSerializer.SerializeToUtf8Bytes(library, JsonOptions);
        if (data.Length > MaxDocumentBytes)
        {
            throw new InvalidDataException($"ライブラリのサイズが上限の {MaxDocumentBytes / (1024 * 1024)} MB を超えています。");
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidDataException("エクスポート先のフォルダを特定できません。");
        }

        Directory.CreateDirectory(parent);
        WriteAtomically(fullPath, data, createBackup: true);
    }

    public static LinkKind DetectKind(string target)
    {
        string normalized = NormalizeTarget(target);
        if (IsHttpTarget(normalized))
        {
            return LinkKind.Web;
        }

        return Directory.Exists(normalized) ? LinkKind.Folder : LinkKind.File;
    }

    public static string NormalizeTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        string value = target.Trim();

        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1].Trim();
        }

        value = Environment.ExpandEnvironmentVariables(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("リンク先が空です。");
        }

        Match schemeMatch = SchemePattern.Match(value);
        if (schemeMatch.Success)
        {
            string scheme = schemeMatch.Groups[1].Value;
            if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? webUri) || string.IsNullOrWhiteSpace(webUri.Host))
                {
                    throw new InvalidDataException("HTTP または HTTPS のリンク先が正しくありません。");
                }

                string absoluteUri = webUri.AbsoluteUri;
                if (absoluteUri.Length > MaxTargetLength)
                {
                    throw new InvalidDataException($"リンク先は {MaxTargetLength} 文字以内で指定してください。");
                }

                return absoluteUri;
            }

            if (scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? fileUri) || !fileUri.IsFile)
                {
                    throw new InvalidDataException("file URI の形式が正しくありません。");
                }

                return NormalizeWindowsPath(fileUri.LocalPath);
            }

            // C:\... と C:/... は URI のスキームではなく、絶対パスとして扱う。
            if (scheme.Length == 1 && value.Length >= 3 && (value[2] == '\\' || value[2] == '/'))
            {
                return NormalizeWindowsPath(value);
            }

            throw new InvalidDataException("HTTP、HTTPS、file URI、または絶対パスを指定してください。");
        }

        return NormalizeWindowsPath(value);
    }

    private static string NormalizeWindowsPath(string value)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException("ファイルまたはフォルダのリンク先には絶対パスを指定してください。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("ファイルまたはフォルダのパスが正しくありません。", error);
        }

        if (fullPath.Length > MaxTargetLength)
        {
            throw new InvalidDataException($"リンク先は {MaxTargetLength} 文字以内で指定してください。");
        }

        return fullPath;
    }

    private static bool IsHttpTarget(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));

    private Library LoadWhenPrimaryMissing()
    {
        try
        {
            Library backup = ReadValidated(FilePath + ".bak");
            RecoveryNotice = "ライブラリ本体が見つからなかったため、バックアップから復元しました。";
            return backup;
        }
        catch (FileNotFoundException)
        {
            return Library.CreateDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return Library.CreateDefault();
        }
        catch (Exception error) when (IsInvalidLibraryError(error))
        {
            RecoveryNotice = $"ライブラリ本体が見つからず、バックアップも読み込めなかったため、初期ライブラリを表示します。バックアップ: {error.Message}";
            return Library.CreateDefault();
        }
    }

    private Library RecoverPrimary(Exception originalError)
    {
        string preserveNotice = PreserveCorruptCopy(FilePath);
        try
        {
            Library backup = ReadValidated(FilePath + ".bak");
            RecoveryNotice = $"保存データに問題があったため、バックアップから復元しました。{preserveNotice} 詳細: {originalError.Message}";
            return backup;
        }
        catch (FileNotFoundException)
        {
            RecoveryNotice = $"保存データを読み込めなかったため、初期ライブラリを表示します。{preserveNotice} 詳細: {originalError.Message}";
            return Library.CreateDefault();
        }
        catch (DirectoryNotFoundException)
        {
            RecoveryNotice = $"保存データを読み込めなかったため、初期ライブラリを表示します。{preserveNotice} 詳細: {originalError.Message}";
            return Library.CreateDefault();
        }
        catch (Exception backupError) when (IsInvalidLibraryError(backupError))
        {
            RecoveryNotice = $"保存データとバックアップの両方を読み込めませんでした。初期ライブラリを表示します。{preserveNotice} 保存データ: {originalError.Message} バックアップ: {backupError.Message}";
            return Library.CreateDefault();
        }
    }

    private static Library ReadValidated(string path) => DeserializeValidated(ReadUtf8Limited(path));

    private static Library DeserializeValidated(string json)
    {
        Library? library;
        try
        {
            library = JsonSerializer.Deserialize<Library>(json, JsonOptions);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("JSON の形式が正しくありません。", error);
        }

        if (library is null)
        {
            throw new InvalidDataException("ライブラリデータが空です。");
        }

        Validate(library);
        return library;
    }

    private static string ReadUtf8Limited(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxDocumentBytes)
        {
            throw new InvalidDataException($"JSON ファイルは {MaxDocumentBytes / (1024 * 1024)} MB 以下にしてください。");
        }

        using var content = new MemoryStream();
        byte[] buffer = new byte[8 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (content.Length + read > MaxDocumentBytes)
            {
                throw new InvalidDataException($"JSON ファイルは {MaxDocumentBytes / (1024 * 1024)} MB 以下にしてください。");
            }

            content.Write(buffer, 0, read);
        }

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(content.ToArray());
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("JSON ファイルは UTF-8 で保存してください。", error);
        }

        return json.Length > 0 && json[0] == '\uFEFF' ? json[1..] : json;
    }

    private static void Validate(Library library)
    {
        if (library.SchemaVersion != 1)
        {
            throw new InvalidDataException($"未対応のライブラリ形式です（SchemaVersion={library.SchemaVersion}）。");
        }

        if (library.Categories is null || library.Links is null || library.Settings is null)
        {
            throw new InvalidDataException("カテゴリ、リンク、または設定のデータがありません。");
        }

        if (library.Categories.Count > MaxCategories || library.Links.Count > MaxLinks)
        {
            throw new InvalidDataException($"カテゴリは {MaxCategories} 件、リンクは {MaxLinks} 件までです。");
        }

        var categories = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        foreach (Category? category in library.Categories)
        {
            if (category is null)
            {
                throw new InvalidDataException("カテゴリ一覧に空の項目があります。");
            }

            ValidateId(category.Id, "カテゴリ");
            ValidateText(category.Name, MaxCategoryNameLength, "カテゴリ名", allowEmpty: false);
            if (!categories.TryAdd(category.Id, category))
            {
                throw new InvalidDataException($"カテゴリ ID が重複しています: {category.Id}");
            }
        }

        foreach (Category category in library.Categories)
        {
            if (!string.IsNullOrWhiteSpace(category.ParentId) && !categories.ContainsKey(category.ParentId))
            {
                throw new InvalidDataException($"親カテゴリが見つかりません: {category.ParentId}");
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Category? current = category;
            int depth = 0;
            while (current is not null)
            {
                if (!visited.Add(current.Id))
                {
                    throw new InvalidDataException($"カテゴリの親子関係に循環があります: {category.Name}");
                }

                if (++depth > MaxDepth)
                {
                    throw new InvalidDataException($"カテゴリの階層は {MaxDepth} 段までです。");
                }

                current = string.IsNullOrWhiteSpace(current.ParentId) ? null : categories[current.ParentId];
            }
        }

        var linkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LinkItem? link in library.Links)
        {
            if (link is null)
            {
                throw new InvalidDataException("リンク一覧に空の項目があります。");
            }

            ValidateId(link.Id, "リンク");
            if (!linkIds.Add(link.Id))
            {
                throw new InvalidDataException($"リンク ID が重複しています: {link.Id}");
            }

            if (string.IsNullOrWhiteSpace(link.CategoryId) || !categories.ContainsKey(link.CategoryId))
            {
                throw new InvalidDataException($"リンクのカテゴリが存在しません: {link.Name}");
            }

            ValidateText(link.Name, MaxLinkNameLength, "リンク名", allowEmpty: false);
            ValidateText(link.Target, MaxTargetLength, "リンク先", allowEmpty: false);
            ValidateText(link.Tags, MaxTagsLength, "タグ", allowEmpty: true);
            ValidateText(link.Note, MaxNoteLength, "メモ", allowEmpty: true);
            if (!Enum.IsDefined(link.Kind))
            {
                throw new InvalidDataException($"リンクの種類が正しくありません: {link.Name}");
            }

            if (link.OpenCount < 0)
            {
                throw new InvalidDataException($"リンクの起動回数が負数です: {link.Name}");
            }

            if (link.LastOpenedUtc is DateTimeOffset lastOpened && lastOpened > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                throw new InvalidDataException($"最終起動日時が未来になっています: {link.Name}");
            }

            string normalizedTarget = NormalizeTarget(link.Target);
            bool isWeb = IsHttpTarget(normalizedTarget);
            if (isWeb != (link.Kind == LinkKind.Web))
            {
                throw new InvalidDataException($"リンクの種類とリンク先が一致しません: {link.Name}");
            }
        }

        ValidateText(library.Settings.Hotkey, 100, "ショートカット", allowEmpty: false);
    }

    private static void ValidateId(string? id, string label)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength || id.Any(char.IsControl))
        {
            throw new InvalidDataException($"{label} ID が正しくありません。");
        }
    }

    private static void ValidateText(string? value, int maxLength, string label, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maxLength || value.Any(char.IsControl) && label is not "メモ")
        {
            throw new InvalidDataException($"{label}は空欄不可、{maxLength} 文字以内で指定してください。");
        }
    }

    private static bool IsInvalidLibraryError(Exception error) =>
        error is InvalidDataException or JsonException or DecoderFallbackException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException;

    private string PreserveCorruptCopy(string path)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string destination = path + ".corrupt-" + timestamp + "-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(path, destination, overwrite: false);
            return $"元データの控えを {Path.GetFileName(destination)} に保存しました。";
        }
        catch (FileNotFoundException)
        {
            return "読み込み中に元データが削除されていたため、控えは不要でした。";
        }
        catch (DirectoryNotFoundException)
        {
            return "読み込み中に元データのフォルダが削除されていたため、控えは不要でした。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _preservationFailed = true;
            return $"元データは変更していませんが、破損データの控えを作成できませんでした（{error.Message}）。";
        }
    }

    private static void WriteAtomically(string path, byte[] data, bool createBackup)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidDataException("保存先のフォルダを特定できません。");
        }

        Directory.CreateDirectory(directory);
        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string backupPath = path + ".bak";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                if (createBackup)
                {
                    string backupTemp = backupPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(path, backupTemp, overwrite: false);
                        File.Move(backupTemp, backupPath, overwrite: true);
                    }
                    finally
                    {
                        TryDelete(backupTemp);
                    }
                }

                File.Move(tempPath, path, overwrite: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        string normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        string normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 一時ファイルの後始末に失敗しても、元データの状態を変えない。
        }
        catch (UnauthorizedAccessException)
        {
            // 一時ファイルの後始末に失敗しても、元データの状態を変えない。
        }
    }
}
