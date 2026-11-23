using Microsoft.Win32;

namespace AudioRelay;

/// <summary>Current-user opt-in registration; never touches the machine-wide
/// Run list, installs a service, or changes Windows startup policy.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "AudioRelay";
    public const string LaunchFlag = "--startup";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var command = key?.GetValue(Entry, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return string.Equals(command, BuildCommand(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(Entry, throwOnMissingValue: false);
            return;
        }

        string path = Application.ExecutablePath;
        string command = BuildCommand(path);
        if (command.Length > 260)
            throw new InvalidOperationException("程序路径太长。请解压到更短的路径，再开启登录自启。");
        if (!File.Exists(path))
            throw new InvalidOperationException("找不到当前程序文件，不能登记登录自启。");

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new UnauthorizedAccessException("Windows 不允许为此用户设置登录自启。");
        key.SetValue(Entry, command, RegistryValueKind.String);
    }

    private static string BuildCommand(string executable) => $"\"{executable}\" {LaunchFlag}";
}
