using Pitstop.Core.Config;

namespace Pitstop.Core.Profiles;

public interface IProfileActionRunner
{
    /// <summary>Applies every action it can. A failing action becomes a warning; the rest still run.</summary>
    IReadOnlyList<IRevertToken> Apply(ProfileActions actions, ICollection<string> warnings);
}

public sealed class ActionRunner : IProfileActionRunner
{
    private readonly IPowerPlans _power;
    private readonly IAudioDevices _audio;
    private readonly INotificationToggle _notifications;
    private readonly IAppControl _apps;

    public ActionRunner(IPowerPlans power, IAudioDevices audio, INotificationToggle notifications, IAppControl apps)
    {
        _power = power;
        _audio = audio;
        _notifications = notifications;
        _apps = apps;
    }

    public IReadOnlyList<IRevertToken> Apply(ProfileActions a, ICollection<string> warnings)
    {
        var tokens = new List<IRevertToken>();

        void Try(string what, Func<IRevertToken> action)
        {
            try { tokens.Add(action()); }
            catch (Exception ex) { warnings.Add($"{what}: {ex.Message}"); }
        }

        // Close first so a closed app can't grab the audio device or fight the power plan.
        foreach (var exe in a.CloseApps)
        {
            if (ProtectedProcesses.IsProtected(exe)) { warnings.Add($"Close {exe}: refused, it's a protected Windows process."); continue; }
            Try($"Close {exe}", () => _apps.RequestClose(exe));
        }
        if (!string.IsNullOrWhiteSpace(a.PowerPlan)) Try($"Power plan '{a.PowerPlan}'", () => _power.Activate(a.PowerPlan));
        if (!string.IsNullOrWhiteSpace(a.AudioOutput)) Try($"Audio output '{a.AudioOutput}'", () => _audio.SetDefaultOutput(a.AudioOutput));
        if (!string.IsNullOrWhiteSpace(a.AudioInput)) Try($"Audio input '{a.AudioInput}'", () => _audio.SetDefaultInput(a.AudioInput));
        if (a.MuteNotifications) Try("Mute notifications", _notifications.Mute);
        foreach (var path in a.LaunchApps) Try($"Launch {Path.GetFileName(path)}", () => _apps.Launch(path));

        return tokens;
    }
}
