namespace Pitstop.Core.Profiles;

public sealed record ProcessEvent(string ExeName, int ProcessId, DateTimeOffset At);

public interface IProcessWatcher : IDisposable
{
    event EventHandler<ProcessEvent>? ProcessStarted;
    event EventHandler<ProcessEvent>? ProcessExited;
    void Start();
}

/// <summary>
/// What a change replaced, saved to disk so it can be undone even if Pitstop crashes or the PC loses power
/// while a profile is active. <see cref="Kind"/> is one of <see cref="RestoreKinds"/>.
/// </summary>
public sealed record RestoreRecord(string Kind, string Value);

public static class RestoreKinds
{
    public const string PowerPlan = "powerPlan", AudioDefault = "audioDefault", Toasts = "toasts";
}

/// <summary>Undoes one applied profile action. Revert must be safe to call more than once.</summary>
public interface IRevertToken
{
    string Description { get; }

    /// <summary>The state to restore after a crash, or null if nothing needs restoring.</summary>
    RestoreRecord? Record { get; }

    void Revert();
}

/// <summary>Restores a <see cref="RestoreRecord"/> left over from a session that didn't shut down cleanly.</summary>
public interface IRestoreHandler
{
    void Restore(RestoreRecord record);
}

public interface IPowerPlans
{
    IRevertToken Activate(string planName);
}

public interface IAudioDevices
{
    IRevertToken SetDefaultOutput(string deviceNameContains);
    IRevertToken SetDefaultInput(string deviceNameContains);
}

public interface INotificationToggle
{
    IRevertToken Mute();
}

public interface IAppControl
{
    /// <summary>Asks the app to close (no force-kill). Protected processes are refused.</summary>
    IRevertToken RequestClose(string exeName);
    IRevertToken Launch(string path);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>A token for an action that changed nothing (for example, the setting was already as requested).</summary>
public sealed class NoOpToken(string description) : IRevertToken
{
    public string Description { get; } = description;
    public RestoreRecord? Record => null;
    public void Revert() { }
}
