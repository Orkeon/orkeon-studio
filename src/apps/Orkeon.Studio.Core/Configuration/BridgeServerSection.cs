using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The serving side of the tool bridge (<c>Orkeon:Bridge</c>): the tools <c>orkeon bridge serve</c> runs
/// on this machine for a caller on another. Nothing is served while <see cref="Allow"/> is empty. Every
/// host validates the section at start, the serving command or not: a value it refuses stops every
/// run. The engine's options class is internal: the section is bound by the configuration binder onto
/// a class of the same shape (<see cref="Values"/>) and the rules are restated
/// (<see cref="ToolBridgeRules"/>), both held to the engine by <c>ToolBridgeRulesOracleTests</c> and
/// <c>ToolBridgeSectionTests</c>. What only the serving command refuses (its own token, an https prefix
/// off Windows, a name that is no bridgeable tool) is not judged here.
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
    public bool Exists => _document.SectionExists(_document.PathAsWritten(SectionPath));

    /// <summary>The listener's prefix; null lets <see cref="DefaultPrefix"/> stand.</summary>
    public string? Prefix
    {
        get => _document.GetString(Key("Prefix"));
        set => _document.SetString(Key("Prefix"), Blank(value));
    }

    /// <summary>The environment variable that holds the token; null lets the default stand.</summary>
    public string? TokenEnvVar
    {
        get => _document.GetString(Key("TokenEnvVar"));
        set => _document.SetString(Key("TokenEnvVar"), Blank(value));
    }

    /// <summary>The tools served, by name, or the lone <c>*</c> for every bridgeable Orkeon tool.</summary>
    public IReadOnlyList<string> Allow
    {
        get => _document.GetStringArray(Key("Allow"));
        set => SetList("Allow", value);
    }

    /// <summary>The tools never served, by name; a name here wins over <see cref="Allow"/>.</summary>
    public IReadOnlyList<string> Deny
    {
        get => _document.GetStringArray(Key("Deny"));
        set => SetList("Deny", value);
    }

    /// <summary>
    /// What the host's start refuses in the section, each with the path of its key; empty when the
    /// section is accepted. A value the binder cannot convert stops the start before any rule is read:
    /// it is then all that is said, the first one only, as the host says it. Then the engine's rules, in
    /// the engine's words and order, and the keys that are no setting of the section.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var configuration = DecisionSection.ToConfiguration(_document.Root);
        var problems = new List<(string, string)>();
        var values = new Values();
        if (!SettingsBinding.TryBind(configuration, SectionPath, values, problems))
            return problems;

        void Say(string key, string problem) => problems.Add(($"{SectionPath}:{key}", problem));
        foreach (var (key, value) in new[]
        {
            (nameof(Values.MaxSessions), values.MaxSessions), (nameof(Values.MaxConcurrentCalls), values.MaxConcurrentCalls),
            (nameof(Values.MaxCallsPerSession), values.MaxCallsPerSession), (nameof(Values.MaxRunsPerSession), values.MaxRunsPerSession),
        })
        {
            if (value < 1 || value > ToolBridgeRules.MaxCountLimit)
                Say(key, $"{SectionPath}:{key} must lie between 1 and {ToolBridgeRules.MaxCountLimit}.");
        }

        if (values.RequestReadTimeout < TimeSpan.FromSeconds(1) || values.RequestReadTimeout > ToolBridgeRules.MaxTimeout)
            Say("RequestReadTimeout", $"{SectionPath}:RequestReadTimeout must lie between 00:00:01 and {ToolBridgeRules.MaxTimeout}.");
        if (values.StopTimeout < TimeSpan.Zero || values.StopTimeout > ToolBridgeRules.MaxTimeout)
            Say("StopTimeout", $"{SectionPath}:StopTimeout must lie between 00:00:00 and {ToolBridgeRules.MaxTimeout}.");
        if (ToolBridgeRules.AddressProblem(values.Prefix, $"{SectionPath}:Prefix") is { } prefix)
            Say("Prefix", prefix);
        if (string.IsNullOrWhiteSpace(values.TokenEnvVar))
            Say("TokenEnvVar", $"{SectionPath}:TokenEnvVar names no environment variable.");
        if (values.MaxRequestBytes <= 0)
            Say("MaxRequestBytes", $"{SectionPath}:MaxRequestBytes must be positive.");
        if (values.MaxFileBytes <= 0)
            Say("MaxFileBytes", $"{SectionPath}:MaxFileBytes must be positive.");
        if (values.HeartbeatWindow < TimeSpan.FromSeconds(1) || values.HeartbeatWindow > ToolBridgeRules.MaxTimeout)
            Say("HeartbeatWindow", $"{SectionPath}:HeartbeatWindow must lie between {TimeSpan.FromSeconds(1)} and {ToolBridgeRules.MaxTimeout}.");
        problems.AddRange(ToolBridgeRules.SelectionProblems(SectionPath, values.Allow, values.Deny)
            .Select(problem => (SettingsBinding.PathOf(problem, SectionPath), problem)));
        problems.AddRange(SettingsBinding.UnknownKeys(configuration, SectionPath, typeof(Values)));
        return problems;
    }

    /// <summary>Removes the whole section.</summary>
    public void Remove() => _document.Remove(_document.PathAsWritten(SectionPath));

    private string Key(string key) => _document.PathAsWritten($"{SectionPath}:{key}");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetList(string key, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            _document.Remove(Key(key));
        else
            _document.SetStringArray(Key(key), values);
    }

    /// <summary>
    /// The section as the engine's <c>ToolBridgeServerOptions</c> shapes it: the same property names,
    /// types, defaults and order, so the binder reads the file here as it does there and stops on the
    /// same first value. Its public properties are the section's settings, and nothing else may be
    /// public on it.
    /// </summary>
    internal sealed class Values
    {
        public string Prefix { get; set; } = DefaultPrefix;

        public string TokenEnvVar { get; set; } = ToolBridgeSection.DefaultTokenEnvVar;

        public Collection<string> Allow { get; } = [];

        public Collection<string> Deny { get; } = [];

        public long MaxRequestBytes { get; set; } = DefaultMaxRequestBytes;

        public long MaxFileBytes { get; set; } = ToolBridgeSection.DefaultMaxFileBytes;

        public TimeSpan HeartbeatWindow { get; set; } = TimeSpan.Parse(DefaultHeartbeatWindow, CultureInfo.InvariantCulture);

        public int MaxSessions { get; set; } = DefaultMaxSessions;

        public int MaxConcurrentCalls { get; set; } = DefaultMaxConcurrentCalls;

        public int MaxCallsPerSession { get; set; } = DefaultMaxCallsPerSession;

        public int MaxRunsPerSession { get; set; } = DefaultMaxRunsPerSession;

        public TimeSpan RequestReadTimeout { get; set; } = TimeSpan.Parse(DefaultRequestReadTimeout, CultureInfo.InvariantCulture);

        public TimeSpan StopTimeout { get; set; } = TimeSpan.Parse(DefaultStopTimeout, CultureInfo.InvariantCulture);
    }
}
