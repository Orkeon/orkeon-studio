using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Presentation;

/// <summary>
/// Both sides of the tool bridge, as text fields: the caller's (<c>Orkeon:Tools:Bridge</c>: the address
/// of the bridge, the tools a run executes there) and the serving one (<c>Orkeon:Bridge</c>: what
/// <c>orkeon bridge serve</c> listens on and runs for a caller). A list is names separated by commas.
/// It stores no token: it names the variable the token is read from. Only a field that changed since
/// it was loaded is written: one left as loaded keeps its key as the file writes it, and a changed
/// field left blank removes its key. A value the engine's start refuses in one of the nine keys this
/// screen shows is refused at apply, in the engine's words, and nothing is then written; a refusal
/// about any other key (a timeout, a size, a limit) blocks no edit here. Those keys stay in the JSON
/// file as written.
/// </summary>
internal sealed class BridgeForm : ISettingsForm
{
    private readonly Func<string, string?> _environment;

    /// <summary>Builds the form; <paramref name="environment"/> reads the token's variable, the process's when null.</summary>
    public BridgeForm(Func<string, string?>? environment = null) =>
        _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <inheritdoc />
    public string Title => "Tool bridge";

    /// <summary>How a list is written.</summary>
    public const string Notice = "Lists: names separated by commas, or *.";

    /// <summary>Where a token comes from: the variable a blank field stands for, never this file.</summary>
    public const string TokenNotice = "Blank token variable: " + ToolBridgeSection.DefaultTokenEnvVar;

    /// <summary>What this screen does not edit.</summary>
    public const string JsonNotice = "Timeouts, sizes, limits: in the JSON file.";

    /// <summary>The heading of the caller's side.</summary>
    public const string CallerHeading = "Tools run elsewhere (" + ToolBridgeSection.SectionPath + ")";

    /// <summary>The heading of the serving side.</summary>
    public const string ServerHeading = "orkeon bridge serve (" + BridgeServerSection.SectionPath + ")";

    /// <summary>The bridge's base address; blank, nothing is bridged.</summary>
    public string Address { get; set; } = "";

    /// <summary>The tools to bridge.</summary>
    public string Allow { get; set; } = "";

    /// <summary>The tools never bridged.</summary>
    public string Deny { get; set; } = "";

    /// <summary>The variable that holds the caller's token; blank, the engine's.</summary>
    public string TokenEnvVar { get; set; } = "";

    /// <summary>The <c>Host</c> header sent to the bridge, when it differs from the address's.</summary>
    public string HostHeader { get; set; } = "";

    /// <summary>The prefix the serving side listens on; blank, the engine's for this machine.</summary>
    public string ServePrefix { get; set; } = "";

    /// <summary>The tools served.</summary>
    public string ServeAllow { get; set; } = "";

    /// <summary>The tools never served.</summary>
    public string ServeDeny { get; set; } = "";

    /// <summary>The variable that holds the serving side's token; blank, the engine's.</summary>
    public string ServeTokenEnvVar { get; set; } = "";

    /// <summary>What a run of the file as loaded bridges, and whether its token is in place.</summary>
    public string Status { get; private set; } = "";

    // The fields as the file gave them: a field left as loaded is not written again, so what the
    // screen cannot show whole (a list the file writes as an object, an entry it reads as blank) stays.
    private (string Address, string Allow, string Deny, string TokenEnvVar, string HostHeader,
        string ServePrefix, string ServeAllow, string ServeDeny, string ServeTokenEnvVar) _loaded;

    /// <inheritdoc />
    public void LoadFrom(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var caller = document.ToolBridge;
        Address = FieldText.FromString(caller.Address);
        Allow = string.Join(", ", caller.Allow);
        Deny = string.Join(", ", caller.Deny);
        TokenEnvVar = FieldText.FromString(caller.TokenEnvVar);
        HostHeader = FieldText.FromString(caller.HostHeader);
        var server = document.BridgeServer;
        ServePrefix = FieldText.FromString(server.Prefix);
        ServeAllow = string.Join(", ", server.Allow);
        ServeDeny = string.Join(", ", server.Deny);
        ServeTokenEnvVar = FieldText.FromString(server.TokenEnvVar);
        _loaded = (Address, Allow, Deny, TokenEnvVar, HostHeader, ServePrefix, ServeAllow, ServeDeny, ServeTokenEnvVar);
        Status = Describe(document);
    }

    /// <summary>The keys this screen edits: a refusal about another key is the JSON file's to fix, and blocks no edit here.</summary>
    private static readonly string[] EditedKeys =
    [
        $"{ToolBridgeSection.SectionPath}:Address", $"{ToolBridgeSection.SectionPath}:Allow", $"{ToolBridgeSection.SectionPath}:Deny",
        $"{ToolBridgeSection.SectionPath}:TokenEnvVar", $"{ToolBridgeSection.SectionPath}:HostHeader",
        $"{BridgeServerSection.SectionPath}:Prefix", $"{BridgeServerSection.SectionPath}:Allow", $"{BridgeServerSection.SectionPath}:Deny",
        $"{BridgeServerSection.SectionPath}:TokenEnvVar",
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> ApplyTo(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        void Write(AppSettingsDocument target)
        {
            var caller = target.ToolBridge;
            if (Address != _loaded.Address)
                caller.Address = FieldText.ToStringOrNull(Address);
            if (Allow != _loaded.Allow)
                caller.Allow = DecisionSection.SplitList(Allow);
            if (Deny != _loaded.Deny)
                caller.Deny = DecisionSection.SplitList(Deny);
            if (TokenEnvVar != _loaded.TokenEnvVar)
                caller.TokenEnvVar = FieldText.ToStringOrNull(TokenEnvVar);
            if (HostHeader != _loaded.HostHeader)
                caller.HostHeader = FieldText.ToStringOrNull(HostHeader);
            var server = target.BridgeServer;
            if (ServePrefix != _loaded.ServePrefix)
                server.Prefix = FieldText.ToStringOrNull(ServePrefix);
            if (ServeAllow != _loaded.ServeAllow)
                server.Allow = DecisionSection.SplitList(ServeAllow);
            if (ServeDeny != _loaded.ServeDeny)
                server.Deny = DecisionSection.SplitList(ServeDeny);
            if (ServeTokenEnvVar != _loaded.ServeTokenEnvVar)
                server.TokenEnvVar = FieldText.ToStringOrNull(ServeTokenEnvVar);
        }

        // Judged on a copy by the rules of the engine's start: what it refuses in a field of this
        // screen is never written. A refusal about a key the screen does not show is the settings
        // check's to report, and blocks no edit here.
        var probe = document.Clone();
        Write(probe);
        var errors = probe.ToolBridge.Problems().Concat(probe.BridgeServer.Problems())
            .Where(problem => EditedKeys.Contains(problem.Path, StringComparer.OrdinalIgnoreCase))
            .Select(problem => problem.Problem)
            .ToList();
        if (errors.Count > 0)
            return errors;

        Write(document);
        LoadFrom(document);
        return [];
    }

    private string Describe(AppSettingsDocument document)
    {
        var caller = document.ToolBridge;
        if (!caller.IsEnabled)
        {
            return caller.BoundAddress is null
                ? "No bridge: every tool runs on this machine."
                : "An address and no tool allowed: nothing is bridged.";
        }

        var variable = caller.EffectiveTokenEnvVar;
        return ToolBridgeRules.TokenProblem(_environment(variable), variable) is { } problem
            ? "A run would refuse to start: " + problem
            : $"Tools are bridged to {caller.BoundAddress}; the token is read from {variable}.";
    }
}
