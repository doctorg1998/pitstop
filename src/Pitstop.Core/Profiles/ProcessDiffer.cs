namespace Pitstop.Core.Profiles;

/// <summary>
/// Turns successive lists of process IDs into start/exit events. Names are looked up only for new PIDs,
/// which keeps each poll cheap: a typical PC has ~300 processes, but only a few start between polls.
/// </summary>
public sealed class ProcessDiffer
{
    private Dictionary<int, string> _known = new();
    private bool _primed;

    /// <param name="currentPids">All process IDs running now.</param>
    /// <param name="nameOf">Returns the exe name for a PID, or null if it already exited or can't be read.</param>
    /// <returns>Started and exited processes since the previous call. The first call only primes the state.</returns>
    public (IReadOnlyList<(int Pid, string Exe)> Started, IReadOnlyList<(int Pid, string Exe)> Exited) Update(
        IReadOnlyCollection<int> currentPids, Func<int, string?> nameOf)
    {
        var next = new Dictionary<int, string>(currentPids.Count);
        var started = new List<(int, string)>();
        foreach (var pid in currentPids)
        {
            if (_known.TryGetValue(pid, out var name)) { next[pid] = name; continue; }
            var exe = nameOf(pid);
            if (exe is null) continue;
            next[pid] = exe;
            if (_primed) started.Add((pid, exe));
        }

        var exited = _known.Where(k => !next.ContainsKey(k.Key)).Select(k => (k.Key, k.Value)).ToList();
        _known = next;
        if (!_primed) { _primed = true; exited.Clear(); }
        return (started, exited);
    }

    /// <summary>The processes known after the last call (used to seed the engine with apps already running).</summary>
    public IReadOnlyDictionary<int, string> Known => _known;
}
