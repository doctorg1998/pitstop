namespace Pitstop.Core.Storage;

public interface IFileStore
{
    bool Exists(string path);
    string ReadAllText(string path);

    /// <summary>Writes via a temp file and rename, so a crash never leaves a half-written file.</summary>
    void WriteAllTextAtomic(string path, string contents);

    void Copy(string from, string to);

    void AppendLine(string path, string line);
}

public sealed class DiskFileStore : IFileStore
{
    public bool Exists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllTextAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    public void Copy(string from, string to) => File.Copy(from, to, overwrite: true);

    public void AppendLine(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.AppendAllText(path, line + Environment.NewLine);
    }
}
