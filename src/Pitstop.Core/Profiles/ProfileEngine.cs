using Pitstop.Core.Config;

namespace Pitstop.Core.Profiles;

public sealed record ProfileChange(ProfileDefinition? Previous, ProfileDefinition? Current, string Trigger, IReadOnlyList<string> Warnings);

/// <summary>
/// Decides which profile should be active and switches to it.
/// Rules: a manual (hotkey or menu) choice wins until it's turned off. Otherwise the highest-priority profile
/// with a running trigger app wins, and a tie goes to the app that started most recently. When the trigger app
/// exits, the engine falls back to the next match, or to no profile, and every change is reverted.
/// Thread-safe: process events arrive on a background thread, hotkeys on the UI thread.
/// </summary>
public sealed class ProfileEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly IProfileActionRunner _runner;
    private readonly IClock _clock;
    private readonly RevertJournal _journal;
    private readonly SessionLog _sessions;

    private IReadOnlyList<ProfileDefinition> _profiles;
    private readonly Dictionary<int, (string Exe, DateTimeOffset At)> _running = new();
    private string? _manualId;

    private ProfileDefinition? _active;
    private IReadOnlyList<IRevertToken> _tokens = Array.Empty<IRevertToken>();
    private DateTimeOffset _activeSince, _lastBreakReminder;
    private string _activeTrigger = "";

    public ProfileEngine(IReadOnlyList<ProfileDefinition> profiles, IProfileActionRunner runner, IClock clock,
        RevertJournal journal, SessionLog sessions)
    {
        _profiles = profiles;
        _runner = runner;
        _clock = clock;
        _journal = journal;
        _sessions = sessions;
    }

    public event EventHandler<ProfileChange>? ProfileChanged;
    public event EventHandler<ProfileDefinition>? BreakReminderDue;

    public ProfileDefinition? Active { get { lock (_gate) return _active; } }
    public string? ManualProfileId { get { lock (_gate) return _manualId; } }

    public void OnProcessStarted(ProcessEvent e)
    {
        var exe = Normalize(e.ExeName);
        lock (_gate)
        {
            if (!_profiles.Any(p => Triggers(p, exe))) return; // ignore the hundreds of unrelated processes
            _running[e.ProcessId] = (exe, e.At);
        }
        Reevaluate($"{exe} started");
    }

    public void OnProcessExited(ProcessEvent e)
    {
        bool removed;
        lock (_gate) removed = _running.Remove(e.ProcessId);
        if (removed) Reevaluate($"{Normalize(e.ExeName)} exited");
    }

    /// <summary>Turns a profile on manually, or off again if it's already the manual choice.</summary>
    public void ToggleManual(string profileId)
    {
        lock (_gate) _manualId = string.Equals(_manualId, profileId, StringComparison.OrdinalIgnoreCase) ? null : profileId;
        Reevaluate("manual");
    }

    /// <summary>Applies a reloaded config. The active profile is re-applied if its definition changed.</summary>
    public void UpdateProfiles(IReadOnlyList<ProfileDefinition> profiles)
    {
        lock (_gate)
        {
            _profiles = profiles;
            if (_manualId is not null && !profiles.Any(p => p.Id.Equals(_manualId, StringComparison.OrdinalIgnoreCase)))
                _manualId = null;
            foreach (var pid in _running.Where(r => !profiles.Any(p => Triggers(p, r.Value.Exe))).Select(r => r.Key).ToList())
                _running.Remove(pid);
            Switch(Desired(), "config reloaded", forceReapply: true);
        }
    }

    /// <summary>Call periodically (e.g. every 30 s) to raise break reminders.</summary>
    public void Tick()
    {
        ProfileDefinition? due = null;
        lock (_gate)
        {
            if (_active?.Actions.BreakReminderMinutes is { } minutes && _clock.Now - _lastBreakReminder >= TimeSpan.FromMinutes(minutes))
            {
                _lastBreakReminder = _clock.Now;
                due = _active;
            }
        }
        if (due is not null) BreakReminderDue?.Invoke(this, due);
    }

    /// <summary>Reverts the active profile (used on exit).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _manualId = null;
            _running.Clear();
            Switch(null, "Pitstop closed");
        }
    }

    private void Reevaluate(string trigger)
    {
        lock (_gate) Switch(Desired(), trigger);
    }

    private ProfileDefinition? Desired()
    {
        if (_manualId is not null)
            return _profiles.FirstOrDefault(p => p.Id.Equals(_manualId, StringComparison.OrdinalIgnoreCase));

        return _profiles
            .Select(p => (Profile: p, Latest: _running.Values.Where(r => Triggers(p, r.Exe)).Select(r => (DateTimeOffset?)r.At).Max()))
            .Where(x => x.Latest is not null)
            .OrderByDescending(x => x.Profile.Priority)
            .ThenByDescending(x => x.Latest)
            .Select(x => x.Profile)
            .FirstOrDefault();
    }

    // Must be called with _gate held.
    private void Switch(ProfileDefinition? next, string trigger, bool forceReapply = false)
    {
        if (!forceReapply && ReferenceEquals(next, _active)) return;
        if (!forceReapply && next is not null && _active is not null && next.Id == _active.Id) return;

        var previous = _active;
        var warnings = new List<string>();
        var now = _clock.Now;

        // Undo the old profile, newest change first.
        foreach (var token in _tokens.Reverse())
        {
            try { token.Revert(); }
            catch (Exception ex) { warnings.Add($"Undo '{token.Description}': {ex.Message}"); }
        }
        if (previous is not null)
            _sessions.Append(new SessionRecord(previous.Id, previous.Name, _activeSince, now, _activeTrigger));

        _tokens = next is null ? Array.Empty<IRevertToken>() : _runner.Apply(next.Actions, warnings);
        _active = next;
        _activeSince = _lastBreakReminder = now;
        _activeTrigger = trigger;

        var records = _tokens.Select(t => t.Record).OfType<RestoreRecord>().ToList();
        if (records.Count > 0) _journal.Save(records); else _journal.Clear();

        if (!ReferenceEquals(previous, next) || warnings.Count > 0)
            ProfileChanged?.Invoke(this, new ProfileChange(previous, next, trigger, warnings));
    }

    private static bool Triggers(ProfileDefinition p, string exe) =>
        p.Triggers.Processes.Any(t => Normalize(t).Equals(exe, StringComparison.OrdinalIgnoreCase));

    internal static string Normalize(string exe)
    {
        var name = Path.GetFileName(exe.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }
}
