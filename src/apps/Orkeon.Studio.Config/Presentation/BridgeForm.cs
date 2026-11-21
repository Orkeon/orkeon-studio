using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Presentation;

/// <summary>
/// Both sides of the tool bridge, as text fields: the caller's (<c>Orkeon:Tools:Bridge</c>: the address
/// of the bridge, the tools a run executes there) and the serving one (<c>Orkeon:Bridge</c>: what
/// <c>orkeon bridge serve</c> listens on and runs for a caller). A list is names separated by commas; a
/// field left blank removes its key. It stores no token: it names the variable the token is read from.
/// A value the engine's start refuses is refused at apply, in the engine's words, and nothing is
/// written. The timeouts, sizes and limits stay in the JSON file as written.
/// </summary>
internal sealed class BridgeForm : ISettingsForm
{
    private readonly Func<string, string?> _environment;

    /// <summary>Builds the form; <paramref name="environment"/> reads the token's variable, the process's when null.</summary>
    public BridgeForm(Func<string, string?>? environment = null) =>
        _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <inheritdoc />
    public string Title => "Tool bridge";

    /// <summary>What the two halves are, and what this screen does not edit.</summary>
    public const string Notice =
        "Lists: names separated by commas, or * alone. The token is read from the environment, never from this file. "
        + "Timeouts, sizes and limits: edit them in the JSON file itself.";

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
        Status = Describe(document);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ApplyTo(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        void Write(AppSettingsDocument target)
        {
            var caller = target.ToolBridge;
            caller.Address = FieldText.ToStringOrNull(Address);
            caller.Allow = DecisionSection.SplitList(Allow);
            caller.Deny = DecisionSection.SplitList(Deny);
            caller.TokenEnvVar = FieldText.ToStringOrNull(TokenEnvVar);
            caller.HostHeader = FieldText.ToStringOrNull(HostHeader);
            var server = target.BridgeServer;
            server.Prefix = FieldText.ToStringOrNull(ServePrefix);
            server.Allow = DecisionSection.SplitList(ServeAllow);
            server.Deny = DecisionSection.SplitList(ServeDeny);
            server.TokenEnvVar = FieldText.ToStringOrNull(ServeTokenEnvVar);
        }

        // Judged on a copy by the rules of the engine's start: what it refuses is never written.
        var probe = document.Clone();
        Write(probe);
        var errors = probe.ToolBridge.Problems().Concat(probe.BridgeServer.Problems()).Select(problem => problem.Problem).ToList();
        if (errors.Count > 0)
            return errors;

        Write(document);
        Status = Describe(document);
        return [];
    }

    private string Describe(AppSettingsDocument document)
    {
        var caller = document.ToolBridge;
        if (!caller.IsEnabled)
        {
            return string.IsNullOrWhiteSpace(caller.Address)
                ? "No bridge: every tool runs on this machine."
                : "An address and no tool allowed: nothing is bridged.";
        }

        var variable = caller.EffectiveTokenEnvVar;
        return ToolBridgeRules.TokenProblem(_environment(variable), variable) is { } problem
            ? "A run would refuse to start: " + problem
            : $"Tools are bridged to {caller.Address}; the token is read from {variable}.";
    }
}
