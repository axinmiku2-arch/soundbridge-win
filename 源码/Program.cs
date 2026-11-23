namespace AudioRelay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool startedAtSignIn = args.Contains(StartupRegistration.LaunchFlag, StringComparer.OrdinalIgnoreCase);
        ApplicationConfiguration.Initialize();
        using var oneInstance = new Mutex(true, @"Local\AudioRelay.v1", out bool first);
        if (!first)
        {
            if (startedAtSignIn) return;
            MessageBox.Show("声桥已在运行。请从任务栏打开原窗口，避免重复播放。",
                "声桥", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.Run(new MainForm(startedAtSignIn));
    }
}
