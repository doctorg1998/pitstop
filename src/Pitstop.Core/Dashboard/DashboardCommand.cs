using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pitstop.Core.Dashboard;

/// <summary>A validated request from the dashboard page to the app.</summary>
public sealed record DashboardCommand(string Type, string? ProfileId = null, bool? On = null, string? Url = null);

/// <summary>
/// The dashboard page is untrusted input: anything it sends is parsed here against a strict allowlist
/// (known command types, size cap, id format, link allowlist) before the app acts on it.
/// </summary>
public static partial class DashboardCommandParser
{
    public const int MaxMessageLength = 4096;

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        "ready", "runCheck", "toggleProfile", "setAutostart", "setIncludeMachineName",
        "openReport", "openConfig", "openFolder", "openUrl",
    };

    /// <summary>The only external links the dashboard may open (in the user's browser).</summary>
    public static readonly IReadOnlyList<string> AllowedUrlPrefixes = new[]
    {
        "https://github.com/doctorg1998/pitstop",
        "https://doctorg1998.github.io",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileIdPattern();

    /// <summary>Returns null (and the reason) for anything that isn't a well-formed, allowed command.</summary>
    public static DashboardCommand? Parse(string? json, out string? rejection)
    {
        rejection = null;
        if (string.IsNullOrEmpty(json)) { rejection = "empty message"; return null; }
        if (json.Length > MaxMessageLength) { rejection = "message too large"; return null; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { rejection = "not an object"; return null; }
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) { rejection = "missing type"; return null; }

            var type = typeEl.GetString()!;
            if (!Types.Contains(type)) { rejection = $"unknown type '{Truncate(type)}'"; return null; }

            switch (type)
            {
                case "toggleProfile":
                    var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                    if (id is null || !ProfileIdPattern().IsMatch(id)) { rejection = "invalid profile id"; return null; }
                    return new DashboardCommand(type, ProfileId: id);

                case "setAutostart" or "setIncludeMachineName":
                    if (!root.TryGetProperty("on", out var onEl) || onEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    { rejection = "missing on/off value"; return null; }
                    return new DashboardCommand(type, On: onEl.GetBoolean());

                case "openUrl":
                    var url = root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
                    if (url is null || !IsAllowedUrl(url)) { rejection = "link not allowed"; return null; }
                    return new DashboardCommand(type, Url: url);

                default:
                    return new DashboardCommand(type);
            }
        }
        catch (JsonException)
        {
            rejection = "invalid JSON";
            return null;
        }
    }

    public static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && AllowedUrlPrefixes.Any(p => url.Equals(p, StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
