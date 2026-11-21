using System.Globalization;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The serving side of the tool bridge (<c>Orkeon:Bridge</c>): the tools <c>orkeon bridge serve</c> runs
/// on this machine for a caller on another. Nothing is served while <see cref="Allow"/> is empty. Every
/// host validates the section at start, the serving command or not: a value it refuses stops every
/// run. The engine's options class is internal, so the defaults are copied (pinned against the engine's
/// settings catalogue by <c>ToolBridgeDefaultsDriftTests</c>) and the rules restated
/// (<see cref="ToolBridgeRules"/>).
/// </summary>
public sealed class BridgeServerSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = "Orkeon:Bridge";

    /// <summary>Default of <c>Prefix</c> under Windows, whose URL reservations name <c>localhost</c>.</summary>
    public const string DefaultPrefixWindows = "http://localhost:7410/";

    /// <summary>Default of <c>Prefix</c> everywhere else.</summary>
    public const string DefaultPrefixElsewhere = "http://127.0.0.1:7410/";

    /// <summary>Default of <c>MaxRequestBytes</c>: 32 MiB.</summary>
    public const long DefaultMaxRequestBytes = 32L * 1024 * 1024;

    /// <summary>Default of <c>HeartbeatWindow</c>.</summary>
    public const string DefaultHeartbeatWindow = "00:00:30";

    /// <summary>Default of <c>RequestReadTimeout</c>.</summary>
    public const string DefaultRequestReadTimeout = "00:00:30";

    /// <summary>Default of <c>StopTimeout</c>.</summary>
    public const string DefaultStopTimeout = "00:00:10";

    /// <summary>Default of <c>MaxSessions</c>.</summary>
    public const int DefaultMaxSessions = 16;

    /// <summary>Default of <c>MaxConcurrentCalls</c>.</summary>
    public const int DefaultMaxConcurrentCalls = 32;

    /// <summary>Default of <c>MaxCallsPerSession</c>.</summary>
    public const int DefaultMaxCallsPerSession = 8;

    /// <summary>Default of <c>MaxRunsPerSession</c>.</summary>
    public const int DefaultMaxRunsPerSession = 16;

    /// <summary>The prefix the engine listens on when the section names none, on this machine.</summary>
    public static string DefaultPrefix => OperatingSystem.IsWindows() ? DefaultPrefixWindows : DefaultPrefixElsewhere;

    private readonly AppSettingsDocument _document;

    internal BridgeServerSection(AppSettingsDocument document) => _document = document;

    /// <summary>True when the section carries at least one key.</summary>
    public bool Exists => _document.SectionExists(SectionPath);

    /// <summary>The listener's prefix; null lets <see cref="DefaultPrefix"/> stand.</summary>
    public string? Prefix
    {
        get => _document.GetString($"{SectionPath}:Prefix");
        set => _document.SetString($"{SectionPath}:Prefix", Blank(value));
    }

    /// <summary>The environment variable that holds the token; null lets the default stand.</summary>
    public string? TokenEnvVar
    {
        get => _document.GetString($"{SectionPath}:TokenEnvVar");
        set => _document.SetString($"{SectionPath}:TokenEnvVar", Blank(value));
    }

    /// <summary>The variable the serving command reads the token from: the one the section names, else the default.</summary>
    public string EffectiveTokenEnvVar =>
        TokenEnvVar is { } named && !string.IsNullOrWhiteSpace(named) ? named : ToolBridgeSection.DefaultTokenEnvVar;

    /// <summary>The tools served, by name, or the lone <c>*</c> for every bridgeable Orkeon tool.</summary>
    public IReadOnlyList<string> Allow
    {
        get => _document.GetStringArray($"{SectionPath}:Allow");
        set => SetList("Allow", value);
    }

    /// <summary>The tools never served, by name; a name here wins over <see cref="Allow"/>.</summary>
    public IReadOnlyList<string> Deny
    {
        get => _document.GetStringArray($"{SectionPath}:Deny");
        set => SetList("Deny", value);
    }

    /// <summary>A key of the section as the file writes it, for the fields Studio shows as text; empty when absent.</summary>
    public string Written(string key) => _document.GetWritten($"{SectionPath}:{key}");

    /// <summary>Whether <c>orkeon bridge serve</c> serves anything with this file: a tool allowed.</summary>
    public bool IsServing => Allow.Count > 0;

    /// <summary>
    /// What the host's start refuses in the section, in the engine's words and order, each with the
    /// path of its key; empty when the section is accepted. A value the binder cannot convert stops the
    /// start before any rule is read: it is then all that is said.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var problems = new List<(string, string)>();
        var counts = new[] { "MaxSessions", "MaxConcurrentCalls", "MaxCallsPerSession", "MaxRunsPerSession" };
        var defaults = new[] { DefaultMaxSessions, DefaultMaxConcurrentCalls, DefaultMaxCallsPerSession, DefaultMaxRunsPerSession };
        var values = new long[counts.Length];
        for (var i = 0; i < counts.Length; i++)
            values[i] = ToolBridgeValues.Int32(_document, SectionPath, counts[i], defaults[i], problems);
        var request = ToolBridgeValues.Int64(_document, SectionPath, "MaxRequestBytes", DefaultMaxRequestBytes, problems);
        var files = ToolBridgeValues.Int64(_document, SectionPath, "MaxFileBytes", ToolBridgeSection.DefaultMaxFileBytes, problems);
        var heartbeat = ToolBridgeValues.Duration(_document, SectionPath, "HeartbeatWindow", DefaultHeartbeatWindow, problems);
        var read = ToolBridgeValues.Duration(_document, SectionPath, "RequestReadTimeout", DefaultRequestReadTimeout, problems);
        var stop = ToolBridgeValues.Duration(_document, SectionPath, "StopTimeout", DefaultStopTimeout, problems);
        if (problems.Count > 0)
            return problems;

        void Say(string key, string problem) => problems.Add(($"{SectionPath}:{key}", problem));
        for (var i = 0; i < counts.Length; i++)
        {
            if (values[i] < 1 || values[i] > ToolBridgeRules.MaxCountLimit)
                Say(counts[i], $"{SectionPath}:{counts[i]} must lie between 1 and {ToolBridgeRules.MaxCountLimit}.");
        }

        if (read < TimeSpan.FromSeconds(1) || read > ToolBridgeRules.MaxTimeout)
            Say("RequestReadTimeout", $"{SectionPath}:RequestReadTimeout must lie between 00:00:01 and {ToolBridgeRules.MaxTimeout}.");
        if (stop < TimeSpan.Zero || stop > ToolBridgeRules.MaxTimeout)
            Say("StopTimeout", $"{SectionPath}:StopTimeout must lie between 00:00:00 and {ToolBridgeRules.MaxTimeout}.");
        // An absent key leaves the engine's default; a key written blank is read as an empty address.
        var prefix = _document.ContainsPath($"{SectionPath}:Prefix") ? Written("Prefix") : DefaultPrefix;
        if (ToolBridgeRules.AddressProblem(prefix, $"{SectionPath}:Prefix") is { } prefixProblem)
            Say("Prefix", prefixProblem);
        if (ToolBridgeValues.IsBlankWritten(_document, SectionPath, "TokenEnvVar"))
            Say("TokenEnvVar", $"{SectionPath}:TokenEnvVar names no environment variable.");
        if (request <= 0)
            Say("MaxRequestBytes", $"{SectionPath}:MaxRequestBytes must be positive.");
        if (files <= 0)
            Say("MaxFileBytes", $"{SectionPath}:MaxFileBytes must be positive.");
        if (heartbeat < TimeSpan.FromSeconds(1) || heartbeat > ToolBridgeRules.MaxTimeout)
            Say("HeartbeatWindow", $"{SectionPath}:HeartbeatWindow must lie between {TimeSpan.FromSeconds(1)} and {ToolBridgeRules.MaxTimeout}.");
        problems.AddRange(ToolBridgeValues.Selection(_document, SectionPath));
        return problems;
    }

    /// <summary>Removes the whole section.</summary>
    public void Remove() => _document.Remove(SectionPath);

    /// <summary>Writes a key of the section from a field's text; blank removes it.</summary>
    public void Write(string key, string? text) => _document.SetString($"{SectionPath}:{key}", Blank(text));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetList(string key, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            _document.Remove($"{SectionPath}:{key}");
        else
            _document.SetStringArray($"{SectionPath}:{key}", values);
    }
}

/// <summary>Reads the values of a bridge section as the host's binder does, a default standing for an absent key.</summary>
internal static class ToolBridgeValues
{
    /// <summary>Whether the key is written with no value, or a blank one: the binder then reads an empty text.</summary>
    public static bool IsBlankWritten(AppSettingsDocument document, string section, string key) =>
        document.ContainsPath($"{section}:{key}") && string.IsNullOrWhiteSpace(document.GetWritten($"{section}:{key}"));

    public static long Int64(AppSettingsDocument document, string section, string key, long fallback, List<(string, string)> problems)
    {
        var path = $"{section}:{key}";
        var written = document.GetWritten(path);
        if (string.IsNullOrWhiteSpace(written))
            return fallback;
        if (long.TryParse(written.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        problems.Add((path, ToolBridgeRules.ConversionProblem(written, path, typeof(long))));
        return fallback;
    }

    public static long Int32(AppSettingsDocument document, string section, string key, int fallback, List<(string, string)> problems)
    {
        var path = $"{section}:{key}";
        var written = document.GetWritten(path);
        if (string.IsNullOrWhiteSpace(written))
            return fallback;
        if (int.TryParse(written.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        problems.Add((path, ToolBridgeRules.ConversionProblem(written, path, typeof(int))));
        return fallback;
    }

    public static TimeSpan Duration(AppSettingsDocument document, string section, string key, string fallback, List<(string, string)> problems)
    {
        var path = $"{section}:{key}";
        var written = document.GetWritten(path);
        if (string.IsNullOrWhiteSpace(written))
            return TimeSpan.Parse(fallback, CultureInfo.InvariantCulture);
        if (ToolBridgeRules.TryReadDuration(written.Trim(), out var value))
            return value;
        problems.Add((path, ToolBridgeRules.ConversionProblem(written, path, typeof(TimeSpan))));
        return TimeSpan.Parse(fallback, CultureInfo.InvariantCulture);
    }

    /// <summary>The problems of the section's <c>Allow</c> and <c>Deny</c>, each at its list's path.</summary>
    public static IEnumerable<(string, string)> Selection(AppSettingsDocument document, string section)
    {
        foreach (var key in new[] { "Allow", "Deny" })
        {
            var entries = document.GetStringArray($"{section}:{key}");
            var (allow, deny) = key == "Allow" ? (entries, (IReadOnlyList<string>)[]) : ([], entries);
            foreach (var problem in ToolBridgeRules.SelectionProblems(section, allow, deny))
                yield return ($"{section}:{key}", problem);
        }
    }
}
