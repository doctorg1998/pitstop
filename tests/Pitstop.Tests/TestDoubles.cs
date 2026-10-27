using Pitstop.Core.Profiles;
using Pitstop.Core.Storage;

namespace Pitstop.Tests;

internal sealed class FakeFileStore : IFileStore
{
    public Dictionary<string, string> Files { get; } = new();
    public bool Exists(string path) => Files.ContainsKey(path);
    public string ReadAllText(string path) => Files[path];
    public void WriteAllTextAtomic(string path, string contents) => Files[path] = contents;
    public void Copy(string from, string to) => Files[to] = Files[from];
    public void AppendLine(string path, string line) =>
        Files[path] = (Files.TryGetValue(path, out var existing) ? existing : "") + line + "\n";
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Records every action and revert, so tests can assert on order.</summary>
internal sealed class RecordingToken(string description, List<string> log, RestoreRecord? record = null) : IRevertToken
{
    private bool _reverted;
    public string Description { get; } = description;
    public RestoreRecord? Record { get; } = record;

    public void Revert()
    {
        if (_reverted) return;
        _reverted = true;
        log.Add("undo " + Description);
    }
}

internal sealed class FakeSystem : IPowerPlans, IAudioDevices, INotificationToggle, IAppControl
{
    public List<string> Log { get; } = new();
    public string? FailOn { get; set; }

    private IRevertToken Do(string what, RestoreRecord? record = null)
    {
        if (FailOn is not null && what.Contains(FailOn)) throw new InvalidOperationException($"{what} failed");
        Log.Add(what);
        return new RecordingToken(what, Log, record);
    }

    public IRevertToken Activate(string planName) => Do($"power {planName}", new(RestoreKinds.PowerPlan, "previous-plan-guid"));
    public IRevertToken SetDefaultOutput(string name) => Do($"output {name}", new(RestoreKinds.AudioDefault, "previous-output"));
    public IRevertToken SetDefaultInput(string name) => Do($"input {name}", new(RestoreKinds.AudioDefault, "previous-input"));
    public IRevertToken Mute() => Do("mute", new(RestoreKinds.Toasts, "1"));
    public IRevertToken RequestClose(string exe) => Do($"close {exe}");
    public IRevertToken Launch(string path) => Do($"launch {path}");
}
