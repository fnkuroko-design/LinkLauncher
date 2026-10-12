using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using LinkLauncher.Models;
using LinkLauncher.Services;
using LinkLauncher.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Use a new result directory.");
        Directory.CreateDirectory(output);
        int passed = 0;
        var app = new ResourceOnlyApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var source = XDocument.Load(Path.Combine(root, "src/LinkLauncher/App.xaml")).Root!.Element(ns + "Application.Resources")!;
            var dictionary = new XElement(ns + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), source.Elements());
            app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());

            Check("Recovery save failure preserves backup; successful saves resume rotation", () =>
            {
                string directory = Path.Combine(output, "recovery");
                var writer = new LibraryStore(directory);
                var library = Sample();
                writer.Save(library); writer.Save(library);
                byte[] backup = File.ReadAllBytes(writer.FilePath + ".bak");
                File.WriteAllText(writer.FilePath, "{corrupt");
                var store = new LibraryStore(directory);
                Library recovered = store.Load();
                Assert(store.RecoveryNotice != null && recovered.Links[0].Name == "Original", "Recover valid backup");
                recovered.Links[0].Name = "First edit";
                using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool failed = false;
                    try { store.Save(recovered); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                    Assert(failed, "Locked primary replacement must fail");
                    Assert(File.ReadAllBytes(store.FilePath + ".bak").SequenceEqual(backup), "Valid backup unchanged on failure");
                    Assert(new LibraryStore(directory).Load().Links[0].Name == "Original", "Restart can still recover");
                }
                store.Save(recovered);
                Assert(File.ReadAllBytes(store.FilePath + ".bak").SequenceEqual(backup), "First successful repair retains recovery backup");
                Assert(new LibraryStore(directory).Load().Links[0].Name == "First edit", "Repaired primary readable");
                recovered.Links[0].Name = "Second edit";
                store.Save(recovered);
                Assert(store.ReadImport(store.FilePath + ".bak").Links[0].Name == "First edit", "Normal backup rotation resumes");
            }, ref passed);

            Library normalized = Sample();
            Check("Mixed-case references and whitespace roots preserve editor selection", () =>
            {
                var library = Sample();
                library.Categories[1].ParentId = " \t ";
                library.Categories[2].ParentId = "abc";
                library.Links[0].CategoryId = "child";
                string path = Path.Combine(output, "mixed-case.json");
                File.WriteAllText(path, JsonSerializer.Serialize(library));
                normalized = new LibraryStore(output).ReadImport(path);
                Assert(normalized.Categories[1].ParentId == null && normalized.Categories[2].ParentId == "ABC", "Canonical parent/root");
                Assert(normalized.Links[0].CategoryId == "CHILD", "Canonical link category");
                Assert(SearchEngine.DescendantIds(normalized, "ABC").Contains("CHILD"), "Hierarchy remains searchable");
                var linkEditor = new LinkEditor(normalized, "FIRST", normalized.Links[0]);
                Assert(((CategoryChoice)Field<ComboBox>(linkEditor, "_category").SelectedItem).Id == "CHILD", "Link keeps original category");
                var categoryEditor = new CategoryEditor(normalized, null, normalized.Categories[2]);
                Assert(((CategoryChoice)Field<ComboBox>(categoryEditor, "_parent").SelectedItem).Id == "ABC", "Child keeps original parent");
                Assert(File.ReadAllText(path).Contains("abc"), "Read does not rewrite input JSON");
            }, ref passed);

            Check("Invalid links and failed saves retain entered fields", () =>
            {
                var editor = new LinkEditor(normalized, "ABC");
                TextBox name = Field<TextBox>(editor, "_name"), target = Field<TextBox>(editor, "_target"),
                    tags = Field<TextBox>(editor, "_tags"), note = Field<TextBox>(editor, "_note");
                TextBlock error = Field<TextBlock>(editor, "_error");
                int attempts = 0;
                editor.TrySave = _ => { attempts++; return "Simulated storage failure"; };
                foreach (string invalid in new[] { "name", "tags", "note", "control", "target" })
                {
                    name.Text = invalid == "name" ? new string('N', 201) : invalid == "control" ? "bad\u0001name" : "Edited name";
                    tags.Text = invalid == "tags" ? new string('T', 1001) : "keep tags";
                    note.Text = invalid == "note" ? new string('M', 10001) : "keep memo";
                    target.Text = invalid == "target" ? "javascript:alert(1)" : "https://example.com/";
                    string originalName = name.Text, originalNote = note.Text;
                    Save(editor);
                    Assert(attempts == 0 && editor.Result == null && editor.DialogResult == null, "Reject before close/persistence: " + invalid);
                    Assert(error.Text.Length > 0 && name.Text == originalName && note.Text == originalNote, "Retain invalid draft: " + invalid);
                }
                name.Text = "Edited name"; target.Text = "https://example.com/"; tags.Text = "keep tags"; note.Text = "keep memo";
                Save(editor);
                Assert(attempts == 1 && editor.Result == null && editor.DialogResult == null && error.Text.Contains("storage failure"), "Storage failure remains in editor");
                Assert(name.Text == "Edited name" && note.Text == "keep memo", "Corrected input retained");
            }, ref passed);

            Check("Invalid categories and failed saves remain editable", () =>
            {
                var editor = new CategoryEditor(normalized, null, normalized.Categories[1]);
                TextBox name = Field<TextBox>(editor, "_name");
                int attempts = 0;
                editor.TrySave = _ => { attempts++; return "Simulated storage failure"; };
                name.Text = new string('C', 121); Save(editor);
                Assert(attempts == 0 && name.Text.Length == 121 && editor.Result == null && editor.DialogResult == null, "Category validation before close");
                name.Text = "Corrected category"; Save(editor);
                Assert(attempts == 1 && name.Text == "Corrected category" && editor.Result == null && editor.DialogResult == null, "Category draft kept after failed save");
                Assert(Field<TextBlock>(editor, "_error").Text.Contains("storage failure"), "Failure shown in editor");
            }, ref passed);

            Check("Icon requests use launch normalization without mutating targets", () =>
            {
                string directory = Path.GetFullPath(output);
                const string variable = "LINKLAUNCHER_REVIEW_ICON_DIRECTORY";
                string? old = Environment.GetEnvironmentVariable(variable);
                try
                {
                    Environment.SetEnvironmentVariable(variable, directory);
                    string path = Path.Combine(directory, "app.exe");
                    foreach (string target in new[] { path, "\"" + path + "\"", "<" + path + ">", "%" + variable + "%\\app.exe", new Uri(path).AbsoluteUri })
                    {
                        var request = ShellIconRequest.Create(target, LinkKind.File);
                        Assert(request.HasValue && request.Value.Path == path, "Normalized icon target: " + target);
                    }
                    Assert(ShellIconRequest.Create("relative.exe", LinkKind.File) == null, "Invalid path fallback");
                }
                finally { Environment.SetEnvironmentVariable(variable, old); }
            }, ref passed);
            Console.WriteLine($"PASS {passed}/5");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }

    private static Library Sample() => new()
    {
        Categories = new List<Category>
        {
            new() { Id = "FIRST", Name = "A first" }, new() { Id = "ABC", Name = "B root" },
            new() { Id = "CHILD", Name = "Child", ParentId = "ABC" }
        },
        Links = new List<LinkItem> { new() { Id = "LINK", Name = "Original", CategoryId = "CHILD", Kind = LinkKind.Web, Target = "https://example.com/" } }
    };
    private sealed class ResourceOnlyApp : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Save(object editor) => editor.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
    private static void Check(string name, Action action, ref int count) { action(); count++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
