using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The caller's side of the tool bridge (<c>Orkeon:Tools:Bridge</c>): the tools of a run that execute
/// on another machine, an Orkeon in a container whose browser runs on the desktop, say. Nothing is
/// bridged while <see cref="Address"/> is unset or <see cref="Allow"/> is empty. The token never goes
/// in the file: the section names the environment variable that holds it. The engine's options class
/// is internal: the section is bound by the configuration binder onto a class of the same shape
/// (<see cref="Values"/>), so every value is read as the engine reads it, in any letter case, and the
/// rules are restated (<see cref="ToolBridgeRules"/>); the defaults are pinned against the engine's
/// settings catalogue and both are held to the engine by <c>ToolBridgeRulesOracleTests</c> and
/// <c>ToolBridgeSectionTests</c>.
/// </summary>
public sealed class ToolBridgeSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = "Orkeon:Tools:Bridge";

    /// <summary>Default of <c>TokenEnvVar</c>, on both sides of the bridge.</summary>
    public const string DefaultTokenEnvVar = "ORKEON_BRIDGE_TOKEN";

    /// <summary>Default of <c>ConnectTimeout</c>.</summary>
    public const string DefaultConnectTimeout = "00:00:10";

    /// <summary>Default of <c>MaxFileBytes</c>, on both sides of the bridge: 16 MiB.</summary>
    public const long DefaultMaxFileBytes = 16L * 1024 * 1024;

    /// <summary>Default of <c>MaxResponseBytes</c>: 32 MiB.</summary>
    public const long DefaultMaxResponseBytes = 32L * 1024 * 1024;

    private readonly AppSettingsDocument _document;

    internal ToolBridgeSection(AppSettingsDocument document) => _document = document;

    /// <summary>True when the section carries at least one key.</summary>
    public bool Exists => _document.SectionExists(_document.PathAsWritten(SectionPath));

    /// <summary>The bridge's base address (<c>https://host:7410/</c>); null, nothing is bridged.</summary>
    public string? Address
    {
        get => _document.GetString(Key("Address"));
        set => _document.SetString(Key("Address"), Blank(value));
    }

    /// <summary>The environment variable that holds the token; null lets <see cref="DefaultTokenEnvVar"/> stand.</summary>
    public string? TokenEnvVar
    {
        get => _document.GetString(Key("TokenEnvVar"));
        set => _document.SetString(Key("TokenEnvVar"), Blank(value));
    }

    /// <summary>
    /// The variable a run reads the token from: the one the section names, else the default. The
    /// default too when the section does not bind or names none, which the start refuses anyway.
    /// </summary>
    public string EffectiveTokenEnvVar =>
        Bound() is { TokenEnvVar: { } named } && !string.IsNullOrWhiteSpace(named) ? named : DefaultTokenEnvVar;

    /// <summary>The tools to bridge, by name, or the lone <c>*</c> for every bridgeable tool the bridge serves.</summary>
    public IReadOnlyList<string> Allow
    {
        get => _document.GetStringArray(Key("Allow"));
        set => SetList("Allow", value);
    }

    /// <summary>The tools never bridged, by name; a name here wins over <see cref="Allow"/>.</summary>
    public IReadOnlyList<string> Deny
    {
        get => _document.GetStringArray(Key("Deny"));
        set => SetList("Deny", value);
    }

    /// <summary>The <c>Host</c> header sent to the bridge, when it differs from the address's.</summary>
    public string? HostHeader
    {
        get => _document.GetString(Key("HostHeader"));
        set => _document.SetString(Key("HostHeader"), Blank(value));
    }

    /// <summary>
    /// Whether a run of the file bridges anything, as the engine reads the section: an address, and a
    /// tool allowed. False when the section does not bind, the start being refused then.
    /// </summary>
    public bool IsEnabled => Bound() is { } values && !string.IsNullOrWhiteSpace(values.Address) && values.Allow.Count > 0;

    /// <summary>The address a run dials, as the engine reads it; null when none is set or the section does not bind.</summary>
    public string? BoundAddress => Bound()?.Address is { } address && !string.IsNullOrWhiteSpace(address) ? address : null;

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
        if (!string.IsNullOrWhiteSpace(values.Address)
            && ToolBridgeRules.AddressProblem(values.Address, $"{SectionPath}:Address", allowDockerHost: true) is { } address)
        {
            Say("Address", address);
        }

        if (!string.IsNullOrWhiteSpace(values.HostHeader) && !ToolBridgeRules.IsAuthority(values.HostHeader.Trim()))
            Say("HostHeader", $"{SectionPath}:HostHeader '{values.HostHeader}' is not a host with an optional port (127.0.0.1:7410).");
        if (string.IsNullOrWhiteSpace(values.TokenEnvVar))
            Say("TokenEnvVar", $"{SectionPath}:TokenEnvVar names no environment variable.");
        if (values.ConnectTimeout <= TimeSpan.Zero)
            Say("ConnectTimeout", $"{SectionPath}:ConnectTimeout must be positive.");
        if (values.MaxFileBytes <= 0)
            Say("MaxFileBytes", $"{SectionPath}:MaxFileBytes must be positive.");
        if (values.MaxResponseBytes <= 0 || values.MaxResponseBytes > int.MaxValue)
            Say("MaxResponseBytes", $"{SectionPath}:MaxResponseBytes must be positive and at most {int.MaxValue.ToString(CultureInfo.InvariantCulture)}.");
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

    private Values? Bound()
    {
        var values = new Values();
        return SettingsBinding.TryBind(DecisionSection.ToConfiguration(_document.Root), SectionPath, values, []) ? values : null;
    }

    /// <summary>
    /// The section as the engine's <c>ToolBridgeClientOptions</c> shapes it: the same property names,
    /// types and defaults, so the binder reads the file here as it does there. Its public properties
    /// are the section's settings, and nothing else may be public on it.
    /// </summary>
    internal sealed class Values
    {
        public string? Address { get; set; }

        public string TokenEnvVar { get; set; } = DefaultTokenEnvVar;

        public Collection<string> Allow { get; } = [];

        public Collection<string> Deny { get; } = [];

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.Parse(DefaultConnectTimeout, CultureInfo.InvariantCulture);

        public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

        public long MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

        public string? HostHeader { get; set; }
    }
}
