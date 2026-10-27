namespace Pitstop.Core.Profiles;

/// <summary>Processes a profile may never close, whatever the config says.</summary>
public static class ProtectedProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "csrss", "wininit", "winlogon", "services", "lsass", "smss",
        "explorer", "dwm", "svchost", "audiodg", "MsMpEng", "Pitstop",
    };

    public static bool IsProtected(string exeName)
    {
        var name = exeName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return Names.Contains(name);
    }
}
