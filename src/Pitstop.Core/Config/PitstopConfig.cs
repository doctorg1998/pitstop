using System.Text.Json;
using Pitstop.Core.Profiles;
using Pitstop.Core.Storage;

namespace Pitstop.Core.Config;

public sealed class PitstopConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public PrivacySettings Privacy { get; set; } = new();
    public List<ProfileDefinition> Profiles { get; set; } = new();
}

public sealed class PrivacySettings
{
    public bool IncludeMachineNameInReports { get; set; }
}

public sealed class ProfileDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public ProfileTriggers Triggers { get; set; } = new();
    public ProfileActions Actions { get; set; } = new();
}

public sealed class ProfileTriggers
{
    public List<string> Processes { get; set; } = new();
    public string? Hotkey { get; set; }
}

public sealed class ProfileActions
{
    public string? PowerPlan { get; set; }
    public string? AudioOutput { get; set; }
    public string? AudioInput { get; set; }
    public bool MuteNotifications { get; set; }
    public List<string> CloseApps { get; set; } = new();
    public List<string> LaunchApps { get; set; } = new();
    public int? BreakReminderMinutes { get; set; }
}

/// <summary>Result of loading config: the config to use, plus any warnings to show in the tray.</summary>
public sealed record ConfigLoadResult(PitstopConfig Config, IReadOnlyList<string> Warnings, bool UsedDefaults);

public sealed class ConfigLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly IFileStore _files;

    public ConfigLoader(IFileStore files) => _files = files;

    public ConfigLoadResult Load(string path)
    {
        if (!_files.Exists(path))
            return new ConfigLoadResult(new PitstopConfig(), Array.Empty<string>(), UsedDefaults: true);

        PitstopConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<PitstopConfig>(_files.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            return FallBack(path, $"Config file is not valid JSON ({ex.Message}).");
        }

        if (config is null)
            return FallBack(path, "Config file is empty.");

        var errors = Validate(config);
        return errors.Count == 0
            ? new ConfigLoadResult(config, Array.Empty<string>(), UsedDefaults: false)
            : FallBack(path, string.Join(" ", errors));
    }

    public void Save(string path, PitstopConfig config) =>
        _files.WriteAllTextAtomic(path, JsonSerializer.Serialize(config, Json));

    public static List<string> Validate(PitstopConfig config)
    {
        var errors = new List<string>();
        if (config.SchemaVersion != PitstopConfig.CurrentSchemaVersion)
            errors.Add($"Unsupported schemaVersion {config.SchemaVersion}.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hotkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Id))
                errors.Add("A profile is missing its id.");
            else if (!ids.Add(p.Id))
                errors.Add($"Duplicate profile id '{p.Id}'.");

            if (p.Actions.BreakReminderMinutes is < 1)
                errors.Add($"Profile '{p.Id}': breakReminderMinutes must be at least 1.");

            foreach (var app in p.Actions.CloseApps.Where(ProtectedProcesses.IsProtected))
                errors.Add($"Profile '{p.Id}': '{app}' is a protected Windows process and can't be closed.");

            if (p.Triggers.Hotkey is { Length: > 0 } hk)
            {
                if (!Hotkey.TryParse(hk, out var parsed))
                    errors.Add($"Profile '{p.Id}': hotkey '{hk}' isn't valid. Use a modifier plus a key, like \"Ctrl+Alt+P\".");
                // Compare the key combination, not the text: "Alt+Ctrl+P" and "ctrl+alt+p" are the same hotkey.
                else if (!hotkeys.Add($"{(uint)parsed!.Modifiers}:{parsed.VirtualKey}"))
                    errors.Add($"Profile '{p.Id}': hotkey '{parsed.Text}' is already used by another profile.");
            }
        }
        return errors;
    }

    /// <summary>The starter config written on first run: three example profiles the user can edit.</summary>
    public static PitstopConfig CreateStarter() => new()
    {
        Profiles =
        {
            new ProfileDefinition
            {
                Id = "work", Name = "Work", Priority = 10,
                Triggers = { Processes = { "ms-teams.exe", "Teams.exe", "EXCEL.EXE", "WINWORD.EXE" }, Hotkey = "Ctrl+Alt+W" },
                Actions = { PowerPlan = "Balanced", BreakReminderMinutes = 60 },
            },
            new ProfileDefinition
            {
                Id = "play", Name = "Play", Priority = 20,
                Triggers = { Hotkey = "Ctrl+Alt+P" },
                Actions = { PowerPlan = "High performance", MuteNotifications = true, BreakReminderMinutes = 90 },
            },
            new ProfileDefinition
            {
                Id = "stream", Name = "Stream / Record", Priority = 30,
                Triggers = { Processes = { "obs64.exe" }, Hotkey = "Ctrl+Alt+S" },
                Actions = { PowerPlan = "High performance", MuteNotifications = true },
            },
        },
    };

    private ConfigLoadResult FallBack(string path, string reason)
    {
        var backup = path + ".bad";
        _files.Copy(path, backup);
        return new ConfigLoadResult(
            new PitstopConfig(),
            // The caller says what happens next (defaults at startup, previous profiles on a live reload; BUG-009).
            new[] { $"{reason} A copy of the file was saved as {Path.GetFileName(backup)}." },
            UsedDefaults: true);
    }
}
