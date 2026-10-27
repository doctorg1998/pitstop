using Pitstop.Core.Config;
using Pitstop.Core.Profiles;

namespace Pitstop.Tests;

public class ProfileEngineTests
{
    private const string JournalPath = "journal.json", SessionsPath = "sessions.jsonl";

    private readonly FakeSystem _system = new();
    private readonly FakeClock _clock = new();
    private readonly FakeFileStore _files = new();

    private static ProfileDefinition Profile(string id, int priority, string[] exes, Action<ProfileActions>? actions = null)
    {
        var p = new ProfileDefinition { Id = id, Name = id, Priority = priority };
        p.Triggers.Processes.AddRange(exes);
        actions?.Invoke(p.Actions);
        return p;
    }

    private static readonly ProfileDefinition Work = Profile("work", 10, new[] { "EXCEL.EXE" }, a => a.PowerPlan = "Balanced");
    private static readonly ProfileDefinition Play = Profile("play", 20, new[] { "cs2.exe", "eldenring" }, a =>
    {
        a.PowerPlan = "High performance";
        a.AudioOutput = "Headset";
        a.MuteNotifications = true;
        a.CloseApps.Add("OneDrive.exe");
    });
    private static readonly ProfileDefinition Stream = Profile("stream", 20, new[] { "obs64.exe" }, a => a.PowerPlan = "Ultimate");

    private ProfileEngine Engine(params ProfileDefinition[] profiles) =>
        new(profiles, new ActionRunner(_system, _system, _system, _system), _clock,
            new RevertJournal(_files, JournalPath), new SessionLog(_files, SessionsPath));

    private void Start(ProfileEngine e, string exe, int pid) => e.OnProcessStarted(new(exe, pid, _clock.Now));
    private void Exit(ProfileEngine e, string exe, int pid) => e.OnProcessExited(new(exe, pid, _clock.Now));

    [Fact]
    public void Trigger_app_activates_profile_and_exit_reverts_everything_in_reverse()
    {
        var engine = Engine(Play);

        Start(engine, "cs2.exe", 100);
        Assert.Equal("play", engine.Active?.Id);
        Assert.Equal(new[] { "close OneDrive.exe", "power High performance", "output Headset", "mute" }, _system.Log);

        _system.Log.Clear();
        Exit(engine, "cs2.exe", 100);
        Assert.Null(engine.Active);
        Assert.Equal(new[] { "undo mute", "undo output Headset", "undo power High performance", "undo close OneDrive.exe" }, _system.Log);
    }

    [Fact]
    public void Trigger_without_exe_suffix_or_with_full_path_still_matches()
    {
        var engine = Engine(Play);
        Start(engine, @"C:\Games\ELDEN RING\Game\eldenring.exe", 7);
        Assert.Equal("play", engine.Active?.Id);
    }

    [Fact]
    public void Unrelated_processes_are_ignored()
    {
        var engine = Engine(Play);
        Start(engine, "notepad.exe", 1);
        Exit(engine, "notepad.exe", 1);
        Assert.Null(engine.Active);
        Assert.Empty(_system.Log);
    }

    [Fact]
    public void Higher_priority_wins_and_lower_resumes_when_it_exits()
    {
        var engine = Engine(Work, Play);

        Start(engine, "EXCEL.EXE", 1);
        Assert.Equal("work", engine.Active?.Id);
        Start(engine, "cs2.exe", 2);
        Assert.Equal("play", engine.Active?.Id);
        Exit(engine, "cs2.exe", 2);
        Assert.Equal("work", engine.Active?.Id);
    }

    [Fact]
    public void Lower_priority_starting_later_does_not_take_over()
    {
        var engine = Engine(Work, Play);
        Start(engine, "cs2.exe", 2);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Start(engine, "EXCEL.EXE", 1);
        Assert.Equal("play", engine.Active?.Id);
    }

    [Fact]
    public void Equal_priority_tie_goes_to_most_recently_started()
    {
        var engine = Engine(Play, Stream);
        Start(engine, "cs2.exe", 1);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Start(engine, "obs64.exe", 2);
        Assert.Equal("stream", engine.Active?.Id);
    }

    [Fact]
    public void Two_instances_of_trigger_keep_profile_until_both_exit()
    {
        var engine = Engine(Play);
        Start(engine, "cs2.exe", 1);
        Start(engine, "cs2.exe", 2);
        Exit(engine, "cs2.exe", 1);
        Assert.Equal("play", engine.Active?.Id);
        Exit(engine, "cs2.exe", 2);
        Assert.Null(engine.Active);
    }

    [Fact]
    public void Manual_toggle_overrides_running_apps_until_toggled_off()
    {
        var engine = Engine(Work, Play);
        Start(engine, "cs2.exe", 1);

        engine.ToggleManual("work");
        Assert.Equal("work", engine.Active?.Id);

        engine.ToggleManual("work");
        Assert.Equal("play", engine.Active?.Id);
    }

    [Fact]
    public void A_failing_action_is_a_warning_and_the_rest_still_apply()
    {
        _system.FailOn = "output";
        var engine = Engine(Play);
        ProfileChange? change = null;
        engine.ProfileChanged += (_, c) => change = c;

        Start(engine, "cs2.exe", 1);

        Assert.Contains("mute", _system.Log);
        Assert.Contains(change!.Warnings, w => w.StartsWith("Audio output 'Headset'"));
    }

    [Fact]
    public void Protected_process_in_close_list_is_refused_not_closed()
    {
        var p = Profile("bad", 1, new[] { "game.exe" }, a => a.CloseApps.Add("explorer.exe"));
        var engine = Engine(p);
        ProfileChange? change = null;
        engine.ProfileChanged += (_, c) => change = c;

        Start(engine, "game.exe", 1);

        Assert.DoesNotContain("close explorer.exe", _system.Log);
        Assert.Contains(change!.Warnings, w => w.Contains("protected"));
    }

    [Fact]
    public void Journal_holds_changes_while_active_and_is_cleared_after_revert()
    {
        var engine = Engine(Play);
        var journal = new RevertJournal(_files, JournalPath);

        Start(engine, "cs2.exe", 1);
        Assert.Equal(3, journal.Load().Count); // power, audio, toasts (closing an app has nothing to restore)

        Exit(engine, "cs2.exe", 1);
        Assert.Empty(journal.Load());
    }

    [Fact]
    public void After_a_crash_the_journal_restores_settings_newest_first()
    {
        var journal = new RevertJournal(_files, JournalPath);
        journal.Save(new[] { new RestoreRecord(RestoreKinds.PowerPlan, "balanced-guid"), new RestoreRecord(RestoreKinds.Toasts, "1") });

        var restored = new List<string>();
        var warnings = journal.RecoverPending(new LambdaRestore(r => restored.Add(r.Kind)));

        Assert.Equal(new[] { RestoreKinds.Toasts, RestoreKinds.PowerPlan }, restored);
        Assert.Empty(warnings);
        Assert.Empty(journal.Load());
    }

    [Fact]
    public void Corrupt_journal_does_not_crash_startup()
    {
        _files.Files[JournalPath] = "{{{";
        var warnings = new RevertJournal(_files, JournalPath).RecoverPending(new LambdaRestore(_ => throw new Exception()));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sessions_are_logged_with_duration_and_trigger()
    {
        var engine = Engine(Play);
        Start(engine, "cs2.exe", 1);
        _clock.Advance(TimeSpan.FromMinutes(95));
        Exit(engine, "cs2.exe", 1);

        var session = Assert.Single(new SessionLog(_files, SessionsPath).ReadAll());
        Assert.Equal("play", session.ProfileId);
        Assert.Equal(TimeSpan.FromMinutes(95), session.Duration);
        Assert.Equal("cs2.exe started", session.Trigger);
    }

    [Fact]
    public void Session_log_skips_a_line_cut_off_by_power_loss()
    {
        var log = new SessionLog(_files, SessionsPath);
        log.Append(new SessionRecord("work", "Work", _clock.Now, _clock.Now.AddHours(1), "manual"));
        _files.Files[SessionsPath] += "{\"ProfileId\":\"pla";

        Assert.Single(log.ReadAll());
    }

    [Fact]
    public void Break_reminder_fires_on_schedule_and_only_once_per_interval()
    {
        var p = Profile("work", 1, new[] { "EXCEL.EXE" }, a => a.BreakReminderMinutes = 60);
        var engine = Engine(p);
        var reminders = 0;
        engine.BreakReminderDue += (_, _) => reminders++;

        Start(engine, "EXCEL.EXE", 1);
        _clock.Advance(TimeSpan.FromMinutes(59)); engine.Tick();
        Assert.Equal(0, reminders);
        _clock.Advance(TimeSpan.FromMinutes(1)); engine.Tick(); engine.Tick();
        Assert.Equal(1, reminders);
        _clock.Advance(TimeSpan.FromMinutes(60)); engine.Tick();
        Assert.Equal(2, reminders);
    }

    [Fact]
    public void No_break_reminder_without_active_profile()
    {
        var engine = Engine(Work);
        var reminders = 0;
        engine.BreakReminderDue += (_, _) => reminders++;
        _clock.Advance(TimeSpan.FromHours(5)); engine.Tick();
        Assert.Equal(0, reminders);
    }

    [Fact]
    public void Dispose_reverts_active_profile()
    {
        var engine = Engine(Play);
        Start(engine, "cs2.exe", 1);
        _system.Log.Clear();

        engine.Dispose();

        Assert.Contains("undo power High performance", _system.Log);
        Assert.Empty(new RevertJournal(_files, JournalPath).Load());
    }

    [Fact]
    public void Reloading_config_reapplies_changed_active_profile()
    {
        var engine = Engine(Play);
        Start(engine, "cs2.exe", 1);
        _system.Log.Clear();

        var edited = Profile("play", 20, new[] { "cs2.exe" }, a => a.PowerPlan = "Ultimate");
        engine.UpdateProfiles(new[] { edited });

        Assert.Equal(new[] { "undo mute", "undo output Headset", "undo power High performance", "undo close OneDrive.exe", "power Ultimate" }, _system.Log);
    }

    [Fact]
    public void Reloading_config_without_the_trigger_deactivates_profile()
    {
        var engine = Engine(Play);
        Start(engine, "cs2.exe", 1);
        engine.UpdateProfiles(new[] { Work });
        Assert.Null(engine.Active);
    }

    private sealed class LambdaRestore(Action<RestoreRecord> restore) : IRestoreHandler
    {
        public void Restore(RestoreRecord record) => restore(record);
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+P", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x50u, "Ctrl+Alt+P")]
    [InlineData("alt + shift + 5", HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x35u, "Alt+Shift+5")]
    [InlineData("Win+F9", HotkeyModifiers.Win, 0x78u, "Win+F9")]
    public void Parses_valid_hotkeys(string text, HotkeyModifiers mods, uint vk, string normalized)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(mods, hk!.Modifiers);
        Assert.Equal(vk, hk.VirtualKey);
        Assert.Equal(normalized, hk.Text);
    }

    [Theory]
    [InlineData("P")]             // no modifier
    [InlineData("Shift+P")]       // just a capital letter
    [InlineData("Ctrl+Ctrl+P")]   // duplicate modifier
    [InlineData("Ctrl+Hyper+P")]  // unknown modifier
    [InlineData("Ctrl+F25")]      // no such key
    [InlineData("Ctrl+Enter")]    // unsupported key
    [InlineData("")]
    public void Rejects_invalid_hotkeys(string text) => Assert.False(Hotkey.TryParse(text, out _));
}

public class ProcessDifferTests
{
    [Fact]
    public void First_poll_primes_without_reporting_existing_processes()
    {
        var differ = new ProcessDiffer();
        var (started, exited) = differ.Update(new[] { 1, 2 }, pid => $"p{pid}.exe");
        Assert.Empty(started);
        Assert.Empty(exited);
        Assert.Equal(2, differ.Known.Count);
    }

    [Fact]
    public void Reports_started_and_exited_and_only_looks_up_new_pids()
    {
        var differ = new ProcessDiffer();
        differ.Update(new[] { 1, 2 }, pid => $"p{pid}.exe");

        var lookups = new List<int>();
        var (started, exited) = differ.Update(new[] { 2, 3 }, pid => { lookups.Add(pid); return $"p{pid}.exe"; });

        Assert.Equal(new[] { (3, "p3.exe") }, started);
        Assert.Equal(new[] { (1, "p1.exe") }, exited);
        Assert.Equal(new[] { 3 }, lookups);
    }

    [Fact]
    public void Process_that_exits_before_its_name_is_read_is_skipped()
    {
        var differ = new ProcessDiffer();
        differ.Update(new[] { 1 }, _ => "a.exe");
        var (started, _) = differ.Update(new[] { 1, 9 }, pid => pid == 9 ? null : "a.exe");
        Assert.Empty(started);
    }
}
