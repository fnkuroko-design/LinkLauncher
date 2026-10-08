using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LinkLauncher;
using LinkLauncher.Models;
using LinkLauncher.Services;
using WpfDataObject = System.Windows.DataObject;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();

        string tempDirectory = Path.Combine(Path.GetTempPath(), "LinkLauncher-EditorDropChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string filePath = Path.Combine(tempDirectory, "sample.txt");
            File.WriteAllText(filePath, "temporary check file");
            string folderPath = Path.Combine(tempDirectory, "sample-folder");
            Directory.CreateDirectory(folderPath);

            Check("ファイルドロップは入力欄からWindowへトンネルし、空名を補完する", () =>
                CheckFileDrop(filePath));
            Check("名前欄へのフォルダドロップは既存名を維持する", () =>
                CheckFolderDrop(folderPath));
            Check("複数ドロップは先頭を使わず案内する", () =>
                CheckMultipleDrop(filePath, folderPath));
            Check("存在しないパス・並び替えデータ混在・テキストは入力を変えない", () =>
                CheckRejectedDrops(tempDirectory, filePath));

            Console.WriteLine($"PASS {_checks}/4");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            string resolvedDirectory = Path.GetFullPath(tempDirectory);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolvedDirectory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedDirectory).StartsWith("LinkLauncher-EditorDropChecks-", StringComparison.Ordinal))
                throw new InvalidOperationException("検証専用フォルダ以外の削除を中止しました。");
            if (Directory.Exists(resolvedDirectory)) Directory.Delete(resolvedDirectory, recursive: true);
        }
    }

    private static void CheckFileDrop(string filePath)
    {
        Library library = Library.CreateDefault();
        int originalLinkCount = library.Links.Count;
        Window editor = CreateEditor(library);
        TextBox name = Field<TextBox>(editor, "_name");
        TextBox target = Field<TextBox>(editor, "_target");
        DragEventArgs args = Raise(target, FileDrop(filePath), DragDrop.PreviewDropEvent);

        Require(args.Handled, "WindowのPreviewDropが入力欄から届いていません。");
        Require(args.Effects == DragDropEffects.Copy, "ファイルドロップがCopyとして扱われません。");
        Require(target.Text == LibraryStore.NormalizeTarget(filePath), "リンク先欄に正規化したファイルパスが入りません。");
        Require(name.Text == "sample.txt", "空の名前がファイル名で補完されません。");
        Require(ReadResult(editor) is null, "ドロップだけでリンクが確定しています。");
        Require(library.Links.Count == originalLinkCount, "ドロップだけでライブラリが変更されています。");
    }

    private static void CheckFolderDrop(string folderPath)
    {
        Window editor = CreateEditor(Library.CreateDefault());
        TextBox name = Field<TextBox>(editor, "_name");
        TextBox target = Field<TextBox>(editor, "_target");
        name.Text = "入力済みの名前";
        DragEventArgs args = Raise(name, FileDrop(folderPath), DragDrop.PreviewDropEvent);

        Require(args.Handled, "名前欄上のフォルダドロップがWindowに届いていません。");
        Require(target.Text == LibraryStore.NormalizeTarget(folderPath), "名前欄上のドロップでリンク先欄が更新されません。");
        Require(name.Text == "入力済みの名前", "ドロップで入力済みの名前が上書きされました。");
        Require(LibraryStore.DetectKind(target.Text) == LinkKind.Folder, "フォルダのリンク種別を判定できません。");
    }

    private static void CheckMultipleDrop(string filePath, string folderPath)
    {
        Window editor = CreateEditor(Library.CreateDefault());
        TextBox name = Field<TextBox>(editor, "_name");
        TextBox target = Field<TextBox>(editor, "_target");
        TextBlock error = Field<TextBlock>(editor, "_error");
        name.Text = "そのままの名前";
        target.Text = "そのままのリンク先";

        DragEventArgs over = Raise(target, FileDrop(filePath, folderPath), DragDrop.PreviewDragOverEvent);
        DragEventArgs drop = Raise(target, FileDrop(filePath, folderPath), DragDrop.PreviewDropEvent);

        Require(over.Effects == DragDropEffects.Copy, "複数件ドロップを受け付ける表示になりません。");
        Require(drop.Handled, "複数件ドロップが処理されません。");
        Require(name.Text == "そのままの名前" && target.Text == "そのままのリンク先", "複数件の先頭がフォームに入力されました。");
        Require(error.Text.Contains("1つずつ", StringComparison.Ordinal), "1件ずつドロップする案内が表示されません。");
        Require(ReadResult(editor) is null, "複数件ドロップだけでリンクが確定しています。");
    }

    private static void CheckRejectedDrops(string tempDirectory, string filePath)
    {
        Window editor = CreateEditor(Library.CreateDefault());
        TextBox name = Field<TextBox>(editor, "_name");
        TextBox target = Field<TextBox>(editor, "_target");
        name.Text = "保持する名前";
        target.Text = "保持するリンク先";

        string missingPath = Path.Combine(tempDirectory, "missing.txt");
        DragEventArgs missingOver = Raise(target, FileDrop(missingPath), DragDrop.PreviewDragOverEvent);
        DragEventArgs missingDrop = Raise(target, FileDrop(missingPath), DragDrop.PreviewDropEvent);
        Require(missingOver.Effects == DragDropEffects.Copy, "FileDropのdrag-overがCopyとして処理されません。");
        Require(missingDrop.Handled && target.Text == "保持するリンク先" && name.Text == "保持する名前",
            "存在しないパスでフォームが上書きされました。");

        var mixedData = FileDrop(filePath);
        mixedData.SetData("LinkLauncher.LinkOrder.v1", "internal-link-id", autoConvert: false);
        DragEventArgs mixedOver = Raise(target, mixedData, DragDrop.PreviewDragOverEvent);
        DragEventArgs mixedDrop = Raise(name, mixedData, DragDrop.PreviewDropEvent);
        Require(mixedOver.Effects == DragDropEffects.None, "内部並び替え形式を含むデータがdrag-overで許可されました。");
        Require(mixedDrop.Handled && target.Text == "保持するリンク先" && name.Text == "保持する名前",
            "内部並び替えデータを含むドロップでフォームが上書きされました。");

        var textData = new WpfDataObject();
        textData.SetData(DataFormats.UnicodeText, "unsupported text", autoConvert: false);
        DragEventArgs textOver = Raise(target, textData, DragDrop.PreviewDragOverEvent);
        DragEventArgs textDrop = Raise(target, textData, DragDrop.PreviewDropEvent);
        Require(textOver.Effects == DragDropEffects.None, "非FileDropがdrag-overで許可されました。");
        Require(textDrop.Handled && target.Text == "保持するリンク先" && name.Text == "保持する名前",
            "非FileDropデータでフォームが上書きされました。");
        Require(ReadResult(editor) is null, "拒否したデータだけでリンクが確定しています。");
    }

    private static Window CreateEditor(Library library)
    {
        Type type = typeof(App).Assembly.GetType("LinkLauncher.Views.LinkEditor", throwOnError: true)!;
        object? instance = Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { library, library.Categories[0].Id, null, null },
            culture: null);
        return instance as Window ?? throw new InvalidOperationException("LinkEditorを作成できません。");
    }

    private static T Field<T>(Window editor, string name) where T : class
    {
        FieldInfo field = editor.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"フィールドがありません: {name}");
        return field.GetValue(editor) as T ?? throw new InvalidOperationException($"フィールド型が違います: {name}");
    }

    private static object? ReadResult(Window editor)
    {
        PropertyInfo property = editor.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Resultプロパティがありません。");
        return property.GetValue(editor);
    }

    private static WpfDataObject FileDrop(params string[] paths)
    {
        var data = new WpfDataObject();
        data.SetData(DataFormats.FileDrop, paths, autoConvert: false);
        return data;
    }

    private static DragEventArgs Raise(TextBox source, System.Windows.IDataObject data, RoutedEvent routedEvent)
    {
        ConstructorInfo constructor = typeof(DragEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(System.Windows.IDataObject),
                typeof(DragDropKeyStates),
                typeof(DragDropEffects),
                typeof(DependencyObject),
                typeof(Point)
            },
            modifiers: null)
            ?? throw new InvalidOperationException("WPFのDragEventArgs内部コンストラクターが見つかりません。");
        var args = (DragEventArgs)constructor.Invoke(new object[]
        {
            data,
            DragDropKeyStates.None,
            DragDropEffects.Copy,
            source,
            new Point(0, 0)
        });
        args.RoutedEvent = routedEvent;
        source.RaiseEvent(args);
        Require(!Window.GetWindow(source)!.IsVisible, "チェックでウィンドウを表示しています。");
        return args;
    }

    private static void Check(string name, Action action)
    {
        action();
        _checks++;
        Console.WriteLine("PASS " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
