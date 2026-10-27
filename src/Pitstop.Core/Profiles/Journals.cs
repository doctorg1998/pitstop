using System.Text.Json;
using Pitstop.Core.Storage;

namespace Pitstop.Core.Profiles;

/// <summary>
/// Records, on disk, what an active profile changed. If Pitstop crashes or the PC loses power mid-profile,
/// the next start restores those settings, so a crash never leaves the PC stuck in "Play" mode.
/// </summary>
public sealed class RevertJournal
{
    private readonly IFileStore _files;
    private readonly string _path;

    public RevertJournal(IFileStore files, string path)
    {
        _files = files;
        _path = path;
    }

    public void Save(IEnumerable<RestoreRecord> records) =>
        _files.WriteAllTextAtomic(_path, JsonSerializer.Serialize(records.ToList()));

    public void Clear() => Save(Array.Empty<RestoreRecord>());

    public IReadOnlyList<RestoreRecord> Load()
    {
        if (!_files.Exists(_path)) return Array.Empty<RestoreRecord>();
        try { return JsonSerializer.Deserialize<List<RestoreRecord>>(_files.ReadAllText(_path)) ?? new(); }
        catch (JsonException) { return Array.Empty<RestoreRecord>(); }
    }

    /// <summary>Restores anything left over from an unclean exit, newest change first. Returns warnings.</summary>
    public IReadOnlyList<string> RecoverPending(IRestoreHandler handler)
    {
        var warnings = new List<string>();
        foreach (var record in Load().Reverse())
        {
            try { handler.Restore(record); }
            catch (Exception ex) { warnings.Add($"Couldn't restore {record.Kind}: {ex.Message}"); }
        }
        Clear();
        return warnings;
    }
}

public sealed record SessionRecord(string ProfileId, string ProfileName, DateTimeOffset Start, DateTimeOffset End, string Trigger)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Append-only log of profile sessions (one JSON object per line), read by the weekly digest.</summary>
public sealed class SessionLog
{
    private readonly IFileStore _files;
    private readonly string _path;

    public SessionLog(IFileStore files, string path)
    {
        _files = files;
        _path = path;
    }

    public void Append(SessionRecord record) => _files.AppendLine(_path, JsonSerializer.Serialize(record));

    /// <summary>Reads all sessions, skipping any line that's damaged (e.g. cut off by a power loss).</summary>
    public IReadOnlyList<SessionRecord> ReadAll()
    {
        if (!_files.Exists(_path)) return Array.Empty<SessionRecord>();
        var list = new List<SessionRecord>();
        foreach (var line in _files.ReadAllText(_path).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { if (JsonSerializer.Deserialize<SessionRecord>(line) is { } r) list.Add(r); }
            catch (JsonException) { }
        }
        return list;
    }
}
