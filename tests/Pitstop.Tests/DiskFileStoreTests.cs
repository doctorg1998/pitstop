using Pitstop.Core.Health;
using Pitstop.Core.Storage;

namespace Pitstop.Tests;

public sealed class DiskFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pitstop-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DiskFileStore _store = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Atomic_write_creates_folders_replaces_content_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_dir, "nested", "config.json");

        _store.WriteAllTextAtomic(path, "first");
        _store.WriteAllTextAtomic(path, "second");

        Assert.Equal("second", _store.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Append_and_copy_work_on_disk()
    {
        var log = Path.Combine(_dir, "sessions.jsonl");
        _store.AppendLine(log, "a");
        _store.AppendLine(log, "b");
        _store.Copy(log, log + ".bak");

        Assert.True(_store.Exists(log + ".bak"));
        Assert.Equal(new[] { "a", "b" }, File.ReadAllLines(log + ".bak"));
    }

    [Fact]
    public void Report_writer_writes_matching_html_and_json()
    {
        var report = new HealthReport(new DateTimeOffset(2026, 10, 27, 9, 5, 3, TimeSpan.Zero), "<pc>", TimeSpan.FromSeconds(5),
            new[] { new Finding("H1", "Windows updates", Severity.Ok, "a", "b", "c") });

        var html = new ReportWriter(_store).Write(report, _dir);

        Assert.EndsWith("pitstop-health-20261027-090503.html", html);
        Assert.True(File.Exists(Path.ChangeExtension(html, ".json")));
    }
}
