namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The keys of the section and their defaults, as the engine's <c>AgentCliOptions</c> declares them
/// (<see cref="AgentCliSection"/>):
/// bound by the binder with its own conversions, so a value it refuses names the engine's types.
/// </summary>
public sealed class AgentCliSettings
{
    /// <summary>The engine that answers, <c>claude</c> by default and the only one.</summary>
    public string Engine { get; set; } = AgentCliSection.ClaudeEngine;

    /// <summary>The CLI's executable, a name looked up on the <c>PATH</c> or a full path; <c>claude</c> by default.</summary>
    public string Command { get; set; } = "claude";

    /// <summary>How many CLI processes may live at once across the host, 4 by default.</summary>
    public int MaxProcesses { get; set; } = 4;

    /// <summary>The longest tool result handed to the CLI, in characters, 100000 by default.</summary>
    public int MaxToolResultCharacters { get; set; } = 100_000;

    /// <summary>The context size at which the CLI would compact the conversation itself, 100000000 by default.</summary>
    public long AutocompactTokens { get; set; } = 100_000_000;

    /// <summary>Whether a CLI that answers on an API key rather than its login is refused, true by default.</summary>
    public bool RequireLogin { get; set; } = true;
}
