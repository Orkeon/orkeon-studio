using System.Collections.ObjectModel;
using System.Globalization;

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

    /// <summary>
    /// The tools to bridge, by name, or the lone <c>*</c> for every bridgeable tool the bridge serves.
    /// Read as the binder reads the key, whatever its shape: a list written as an object gives its
    /// values, an entry that is no text an empty name. Setting an empty list removes the key; setting
    /// the names it already reads writes nothing, so a list shown and applied untouched stays as the
    /// file writes it.
    /// </summary>
    public IReadOnlyList<string> Allow
    {
        get => DecisionSection.Names(Read().Allow);
        set => SetList("Allow", value, Allow);
    }

    /// <summary>
    /// The tools never bridged, by name; a name here wins over <see cref="Allow"/>. Read and written
    /// as <see cref="Allow"/> is.
    /// </summary>
    public IReadOnlyList<string> Deny
    {
        get => DecisionSection.Names(Read().Deny);
        set => SetList("Deny", value, Deny);
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
    public bool IsEnabled => Bound() is { IsEnabled: true };

    /// <summary>The address a run dials, as the engine reads it; null when none is set or the section does not bind.</summary>
    public string? BoundAddress => Bound()?.Address is { } address && !string.IsNullOrWhiteSpace(address) ? address : null;

    /// <summary>
    /// What the host's start refuses in the section, each with the path of its key; empty when the
    /// section is accepted. In the host's order, so the first one is what it says: a file the
    /// configuration does not load, when the key it names is the section's; the values the binder cannot
    /// convert (the host stops on the first, and says the next once that one is mended); the engine's
    /// rules, in the engine's words and order, judged on every value that did convert; and the keys
    /// that are no setting of the section, at any depth.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems() =>
        Judge(DecisionSection.ToConfiguration(_document.Root)).Problems;

    /// <summary>
    /// The section read once off <paramref name="configuration"/>, the document as a run binds it:
    /// what the start refuses in it (<see cref="Problems"/>) and what a run makes of it
    /// (<see cref="IsEnabled"/>, <see cref="EffectiveTokenEnvVar"/>, <see cref="BoundAddress"/>), for a
    /// caller that needs them all of one reading.
    /// </summary>
    internal Judgement Judge(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var problems = new List<(string, string)>();
        if (_document.LoadRefusal() is { } load && SettingsBinding.IsUnder(load.Path, SectionPath))
            problems.Add(load);
        var before = problems.Count;
        var values = SettingsBinding.Bind<Values>(configuration, SectionPath, problems);
        // A value that does not convert: the section does not bind, and a run makes nothing of it.
        var bound = problems.Count == before;

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
        SettingsBinding.AddWalked(problems, SettingsBinding.UnknownKeys(configuration, SectionPath, typeof(Values)));
        return new Judgement(
            problems,
            bound && values.IsEnabled,
            bound && !string.IsNullOrWhiteSpace(values.TokenEnvVar) ? values.TokenEnvVar : DefaultTokenEnvVar,
            bound && !string.IsNullOrWhiteSpace(values.Address) ? values.Address : null);
    }

    /// <summary>One reading of the section (<see cref="Judge"/>).</summary>
    /// <param name="Problems">What the host's start refuses, as <see cref="ToolBridgeSection.Problems"/> gives it.</param>
    /// <param name="IsEnabled">As <see cref="ToolBridgeSection.IsEnabled"/>.</param>
    /// <param name="TokenEnvVar">As <see cref="EffectiveTokenEnvVar"/>.</param>
    /// <param name="Address">As <see cref="BoundAddress"/>.</param>
    internal sealed record Judgement(
        IReadOnlyList<(string Path, string Problem)> Problems, bool IsEnabled, string TokenEnvVar, string? Address);

    private string Key(string key) => _document.PathAsWritten($"{SectionPath}:{key}");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetList(string key, IReadOnlyList<string> values, IReadOnlyList<string> read)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            _document.Remove(Key(key));
        else if (!values.SequenceEqual(read, StringComparer.Ordinal))
            _document.SetStringArray(Key(key), values);
    }

    // The section as the binder reads it, each value that does not convert left on its default.
    private Values Read() => SettingsBinding.Bind<Values>(DecisionSection.ToConfiguration(_document.Root), SectionPath, []);

    private Values? Bound()
    {
        var unconverted = new List<(string Path, string Problem)>();
        var values = SettingsBinding.Bind<Values>(DecisionSection.ToConfiguration(_document.Root), SectionPath, unconverted);
        return unconverted.Count == 0 ? values : null;
    }

    /// <summary>
    /// The section as the engine's <c>ToolBridgeClientOptions</c> shapes it: the same property names,
    /// types, defaults and order, so the binder reads the file here as it does there. Its public
    /// properties the binder writes or fills are the keys the section may carry; the two computed ones
    /// are none, as the engine's start reads its class since Orkeon #449. Nothing else may be public on it.
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

        public bool IsDockerHostOverHttp =>
            Uri.TryCreate(Address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && ToolBridgeRules.IsDockerHost(uri);

        public bool IsEnabled => !string.IsNullOrWhiteSpace(Address) && Allow.Count > 0;
    }
}
