using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Launch;

/// <summary>What a <see cref="DecisionPreflightFinding"/> says of a launch.</summary>
public enum DecisionPreflightKind
{
    /// <summary>
    /// The settings name a decision provider the engine refuses (an unknown name, two providers it could
    /// deduce, a Kev address the key may not travel to, an unreadable switch): the runner refuses to
    /// start, whatever the crew, a check included.
    /// </summary>
    ProviderRefused,

    /// <summary>
    /// The crew cannot run without the decision models (<see cref="CrewDecisionMode.Required"/>) and this
    /// run cannot give them: an agent or a task holds <c>system_one_decide</c> and no provider is
    /// configured while the runner resolves tools strictly (the default), or a decision task has neither a
    /// fallback nor an <c>otherwise</c> task while the circuit is off. The launch is blocked; a check stays
    /// offered: it loads the crew for such a decision task, and for a holder of the tool it reports the
    /// engine's unknown-tool line (<see cref="DecisionPreflightFinding.RefusesToolAtLoad"/>).
    /// </summary>
    RequiredWithoutModel,

    /// <summary>
    /// The crew cannot run without the decision models, and the settings the CLI will read are not known
    /// (it resolves them itself) while they may not give them: said, never blocking.
    /// </summary>
    RequiredUnknownSettings,

    /// <summary>
    /// The provider the run uses needs a key and none is in place, neither in the file nor in its
    /// variable: each request it gets fails, and with it each decision task that asks it. A warning.
    /// </summary>
    ProviderKeyMissing,

    /// <summary>
    /// A holder of <c>system_one_decide</c> runs while the circuit is off (<c>--no-decision</c> or
    /// <c>Orkeon:Decision:Enabled</c> false): the circuit governs the decision tasks only, so the tool
    /// still asks the provider. A warning.
    /// </summary>
    ToolIgnoresSwitch,

    /// <summary>
    /// A holder of <c>system_one_decide</c> runs without a provider while the runner resolves tools
    /// leniently (<c>Orkeon:CrewFactory:StrictTools</c> false): the engine drops the tool with a log line
    /// and runs the crew without it. A warning.
    /// </summary>
    ToolDroppedWithoutProvider,

    /// <summary>
    /// The crew declares decision tasks that answer without the model, and this run goes without it:
    /// each answers from its fallback, or lets its <c>otherwise</c> task run. Information only.
    /// </summary>
    OptionalWithoutModel,
}

/// <summary>Why a run goes without the decision models.</summary>
public enum DecisionOffReason
{
    /// <summary>The settings configure no decision provider, and no provider's key is in the environment.</summary>
    NoProvider,

    /// <summary>The settings switch the circuit off (<c>Orkeon:Decision:Enabled</c> is false).</summary>
    SettingsSwitch,

    /// <summary>The run switches the circuit off (<c>--no-decision</c>).</summary>
    RunSwitch,
}

/// <summary>What the launch is told of the decision models.</summary>
/// <param name="Kind">What is said.</param>
/// <param name="Usage">What the crew asks of the decision models.</param>
/// <param name="Status">The decision circuit the settings give; null when no settings were read.</param>
/// <param name="SettingsKnown">Whether <paramref name="Status"/> comes from the very file the run reads.</param>
/// <param name="SwitchedOffForRun">Whether the run itself switches the circuit off (<c>--no-decision</c>).</param>
/// <param name="Reasons">
/// Why the run goes without the decision models, in the order they are best fixed (no provider, the
/// settings switch, the run's switch); empty when the finding is not about the circuit being off.
/// </param>
public sealed record DecisionPreflightFinding(
    DecisionPreflightKind Kind,
    CrewDecisionUsage Usage,
    DecisionStatus? Status,
    bool SettingsKnown,
    bool SwitchedOffForRun,
    IReadOnlyList<DecisionOffReason> Reasons)
{
    /// <summary>
    /// Whether the finding blocks « Launch now »: a refused provider of the file the run reads, or a crew
    /// that needs the decision models on a run that cannot give them.
    /// </summary>
    public bool BlocksLaunch =>
        Kind == DecisionPreflightKind.RequiredWithoutModel
        || (Kind == DecisionPreflightKind.ProviderRefused && SettingsKnown);

    /// <summary>
    /// Whether the finding blocks a check (<c>--validate</c>) too: only a refused provider of the file the
    /// run reads, which stops the host before it loads the crew.
    /// </summary>
    public bool BlocksValidate => Kind == DecisionPreflightKind.ProviderRefused && SettingsKnown;

    /// <summary>The first of <see cref="Reasons"/>; null when there is none.</summary>
    public DecisionOffReason? OffReason => Reasons.Count > 0 ? Reasons[0] : null;

    /// <summary>
    /// Whether the engine refuses the crew at load for its holders of <c>system_one_decide</c>: no provider
    /// in the file the run reads, tools resolved strictly. A check (<c>--validate</c>) then fails too, on
    /// the engine's unknown-tool line, while the launch is blocked.
    /// </summary>
    public bool RefusesToolAtLoad =>
        Kind == DecisionPreflightKind.RequiredWithoutModel
        && SettingsKnown
        && Usage.ToolHolders.Count > 0
        && Status is { Provider: null, StrictTools: true };
}

/// <summary>
/// Tells, before a run, what the decision models (System One) change to it: the crew's mode
/// (<see cref="CrewDecisionUsage"/>) against the circuit the settings give (<see cref="DecisionSection.Resolve"/>)
/// and the run's own switch (<c>--no-decision</c>). As the engine runs it: the start is refused when the
/// choice of provider is refused; <c>system_one_decide</c> is registered whenever a provider resolves,
/// whatever the circuit, so its holders fail only without a provider, at load under strict tool
/// resolution (the default, a check included), else the tool is dropped; the circuit governs the
/// decision tasks alone, and with it off a run refuses a decision task that has neither a fallback nor
/// an <c>otherwise</c> task.
/// </summary>
public static class DecisionPreflight
{
    /// <summary>The finding of a launch, or null when there is nothing to say.</summary>
    /// <param name="usage">What the crew asks of the decision models.</param>
    /// <param name="status">
    /// The circuit the settings give: those of the file the run reads, or of the machine's file when the
    /// CLI resolves the file itself; null when none was read.
    /// </param>
    /// <param name="settingsKnown">
    /// Whether <paramref name="status"/> comes from the very file the run reads (a pinned or team file).
    /// While it does not, a verdict of the settings warns and never blocks: the file the CLI picks may
    /// say otherwise. The run's own switch blocks either way.
    /// </param>
    /// <param name="noDecision">Whether the run switches the circuit off.</param>
    public static DecisionPreflightFinding? Check(
        CrewDecisionUsage usage, DecisionStatus? status, bool settingsKnown, bool noDecision)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var known = settingsKnown && status is not null;
        DecisionPreflightFinding Say(DecisionPreflightKind kind, IReadOnlyList<DecisionOffReason>? reasons = null) =>
            new(kind, usage, status, known, noDecision, reasons ?? []);

        if (status?.Refusal is not null)
            return Say(DecisionPreflightKind.ProviderRefused);

        if (usage.Mode == CrewDecisionMode.None)
            return null;

        var holders = usage.ToolHolders.Count > 0;
        var unanswered = usage.TasksWithoutAnswer.Count > 0;
        var noProvider = status is { Provider: null };
        // Without settings read, the runner's default: strict.
        var strictTools = status?.StrictTools ?? true;
        var settingsOff = status is { Enabled: false };
        var circuitOff = noDecision || status is { CircuitOn: false };
        List<DecisionOffReason> TaskReasons(bool fromSettings)
        {
            var reasons = new List<DecisionOffReason>();
            if (fromSettings && noProvider)
                reasons.Add(DecisionOffReason.NoProvider);
            if (fromSettings && settingsOff)
                reasons.Add(DecisionOffReason.SettingsSwitch);
            if (noDecision)
                reasons.Add(DecisionOffReason.RunSwitch);
            return reasons;
        }

        var toolBlocked = holders && known && noProvider && strictTools;
        var tasksBlocked = unanswered && (noDecision || (known && status is { CircuitOn: false }));
        if (toolBlocked || tasksBlocked)
        {
            var reasons = new List<DecisionOffReason>();
            if (toolBlocked)
                reasons.Add(DecisionOffReason.NoProvider);
            if (tasksBlocked)
                reasons.AddRange(TaskReasons(fromSettings: known).Where(reason => !reasons.Contains(reason)));
            return Say(DecisionPreflightKind.RequiredWithoutModel, [.. reasons.Order()]);
        }

        if (!known && ((holders && status?.Provider is null && strictTools) || (unanswered && status is not { CircuitOn: true })))
            return Say(DecisionPreflightKind.RequiredUnknownSettings, TaskReasons(fromSettings: status is not null));

        if (holders && noProvider && !strictTools)
            return Say(DecisionPreflightKind.ToolDroppedWithoutProvider, [DecisionOffReason.NoProvider]);

        if (status?.MissingKeyVariable is not null && (holders || (usage.DecisionTasks.Count > 0 && !circuitOff)))
            return Say(DecisionPreflightKind.ProviderKeyMissing);

        if (holders && status?.Provider is not null && (noDecision || settingsOff))
            return Say(DecisionPreflightKind.ToolIgnoresSwitch, TaskReasons(fromSettings: true));

        if (usage.DecisionTasks.Count > 0 && circuitOff)
            return Say(DecisionPreflightKind.OptionalWithoutModel, TaskReasons(fromSettings: true));

        return null;
    }
}
