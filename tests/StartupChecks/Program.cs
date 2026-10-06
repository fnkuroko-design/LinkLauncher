using LinkLauncher.Services;
using Microsoft.Win32;

// The real Windows Run key is never used by these checks.
const string prefix = @"Software\LinkLauncher.Tests\";
string testKeyPath = prefix + Guid.NewGuid().ToString("N");
int checks = 0;
try
{
    using (var key = Registry.CurrentUser.CreateSubKey(testKeyPath))
        key.SetValue("OtherApp", "untouched", RegistryValueKind.String);
    var startup = new StartupRegistration(testKeyPath);
    Check("登録先を引用しbackground起動、他の値を保持", () =>
    {
        Require(!startup.IsEnabled && startup.ReadCommand() == null);
        startup.SetEnabled(true);
        Require(startup.IsEnabled);
        Require(startup.ReadCommand() == "\"" + Environment.ProcessPath + "\" --background");
        using var key = Registry.CurrentUser.OpenSubKey(testKeyPath)!;
        Require(key.GetValueKind("LinkLauncher") == RegistryValueKind.String);
        Require((string?)key.GetValue("OtherApp") == "untouched");
    });
    Check("設定変更前のコマンドをそのまま復元", () =>
    {
        string previous = @"""C:\Previous Version\LinkLauncher.exe"" --background";
        startup.RestoreCommand(previous);
        startup.SetEnabled(true);
        startup.RestoreCommand(previous);
        Require(startup.ReadCommand() == previous);
    });
    Check("自分の登録のみ解除、二度解除しても他の値を保持", () =>
    {
        startup.SetEnabled(false);
        startup.SetEnabled(false);
        Require(!startup.IsEnabled && startup.ReadCommand() == null);
        using var key = Registry.CurrentUser.OpenSubKey(testKeyPath)!;
        Require((string?)key.GetValue("OtherApp") == "untouched");
    });
    Check("想定外の登録値の型を読み取り時に拒否", () =>
    {
        using (var key = Registry.CurrentUser.OpenSubKey(testKeyPath, writable: true)!)
            key.SetValue("LinkLauncher", 1, RegistryValueKind.DWord);
        try { startup.ReadCommand(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("不正な型の登録値を受け入れました。");
    });
    Console.WriteLine($"PASS {checks}/4");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally
{
    if (testKeyPath.StartsWith(prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(testKeyPath[prefix.Length..], "N", out _))
        Registry.CurrentUser.DeleteSubKeyTree(testKeyPath, throwOnMissingSubKey: false);
}
void Check(string name, Action action) { action(); checks++; Console.WriteLine("PASS " + name); }
void Require(bool condition) { if (!condition) throw new InvalidOperationException("期待結果と一致しません。"); }
