using System.Globalization;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The caller's side of the tool bridge (<c>Orkeon:Tools:Bridge</c>): the tools of a run that execute
/// on another machine, an Orkeon in a container whose browser runs on the desktop, say. Nothing is
/// bridged while <see cref="Address"/> is unset or <see cref="Allow"/> is empty. The token never goes
/// in the file: the section names the environment variable that holds it. The engine's options class
/// is internal, so the defaults are copied (pinned against the engine's settings catalogue by
/// <c>ToolBridgeDefaultsDriftTests</c>) and the rules restated (<see cref="ToolBridgeRules"/>).
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
    public bool Exists => _document.SectionExists(SectionPath);

    /// <summary>The bridge's base address (<c>https://host:7410/</c>); null, nothing is bridged.</summary>
    public string? Address
    {
        get => _document.GetString($"{SectionPath}:Address");
        set => _document.SetString($"{SectionPath}:Address", Blank(value));
    }

    /// <summary>The environment variable that holds the token; null lets <see cref="DefaultTokenEnvVar"/> stand.</summary>
    public string? TokenEnvVar
    {
        get => _document.GetString($"{SectionPath}:TokenEnvVar");
        set => _document.SetString($"{SectionPath}:TokenEnvVar", Blank(value));
    }

    /// <summary>The variable a run reads the token from: the one the section names, else the default.</summary>
    public string EffectiveTokenEnvVar => TokenEnvVar is { } named && !string.IsNullOrWhiteSpace(named) ? named : DefaultTokenEnvVar;

    /// <summary>The tools to bridge, by name, or the lone <c>*</c> for every bridgeable tool the bridge serves.</summary>
    public IReadOnlyList<string> Allow
    {
        get => _document.GetStringArray($"{SectionPath}:Allow");
        set => SetList("Allow", value);
    }

    /// <summary>The tools never bridged, by name; a name here wins over <see cref="Allow"/>.</summary>
    public IReadOnlyList<string> Deny
    {
        get => _document.GetStringArray($"{SectionPath}:Deny");
        set => SetList("Deny", value);
    }

    /// <summary>How long the connection to the bridge may take, as written (<c>hh:mm:ss</c>).</summary>
    public string? ConnectTimeout
    {
        get => _document.GetString($"{SectionPath}:ConnectTimeout");
        set => _document.SetString($"{SectionPath}:ConnectTimeout", Blank(value));
    }

    /// <summary>The largest total, in bytes, of the files of one call.</summary>
    public long? MaxFileBytes
    {
        get => ReadInt64("MaxFileBytes");
        set => WriteInt64("MaxFileBytes", value);
    }

    /// <summary>The largest answer, in bytes, read from the bridge.</summary>
    public long? MaxResponseBytes
    {
        get => ReadInt64("MaxResponseBytes");
        set => WriteInt64("MaxResponseBytes", value);
    }

    /// <summary>The <c>Host</c> header sent to the bridge, when it differs from the address's.</summary>
    public string? HostHeader
    {
        get => _document.GetString($"{SectionPath}:HostHeader");
        set => _document.SetString($"{SectionPath}:HostHeader", Blank(value));
    }

    /// <summary>Whether a run of the file bridges anything: an address, and a tool allowed.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Address) && Allow.Count > 0;

    /// <summary>
    /// What the host's start refuses in the section, in the engine's words and order, each with the
    /// path of its key; empty when the section is accepted. A value the binder cannot convert stops the
    /// start before any rule is read: it is then all that is said.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var problems = new List<(string, string)>();
        var timeout = ToolBridgeValues.Duration(_document, SectionPath, "ConnectTimeout", DefaultConnectTimeout, problems);
        var files = ToolBridgeValues.Int64(_document, SectionPath, "MaxFileBytes", DefaultMaxFileBytes, problems);
        var response = ToolBridgeValues.Int64(_document, SectionPath, "MaxResponseBytes", DefaultMaxResponseBytes, problems);
        if (problems.Count > 0)
            return problems;

        void Say(string key, string problem) => problems.Add(($"{SectionPath}:{key}", problem));
        var address = Address;
        if (!string.IsNullOrWhiteSpace(address)
            && ToolBridgeRules.AddressProblem(address, $"{SectionPath}:Address", allowDockerHost: true) is { } addressProblem)
        {
            Say("Address", addressProblem);
        }

        if (HostHeader is { } host && !string.IsNullOrWhiteSpace(host) && !ToolBridgeRules.IsAuthority(host.Trim()))
            Say("HostHeader", $"{SectionPath}:HostHeader '{host}' is not a host with an optional port (127.0.0.1:7410).");
        if (ToolBridgeValues.IsBlankWritten(_document, SectionPath, "TokenEnvVar"))
            Say("TokenEnvVar", $"{SectionPath}:TokenEnvVar names no environment variable.");
        if (timeout <= TimeSpan.Zero)
            Say("ConnectTimeout", $"{SectionPath}:ConnectTimeout must be positive.");
        if (files <= 0)
            Say("MaxFileBytes", $"{SectionPath}:MaxFileBytes must be positive.");
        if (response <= 0 || response > int.MaxValue)
            Say("MaxResponseBytes", $"{SectionPath}:MaxResponseBytes must be positive and at most {int.MaxValue.ToString(CultureInfo.InvariantCulture)}.");
        problems.AddRange(ToolBridgeValues.Selection(_document, SectionPath));
        return problems;
    }

    /// <summary>Removes the whole section.</summary>
    public void Remove() => _document.Remove(SectionPath);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetList(string key, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            _document.Remove($"{SectionPath}:{key}");
        else
            _document.SetStringArray($"{SectionPath}:{key}", values);
    }

    private long? ReadInt64(string key) =>
        long.TryParse(_document.GetWritten($"{SectionPath}:{key}"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private void WriteInt64(string key, long? value) =>
        _document.SetNode($"{SectionPath}:{key}", value is { } number ? System.Text.Json.Nodes.JsonValue.Create(number) : null);
}
