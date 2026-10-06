using System;
using System.IO;
using Microsoft.Win32;

namespace LinkLauncher.Services;

/// <summary>
/// 現在のユーザーに対する Windows 起動時登録を管理します。
/// </summary>
public sealed class StartupRegistration
{
    private const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LinkLauncher";
    private const string BackgroundArgument = " --background";

    private readonly string _runKeyPath;

    public StartupRegistration()
        : this(DefaultRunKeyPath)
    {
    }

    internal StartupRegistration(string runKeyPath)
    {
        if (string.IsNullOrWhiteSpace(runKeyPath))
        {
            throw new ArgumentException("レジストリキーのパスを指定してください。", nameof(runKeyPath));
        }

        _runKeyPath = runKeyPath;
    }

    /// <summary>
    /// LinkLauncher の起動時登録が有効かどうかを返します。
    /// </summary>
    public bool IsEnabled => !string.IsNullOrEmpty(ReadCommand());

    /// <summary>
    /// 現在の起動コマンドを返します。値がREG_SZ以外の場合は、内容を保護するため例外を返します。
    /// </summary>
    public string? ReadCommand()
    {
        using RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using RegistryKey? runKey = currentUser.OpenSubKey(_runKeyPath, writable: false);
        if (runKey is null)
        {
            return null;
        }

        RegistryValueSnapshot? value = CaptureValue(runKey);
        if (value is null)
        {
            return null;
        }

        if (value.Kind != RegistryValueKind.String)
        {
            throw new InvalidOperationException(
                $"起動時登録の「{ValueName}」値がREG_SZではありません。値を変更せず、現在の型を維持します。");
        }

        return (string)value.Data;
    }

    /// <summary>
    /// 現在の実行ファイルを起動時登録するか、登録を解除します。
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        string? command = enabled ? BuildCurrentProcessCommand() : null;
        ApplyCommand(command, rejectNonStringValue: true);
    }

    /// <summary>
    /// 指定したコマンドを復元します。null の場合は LinkLauncher の値だけを削除します。
    /// </summary>
    public void RestoreCommand(string? command)
    {
        ApplyCommand(command, rejectNonStringValue: false);
    }

    private void ApplyCommand(string? command, bool rejectNonStringValue)
    {
        using RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        RegistryKey? runKey = command is null
            ? currentUser.OpenSubKey(_runKeyPath, writable: true)
            : currentUser.CreateSubKey(_runKeyPath, writable: true);

        if (runKey is null)
        {
            if (command is null)
            {
                return;
            }

            throw new InvalidOperationException("起動時登録用のレジストリキーを開けませんでした。");
        }

        using (runKey)
        {
            RegistryValueSnapshot? previous = CaptureValue(runKey);
            if (rejectNonStringValue && previous is not null && previous.Kind != RegistryValueKind.String)
            {
                throw new InvalidOperationException(
                    $"起動時登録の「{ValueName}」値がREG_SZではありません。値を上書きせず、現在の型を維持します。");
            }

            try
            {
                if (command is null)
                {
                    runKey.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                else
                {
                    runKey.SetValue(ValueName, command, RegistryValueKind.String);
                }

                runKey.Flush();
            }
            catch (Exception operationError)
            {
                try
                {
                    RestoreValue(runKey, previous);
                    runKey.Flush();
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "起動時登録の変更に失敗し、変更前の値への復元にも失敗しました。",
                        operationError,
                        rollbackError);
                }

                throw new InvalidOperationException(
                    "起動時登録を変更できませんでした。変更前の値に戻しました。",
                    operationError);
            }
        }
    }

    private static string BuildCurrentProcessCommand()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("現在の実行ファイルのパスを取得できませんでした。");
        }

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(processPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("現在の実行ファイルの絶対パスを取得できませんでした。", error);
        }

        if (string.Equals(Path.GetFileName(executablePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "dotnet.exe から実行中のため、起動時登録できません。LinkLauncher.exe から起動してください。");
        }

        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException("現在の実行ファイルが見つからないため、起動時登録できません。");
        }

        string command = $"\"{executablePath}\"{BackgroundArgument}";
        if (command.Length > 260)
            throw new InvalidOperationException("起動時登録には実行ファイルのパスが長すぎます。アプリを短いパスのフォルダーへ移してから登録してください。");
        return command;
    }

    private static RegistryValueSnapshot? CaptureValue(RegistryKey runKey)
    {
        bool valueExists = false;
        foreach (string name in runKey.GetValueNames())
        {
            if (string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase))
            {
                valueExists = true;
                break;
            }
        }

        if (!valueExists)
        {
            return null;
        }

        RegistryValueKind kind = runKey.GetValueKind(ValueName);
        if (kind is RegistryValueKind.None or RegistryValueKind.Unknown)
        {
            throw new InvalidOperationException(
                $"起動時登録の「{ValueName}」値を読み取れない型です。値を変更せず、現在の状態を維持します。");
        }

        object? data = runKey.GetValue(
            ValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (data is null)
        {
            throw new InvalidOperationException(
                $"起動時登録の「{ValueName}」値を読み取れません。値を変更せず、現在の状態を維持します。");
        }

        return new RegistryValueSnapshot(kind, CloneValue(data));
    }

    private static void RestoreValue(RegistryKey runKey, RegistryValueSnapshot? value)
    {
        if (value is null)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        runKey.SetValue(ValueName, CloneValue(value.Data), value.Kind);
    }

    private static object CloneValue(object value) => value switch
    {
        byte[] bytes => (byte[])bytes.Clone(),
        string[] strings => (string[])strings.Clone(),
        _ => value
    };

    private sealed class RegistryValueSnapshot
    {
        public RegistryValueKind Kind { get; }
        public object Data { get; }

        public RegistryValueSnapshot(RegistryValueKind kind, object data)
        {
            Kind = kind;
            Data = data;
        }
    }
}
