using Pitstop.Core.Config;
using Pitstop.Core.Health;
using Pitstop.Core.Storage;
using Pitstop.Windows;

namespace Pitstop.App;

internal static class Program
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pitstop");

    [STAThread]
    private static int Main(string[] args)
    {
        // Headless mode: `Pitstop.exe --check [outputFolder]` writes a report and exits.
        // Exit code: 0 = OK, 1 = Watch, 2 = Action, 3 = failed to run.
        if (ArgValue(args, "--check", Path.Combine(DataFolder, "reports")) is { } reportFolder)
            return RunHeadlessCheckAsync(reportFolder).GetAwaiter().GetResult();

        // Support snapshot: `Pitstop.exe --diag [file]`.
        if (ArgValue(args, "--diag", Path.Combine(DataFolder, "diagnostics.txt")) is { } diagFile)
            return Diagnostics.Run(diagFile);

        ApplicationConfiguration.Initialize();

        // Tray mode: one instance per user session.
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\Pitstop.Tray", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Pitstop is already running. Look for its icon in the system tray (you may need to click ^).",
                "Pitstop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // Stay alive and keep reverting safely even if something unexpected throws on the UI thread.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => TrayHost.Log("Unexpected error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => TrayHost.Log("Fatal error: " + e.ExceptionObject);

        using var host = new TrayHost();
        // Revert profile changes before Windows signs out or shuts down.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => host.Dispose();
        host.Start();
        Application.Run();
        return 0;
    }

    /// <summary>Returns the value after <paramref name="flag"/> (or the fallback), or null if the flag is absent.</summary>
    private static string? ArgValue(string[] args, string flag, string fallback)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : fallback;
    }

    private static async Task<int> RunHeadlessCheckAsync(string folder)
    {
        try
        {
            var files = new DiskFileStore();
            var config = new ConfigLoader(files).Load(Path.Combine(DataFolder, "config.json")).Config;
            var report = await new HealthCheckRunner(new WindowsSystemProbe())
                .RunAsync(config.Privacy.IncludeMachineNameInReports);
            new ReportWriter(files).Write(report, folder);
            return report.Overall switch { Severity.Ok => 0, Severity.Watch => 1, _ => 2 };
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(DataFolder);
            File.AppendAllText(Path.Combine(DataFolder, "error.log"), $"{DateTimeOffset.Now:O} --check failed: {ex}{Environment.NewLine}");
            return 3;
        }
    }
}
