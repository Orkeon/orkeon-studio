using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Launch;

/// <summary>What a <see cref="ToolBridgePreflightFinding"/> says of a launch.</summary>
public enum ToolBridgePreflightKind
{
    /// <summary>
    /// One of the bridge's two sections holds a value the host's start refuses: every run of the file
    /// stops there, a check included, the caller's section (<c>Orkeon:Tools:Bridge</c>) and the serving
    /// one (<c>Orkeon:Bridge</c>) being validated by every host.
    /// </summary>
    SettingsRefused,

    /// <summary>
    /// The run bridges tools (an address, a tool allowed) and the token is not in its environment
    /// variable, or is too short: the caller's link refuses the start.
    /// </summary>
    TokenRefused,

    /// <summary>
    /// The settings name a bridge's address and allow no tool on it: nothing is bridged, every tool runs
    /// on this machine. Information only.
    /// </summary>
    NothingBridged,
}

/// <summary>What the launch is told of the tool bridge.</summary>
/// <param name="Kind">What is said.</param>
/// <param name="Reason">
/// The engine's sentence for a refusal (the first value refused, or what is wrong with the token); the
/// bridge's address for <see cref="ToolBridgePreflightKind.NothingBridged"/>.
/// </param>
/// <param name="Path">The key the refusal is about; null when it is about the token.</param>
public sealed record ToolBridgePreflightFinding(ToolBridgePreflightKind Kind, string Reason, string? Path = null)
{
    /// <summary>Whether the finding stops the run at start, a check included.</summary>
    public bool BlocksStart => Kind != ToolBridgePreflightKind.NothingBridged;
}

/// <summary>
/// Tells, before a run, what the tool bridge of the settings the run reads does to it, as the engine's
/// host starts: both sections are validated whatever the run, and the caller's link reads its token
/// once an address is named and a tool allowed. Whether the bridge answers, serves the tools named and
/// accepts the token is the engine's to find at start: Studio opens no connection.
/// </summary>
public static class ToolBridgePreflight
{
    /// <summary>The finding of a launch, or null when there is nothing to say.</summary>
    /// <param name="settings">The settings file the run reads.</param>
    /// <param name="environment">Reads an environment variable, as the spawned CLI inherits it.</param>
    public static ToolBridgePreflightFinding? Check(AppSettingsDocument settings, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);

        var caller = settings.ToolBridge;
        var refused = caller.Problems().Concat(settings.BridgeServer.Problems()).FirstOrDefault();
        if (refused.Problem is not null)
            return new ToolBridgePreflightFinding(ToolBridgePreflightKind.SettingsRefused, refused.Problem, refused.Path);

        if (caller.IsEnabled)
        {
            var variable = caller.EffectiveTokenEnvVar;
            return ToolBridgeRules.TokenProblem(environment(variable), variable) is { } token
                ? new ToolBridgePreflightFinding(ToolBridgePreflightKind.TokenRefused, token)
                : null;
        }

        return caller.BoundAddress is { } address
            ? new ToolBridgePreflightFinding(ToolBridgePreflightKind.NothingBridged, address)
            : null;
    }
}
