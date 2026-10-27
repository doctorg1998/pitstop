using System.Diagnostics;
using System.IO;
using System.Text;
using Pitstop.Core.Profiles;
using Pitstop.Windows;

namespace Pitstop.App;

/// <summary>
/// `Pitstop.App.exe --diag [file]`: a support snapshot of what profiles can control on this PC.
/// It changes nothing. The two "set" calls re-apply the current power plan and audio device,
/// which proves the API path works without disturbing the user.
/// </summary>
internal static class Diagnostics
{
    public static int Run(string outputFile)
    {
        var sb = new StringBuilder($"Pitstop diagnostics {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\n\n");
        var failures = 0;

        void Step(string title, Func<string> body)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = body(); // run before formatting, or the timing is read too early
                sb.Append($"[ OK ] {title} ({sw.Elapsed.TotalMilliseconds:0.0} ms)\n{Indent(result)}\n");
            }
            catch (Exception ex) { failures++; sb.Append($"[FAIL] {title}: {ex.GetType().Name}: {ex.Message}\n\n"); }
        }

        Step("Power plans", () =>
        {
            var active = WindowsPowerPlans.GetActive();
            return string.Join("\n", WindowsPowerPlans.List().Select(p => $"{(p.Id == active ? "*" : " ")} {p.Name}  {p.Id}"));
        });
        Step("Re-apply current power plan (no change)", () =>
        {
            var active = WindowsPowerPlans.GetActive();
            WindowsPowerPlans.SetActive(active);
            return WindowsPowerPlans.GetActive() == active ? "unchanged, as expected" : throw new Exception("plan changed!");
        });
        foreach (var flow in new[] { WindowsAudioDevices.DataFlow.Render, WindowsAudioDevices.DataFlow.Capture })
        {
            var label = flow == WindowsAudioDevices.DataFlow.Render ? "outputs" : "inputs";
            Step($"Audio {label}", () =>
            {
                var def = WindowsAudioDevices.GetDefaultId(flow, WindowsAudioDevices.Role.Console);
                return string.Join("\n", WindowsAudioDevices.List(flow).Select(d => $"{(d.Id == def ? "*" : " ")} {d.Name}"));
            });
        }
        Step("Re-apply current default output (no change)", () =>
        {
            var def = WindowsAudioDevices.GetDefaultId(WindowsAudioDevices.DataFlow.Render, WindowsAudioDevices.Role.Console)
                ?? throw new Exception("no default output");
            WindowsAudioDevices.SetDefaultId(def, WindowsAudioDevices.Role.Console);
            return WindowsAudioDevices.GetDefaultId(WindowsAudioDevices.DataFlow.Render, WindowsAudioDevices.Role.Console) == def
                ? "unchanged, as expected" : throw new Exception("default changed!");
        });
        Step("Notifications", () => WindowsNotificationToggle.Read() switch
        {
            null => "ToastEnabled not set (Windows default: on)",
            0 => "ToastEnabled = 0 (muted)",
            var v => $"ToastEnabled = {v} (on)",
        });
        Step("Process watcher: one poll", () =>
        {
            using var watcher = new PollingProcessWatcher();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            watcher.Start();
            sw.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            return $"{watcher.AlreadyRunning.Count} processes readable, first poll {sw.Elapsed.TotalMilliseconds:0.0} ms wall, {cpu.TotalMilliseconds:0.0} ms CPU";
        });
        Step("Hotkey parser", () => Hotkey.TryParse("Ctrl+Alt+P", out var hk) ? $"{hk!.Text} -> VK 0x{hk.VirtualKey:X2}" : throw new Exception("parse failed"));

        sb.Append(failures == 0 ? "All checks passed.\n" : $"{failures} check(s) failed.\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
        File.WriteAllText(outputFile, sb.ToString());
        return failures == 0 ? 0 : 3;
    }

    private static string Indent(string text) =>
        string.Join("\n", text.Split('\n').Select(l => "       " + l)) + "\n";
}
