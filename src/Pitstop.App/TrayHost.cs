using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pitstop.Core.Config;
using Pitstop.Core.Dashboard;
using Pitstop.Core.Digest;
using Pitstop.Core.Health;
using Pitstop.Core.Profiles;
using Pitstop.Core.Storage;
using Pitstop.Windows;

namespace Pitstop.App;

/// <summary>Owns everything the running tray app does: icon and menu, profile engine, watchers and hotkeys.</summary>
internal sealed class TrayHost : IDisposable
{
    private static string DataPath(string name) => Path.Combine(Program.DataFolder, name);
    private static readonly string ConfigPath = DataPath("config.json");
    private static readonly string LogPath = DataPath("pitstop.log");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly DiskFileStore _files = new();
    private readonly Control _ui = new(); // marshals engine and watcher events onto the UI thread
    private readonly Icon _idleIcon = DrawIcon(Color.FromArgb(0x4c, 0xc9, 0xf0));
    private readonly Icon _activeIcon = DrawIcon(Color.FromArgb(0x3d, 0xdc, 0x97));

    private NotifyIcon _tray = null!;
    private ProfileEngine _engine = null!;
    private PollingProcessWatcher _watcher = null!;
    private HotkeyManager _hotkeys = null!;
    private FileSystemWatcher _configWatcher = null!;
    // 10 s keeps break reminders within 10 s of their time (30 s let them run up to 30 s late; BUG-007).
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 10_000 };
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 600 };
    private PitstopConfig _config = new();
    private DashboardForm? _dashboard;
    private RegisteredWaitHandle? _showListener;
    private bool _healthCheckRunning, _disposed;

    /// <param name="showDashboard">False when started with Windows (stay quietly in the tray).</param>
    /// <param name="showRequests">Signalled when the user launches Pitstop again while it's already running.</param>
    public void Start(bool showDashboard, EventWaitHandle showRequests)
    {
        _ui.CreateControl();
        Directory.CreateDirectory(Program.DataFolder);
        Log("Pitstop started.");
        var startupWarnings = new List<string>();

        // 1. Undo anything a previous session left applied (crash or power loss mid-profile).
        var journal = new RevertJournal(_files, DataPath("revert-journal.json"));
        if (journal.Load().Count > 0)
        {
            Log("Previous session didn't exit cleanly; restoring its settings.");
            startupWarnings.AddRange(journal.RecoverPending(new WindowsRestoreHandler()));
        }

        // 2. Config: write the starter profiles on first run.
        var loader = new ConfigLoader(_files);
        if (!File.Exists(ConfigPath)) loader.Save(ConfigPath, ConfigLoader.CreateStarter());
        var load = loader.Load(ConfigPath);
        _config = load.Config;
        startupWarnings.AddRange(load.Warnings.Select(w => w + " Pitstop is using default settings until it's fixed."));

        // 3. Engine.
        var runner = new ActionRunner(new WindowsPowerPlans(), new WindowsAudioDevices(), new WindowsNotificationToggle(), new WindowsAppControl());
        _engine = new ProfileEngine(_config.Profiles, runner, new SystemClock(), journal, new SessionLog(_files, DataPath("sessions.jsonl")));
        _engine.ProfileChanged += (_, c) => OnUi(() => OnProfileChanged(c));
        _engine.BreakReminderDue += (_, p) => OnUi(() =>
        {
            Log($"Break reminder ({p.Name}).");
            new BreakReminderForm(p.Name, p.Actions.BreakReminderMinutes ?? 0).Show();
        });

        // 4. Tray icon.
        _tray = new NotifyIcon { Icon = _idleIcon, Text = "Pitstop: no profile active", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu(_tray.ContextMenuStrip);
        _tray.DoubleClick += (_, _) => OpenDashboard();
        BuildMenu(_tray.ContextMenuStrip);

        // 5. Hotkeys.
        _hotkeys = new HotkeyManager();
        startupWarnings.AddRange(RegisterHotkeys());

        // 6. Process watcher, seeded with apps that were already open.
        _watcher = new PollingProcessWatcher();
        _watcher.ProcessStarted += (_, e) => _engine.OnProcessStarted(e);
        _watcher.ProcessExited += (_, e) => _engine.OnProcessExited(e);
        _watcher.Start();
        foreach (var (pid, exe) in _watcher.AlreadyRunning) _engine.OnProcessStarted(new ProcessEvent(exe, pid, DateTimeOffset.Now));

        // 7. Break reminder clock and live config reload.
        _tick.Tick += (_, _) => _engine.Tick();
        _tick.Start();
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadConfig(); };
        _configWatcher = new FileSystemWatcher(Program.DataFolder, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler onChange = (_, _) => OnUi(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });
        _configWatcher.Changed += onChange;
        _configWatcher.Created += onChange;
        _configWatcher.Renamed += (s, e) => onChange(s, e);

        foreach (var w in startupWarnings) Log("Warning: " + w);
        if (startupWarnings.Count > 0) Balloon("Pitstop started with warnings", string.Join("\n", startupWarnings), ToolTipIcon.Warning);

        // 8. A second launch (Start menu, desktop shortcut) asks this instance to show its window.
        _showListener = ThreadPool.RegisterWaitForSingleObject(showRequests, (_, _) => OnUi(OpenDashboard), null, Timeout.Infinite, executeOnlyOnce: false);
        if (showDashboard) OpenDashboard();
    }

    private void OpenDashboard()
    {
        if (_disposed) return;
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm(BuildDashboardState, HandleDashboardCommand);
            _dashboard.FormClosed += (_, _) => { _dashboard?.Dispose(); _dashboard = null; };
        }
        _dashboard.Reveal();
    }

    private void PushDashboard()
    {
        if (_dashboard is { IsDisposed: false } d) d.PushState();
    }

    private void HandleDashboardCommand(DashboardCommand cmd)
    {
        switch (cmd.Type)
        {
            case "runCheck":
                _ = RunHealthCheckAsync(openReport: false);
                break;
            case "toggleProfile":
                if (_config.Profiles.Any(p => p.Id.Equals(cmd.ProfileId, StringComparison.OrdinalIgnoreCase)))
                    _engine.ToggleManual(cmd.ProfileId!);
                break;
            case "setAutostart":
                SetAutostart(cmd.On == true);
                PushDashboard();
                break;
            case "setIncludeMachineName":
                _config.Privacy.IncludeMachineNameInReports = cmd.On == true;
                new ConfigLoader(_files).Save(ConfigPath, _config); // the config watcher reloads and pushes state
                break;
            case "openReport":
                if (LatestReportFile(".html") is { } html) Open(html, null);
                else _dashboard?.Toast("No report yet. Run a Health Check first.", "warn");
                break;
            case "openConfig":
                Open("notepad.exe", ConfigPath);
                break;
            case "openFolder":
                Open("explorer.exe", Program.DataFolder);
                break;
            case "openUrl":
                Open(cmd.Url!, null); // already checked against the link allowlist
                break;
        }
    }

    private static string? LatestReportFile(string extension)
    {
        var dir = DataPath("reports");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "pitstop-health-*" + extension).OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;
    }

    /// <summary>Everything the dashboard shows, as one JSON-ready snapshot.</summary>
    private object BuildDashboardState()
    {
        var now = DateTimeOffset.Now;
        var reportsDir = DataPath("reports");
        var reportFiles = Directory.Exists(reportsDir)
            ? Directory.EnumerateFiles(reportsDir, "pitstop-health-*.json").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();
        var summaries = reportFiles.Select(f => DigestBuilder.ReadReport(File.ReadAllText(f))).OfType<HealthSummary>().ToList();

        object? report = null;
        if (reportFiles.Count > 0 && ReportWriter.FromJson(File.ReadAllText(reportFiles[^1])) is { } latest)
        {
            var score = HealthScore.Compute(latest.Findings);
            report = new
            {
                takenAt = latest.TakenAt,
                score,
                grade = HealthScore.Grade(score),
                findings = latest.Findings.Select(f => new { f.CheckId, f.Title, severity = f.Severity.ToString(), f.Summary, f.WhatItMeans, f.WhatToDo }),
            };
        }

        var sessions = new SessionLog(_files, DataPath("sessions.jsonl")).ReadAll();
        var digest = DigestBuilder.Build(sessions, summaries, now);
        var active = _engine.Active;

        return new
        {
            type = "state",
            version = Application.ProductVersion.Split('+')[0],
            autostart = IsAutostartEnabled(),
            includeMachineName = _config.Privacy.IncludeMachineNameInReports,
            activeProfileId = active?.Id,
            manualProfileId = _engine.ManualProfileId,
            checkRunning = _healthCheckRunning,
            memTotalGb = MetricsSampler.TotalMemoryGb(),
            uptimeSec = Environment.TickCount64 / 1000,
            drives = MetricsSampler.Drives().Select(d => new { d.Name, d.Label, totalGb = d.TotalBytes / 1e9, freeGb = d.FreeBytes / 1e9 }),
            profiles = _config.Profiles.Select(p => new
            {
                p.Id, p.Name, p.Priority,
                hotkey = Hotkey.TryParse(p.Triggers.Hotkey, out var hk) ? hk!.Text : null,
                processes = p.Triggers.Processes,
                actions = p.Actions,
            }),
            report,
            history = summaries.TakeLast(10).Select(s => new { s.TakenAt, score = HealthScore.FromCounts(s.Action, s.Watch), s.Action, s.Watch }),
            week = digest.TimePerProfile.Select(p => new { name = p.ProfileName, minutes = Math.Round(p.Time.TotalMinutes, 1), p.Sessions }),
            sessions = sessions.OrderByDescending(s => s.Start).Take(6)
                .Select(s => new { name = s.ProfileName, start = s.Start, minutes = Math.Round(s.Duration.TotalMinutes, 1), trigger = s.Trigger }),
        };
    }

    private void OnUi(Action action)
    {
        if (_disposed || !_ui.IsHandleCreated) return;
        try { _ui.BeginInvoke(action); } catch (InvalidOperationException) { /* shutting down */ }
    }

    private IEnumerable<string> RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        foreach (var profile in _config.Profiles)
        {
            if (!Hotkey.TryParse(profile.Triggers.Hotkey, out var hk)) continue;
            var id = profile.Id;
            if (!_hotkeys.TryRegister(hk!, () => _engine.ToggleManual(id)))
                yield return $"Hotkey {hk!.Text} for '{profile.Name}' is already used by another app.";
        }
    }

    private void ReloadConfig()
    {
        var load = new ConfigLoader(_files).Load(ConfigPath);
        if (load.UsedDefaults && load.Warnings.Count > 0)
        {
            // Keep the profiles that were working rather than dropping to empty defaults mid-session.
            var message = load.Warnings[0] + " Your previous profiles are still active.";
            Log("Config reload rejected: " + message);
            Balloon("Profile changes not applied", message, ToolTipIcon.Warning);
            return;
        }
        _config = load.Config;
        _engine.UpdateProfiles(_config.Profiles);
        var warnings = RegisterHotkeys().ToList();
        Log($"Config reloaded ({_config.Profiles.Count} profiles).");
        PushDashboard();
        Balloon("Profiles updated", warnings.Count == 0 ? $"{_config.Profiles.Count} profiles loaded." : string.Join("\n", warnings),
            warnings.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void OnProfileChanged(ProfileChange change)
    {
        if (_disposed) return;
        var current = change.Current;
        _tray.Icon = current is null ? _idleIcon : _activeIcon;
        _tray.Text = StatusText();

        var what = current is null ? $"{change.Previous?.Name ?? "Profile"} off" : $"{current.Name} on";
        Log($"{what} ({change.Trigger}).");
        foreach (var w in change.Warnings) Log("  Warning: " + w);
        PushDashboard();
        if (_dashboard is { IsDisposed: false } d)
        {
            var friendly = current is null ? $"{change.Previous?.Name} profile turned off." : $"{current.Name} profile turned on.";
            d.Toast(change.Warnings.Count > 0 ? $"{friendly} Some settings couldn't be changed: {string.Join(" ", change.Warnings)}" : friendly,
                change.Warnings.Count > 0 ? "warn" : "");
        }

        if (!ReferenceEquals(change.Previous, change.Current) || change.Warnings.Count > 0)
            Balloon(what, change.Warnings.Count > 0 ? string.Join("\n", change.Warnings) : $"Triggered by {change.Trigger}.",
                change.Warnings.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.None);
    }

    private string StatusText() =>
        Truncate(_engine.Active is { } a ? $"Pitstop: {a.Name} active" : "Pitstop: no profile active", 63);

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var active = _engine.Active;
        var manual = _engine.ManualProfileId;

        var open = new ToolStripMenuItem("Open Pitstop") { Font = new Font(menu.Font, FontStyle.Bold) };
        open.Click += (_, _) => OpenDashboard();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripMenuItem(active is null ? "No profile active" : $"Active: {active.Name}{(manual is null ? "" : " (manual)")}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var check = new ToolStripMenuItem(_healthCheckRunning ? "Health Check running…" : "Run Health Check") { Enabled = !_healthCheckRunning };
        check.Click += async (_, _) => { OpenDashboard(); await RunHealthCheckAsync(openReport: false); };
        menu.Items.Add(check);
        menu.Items.Add("Weekly digest", null, (_, _) => OpenWeeklyDigest());

        var profiles = new ToolStripMenuItem("Profiles");
        foreach (var p in _config.Profiles)
        {
            var item = new ToolStripMenuItem(p.Name) { Checked = active?.Id == p.Id };
            if (Hotkey.TryParse(p.Triggers.Hotkey, out var hk)) item.ShortcutKeyDisplayString = hk!.Text;
            var id = p.Id;
            item.Click += (_, _) => _engine.ToggleManual(id);
            profiles.DropDownItems.Add(item);
        }
        if (_config.Profiles.Count == 0) profiles.DropDownItems.Add(new ToolStripMenuItem("No profiles yet") { Enabled = false });
        profiles.DropDownItems.Add(new ToolStripSeparator());
        profiles.DropDownItems.Add("Edit profiles…", null, (_, _) => Open("notepad.exe", ConfigPath));
        menu.Items.Add(profiles);

        menu.Items.Add("Open Pitstop folder", null, (_, _) => Open("explorer.exe", Program.DataFolder));
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = IsAutostartEnabled() };
        autostart.Click += (_, _) => SetAutostart(!IsAutostartEnabled());
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { Dispose(); Application.Exit(); });
    }

    private async Task RunHealthCheckAsync(bool openReport)
    {
        if (_healthCheckRunning) return;
        _healthCheckRunning = true;
        _tray.Text = "Pitstop: running Health Check…";
        PushDashboard();
        try
        {
            var report = await Task.Run(() => new HealthCheckRunner(new WindowsSystemProbe()).RunAsync(_config.Privacy.IncludeMachineNameInReports));
            var html = new ReportWriter(_files).Write(report, DataPath("reports"));
            var score = HealthScore.Compute(report.Findings);
            Log($"Health Check: {report.Overall}, score {score} ({report.Count(Severity.Action)} action, {report.Count(Severity.Watch)} watch).");
            if (openReport) Open(html, null);
            _dashboard?.Toast($"Health Check complete: score {score} ({HealthScore.Grade(score)}).");
        }
        catch (Exception ex)
        {
            Log("Health Check failed: " + ex);
            Balloon("Health Check failed", ex.Message, ToolTipIcon.Error);
            _dashboard?.Toast("Health Check failed: " + ex.Message, "error");
        }
        finally
        {
            _healthCheckRunning = false;
            if (!_disposed) _tray.Text = StatusText();
            PushDashboard();
        }
    }

    private void OpenWeeklyDigest()
    {
        try
        {
            var sessions = new SessionLog(_files, DataPath("sessions.jsonl")).ReadAll();
            var reportsDir = DataPath("reports");
            var reports = Directory.Exists(reportsDir)
                ? Directory.EnumerateFiles(reportsDir, "pitstop-health-*.json")
                    .Select(f => DigestBuilder.ReadReport(File.ReadAllText(f))).OfType<HealthSummary>().ToList()
                : new List<HealthSummary>();
            var path = DataPath("weekly-digest.html");
            _files.WriteAllTextAtomic(path, DigestBuilder.ToHtml(DigestBuilder.Build(sessions, reports, DateTimeOffset.Now)));
            Open(path, null);
        }
        catch (Exception ex)
        {
            Log("Weekly digest failed: " + ex);
            Balloon("Weekly digest failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Pitstop") is string;
    }

    private static void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue("Pitstop", $"\"{Environment.ProcessPath}\" --tray"); // start quietly, no window
        else key.DeleteValue("Pitstop", throwOnMissingValue: false);
        Log(enable ? "Start with Windows: on." : "Start with Windows: off.");
    }

    private void Balloon(string title, string text, ToolTipIcon icon)
    {
        if (_disposed) return;
        _tray.ShowBalloonTip(5000, title, Truncate(text, 250), icon);
    }

    private static void Open(string target, string? args)
    {
        try { Process.Start(new ProcessStartInfo(target) { Arguments = args ?? "", UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log($"Couldn't open {target}: {ex.Message}"); }
    }

    /// <summary>Activity log for support: profile changes, warnings, reminders. Rolls over at 1 MB.</summary>
    internal static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Program.DataFolder);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                File.Move(LogPath, DataPath("pitstop.old.log"), overwrite: true);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Draws the tray icon at runtime: a dark rounded tile with a coloured "P" (no image assets needed).</summary>
    private static Icon DrawIcon(Color accent)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90); path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90); path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(0x0b, 0x0f, 0x14));
            using var pen = new Pen(accent, 2.5f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(accent);
            var size = g.MeasureString("P", font);
            g.DrawString("P", font, brush, (32 - size.Width) / 2 + 1, (32 - size.Height) / 2);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _showListener?.Unregister(null);
        if (_dashboard is { IsDisposed: false } d) d.Dispose();
        _tick.Stop();
        _reloadDebounce.Stop();
        _configWatcher?.Dispose();
        _watcher?.Dispose();
        _engine?.Dispose(); // reverts the active profile and clears the journal
        _hotkeys?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _idleIcon.Dispose();
        _activeIcon.Dispose();
        _tick.Dispose();
        _reloadDebounce.Dispose();
        _ui.Dispose();
        Log("Pitstop exited.");
    }
}
