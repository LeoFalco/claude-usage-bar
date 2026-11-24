namespace ClaudeUsageBar;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var i = Array.IndexOf(args, "--snapshot");
        if (i >= 0 && i + 1 < args.Length) return Snapshot.Render(args[i + 1]) ? 0 : 1;
        var h = Array.IndexOf(args, "--snapshot-hover");
        if (h >= 0 && h + 1 < args.Length) return Snapshot.Render(args[h + 1], hover: true) ? 0 : 1;

        // One tray icon per user session: a second launch (e.g. login item plus a manual start) just exits.
        using var mutex = new Mutex(true, @"Local\ClaudeUsageBar", out var first);
        if (!first) return 0;
        // A tray app has no window to report into: log UI-thread errors instead of the .NET error dialog,
        // and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        Application.Run(new TrayApp());
        return 0;
    }

    /// <summary>Appends to %LOCALAPPDATA%\ClaudeUsageBar\error.log, kept under 1 MB.</summary>
    static void LogError(Exception error)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageBar");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
