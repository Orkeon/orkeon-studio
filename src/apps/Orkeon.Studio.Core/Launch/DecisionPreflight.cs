using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Launch;

/// <summary>What a <see cref="DecisionPreflightFinding"/> says of a launch.</summary>
public enum DecisionPreflightKind
{
    /// <summary>
    /// The settings name a decision provider the engine refuses (an unknown name, two providers it could
    /// deduce, a Kev address the key may not travel to, an unreadable switch, a fail-closed LLM judge
    /// while the circuit is off): the runner refuses to start, whatever the crew, a check included.
    /// </summary>
    ProviderRefused,

    /// <summary>
    /// An agent or a task of the crew is routed on <c>llm.profile: auto</c> and the settings give no
    /// ladder that serves it (<c>Orkeon:Decision:Route:Ladder</c>). With no ladder the engine refuses the
    /// crew at load, whatever the circuit: the launch is blocked when the settings are those of the file
    /// the run reads, warned of otherwise. With a ladder one rung of which names no profile of the
    /// settings, it only warns: the run is offered Studio's own model profiles besides. A check stays
    /// offered and reports the engine's line.
    /// </summary>
    RouteWithoutLadder,

    /// <summary>
    /// The crew cannot run without the decision models (<see cref="CrewDecisionMode.Required"/>) and this
    /// run goes without them. A crew that states <c>decision.mode: required</c> is refused before its
    /// first task (exit code 69). A crew that states no mode is refused for an agent or a task that holds
    /// <c>system_one_decide</c>, withheld while the runner resolves tools strictly (the default), or for a
    /// decision task that has neither a fallback nor an <c>otherwise</c> task. The launch is blocked; a
    /// check stays offered: it loads the crew and reports what a run would refuse as a warning.
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
    /// A holder of <c>system_one_decide</c> runs with the circuit off while the runner resolves tools
    /// leniently (<c>Orkeon:CrewFactory:StrictTools</c> false): the engine withholds the tool with a
    /// warning and runs the crew without it. A warning.
    /// </summary>
    ToolWithheld,

    /// <summary>
    /// The crew runs with or without the decision models, and this run goes without them: each decision
    /// task is answered by its relay, answers from its fallback, or lets its <c>otherwise</c> task run, and
    /// what System One picks falls back to what the crew or the host states. Information only.
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
    /// that needs the decision models on a run that goes without them.
    /// </summary>
    public bool BlocksLaunch =>
        Kind == DecisionPreflightKind.RequiredWithoutModel
        || (Kind == DecisionPreflightKind.ProviderRefused && SettingsKnown)
        || (Kind == DecisionPreflightKind.RouteWithoutLadder && SettingsKnown && Status is { HasRouteLadder: false });

    /// <summary>
    /// The agents and tasks on <c>llm.profile: auto</c>, in the crew's order: those a
    /// <see cref="DecisionPreflightKind.RouteWithoutLadder"/> finding is about.
    /// </summary>
    public IReadOnlyList<string> RoutedHolders =>
        [.. Usage.OtherUses.Where(use => use.Kind == CrewSystemOneUseKind.RouteAuto).Select(use => use.Holder)];

    /// <summary>
    /// Whether the finding blocks a check (<c>--validate</c>) too: only a refused provider of the file the
    /// run reads, which stops the host before it loads the crew.
    /// </summary>
    public bool BlocksValidate => Kind == DecisionPreflightKind.ProviderRefused && SettingsKnown;

    /// <summary>The first of <see cref="Reasons"/>; null when there is none.</summary>
    public DecisionOffReason? OffReason => Reasons.Count > 0 ? Reasons[0] : null;

    /// <summary>
    /// Whether the run is refused on the crew's own word (<c>decision.mode: required</c>), before its
    /// first task, with the CLI's exit code 69.
    /// </summary>
    public bool RefusedByMode =>
        Kind == DecisionPreflightKind.RequiredWithoutModel && Usage is { Declared: true, Mode: CrewDecisionMode.Required };
}

/// <summary>
/// Tells, before a run, what the decision models (System One) change to it: the crew's mode
/// (<see cref="CrewDecisionUsage"/>) against the circuit the settings give (<see cref="DecisionSection.Resolve"/>)
/// and the run's own switch (<c>--no-decision</c>). As the engine runs it: the start is refused when the
/// choice of provider is refused; the circuit is off without a provider, under the settings' switch or
/// under the run's, and then no consumer asks the model. A crew that states <c>required</c> is refused
/// before its first task; one that states <c>optional</c> answers each decision task by its relay; one
/// that states no mode has <c>system_one_decide</c> withheld (a refusal at load under strict tool
/// resolution, the default; a warning otherwise, and under a check), answers its decision tasks from
/// their fallbacks, and is refused for a decision task that has neither a fallback nor an
/// <c>otherwise</c> task.
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

        // The run's own switch meets a fail-closed LLM judge: the host refuses to start, as for a
        // switch of the settings.
        if (noDecision && status is { Refusal: null, RefusalOfRunSwitch: { } ofSwitch })
            status = status with { Refusal = ofSwitch };

        if (status?.Refusal is not null)
            return Say(DecisionPreflightKind.ProviderRefused);

        // A profile to pick with no ladder to pick it on: the engine refuses the crew at load.
        if (status is { ServesRouteAuto: false } && usage.OtherUses.Any(use => use.Kind == CrewSystemOneUseKind.RouteAuto))
            return Say(DecisionPreflightKind.RouteWithoutLadder);

        if (usage.Mode == CrewDecisionMode.None)
            return null;

        var noProvider = status is { Provider: null };
        var settingsOff = status is { Enabled: false };
        // Off for sure: the run's own switch, or the circuit of the very file the run reads.
        var offForSure = noDecision || (known && status is { CircuitOn: false });
        // Off as far as is known: the machine's file says so, or nothing was read.
        var offAsFarAsKnown = noDecision || status is { CircuitOn: false };
        var mayBeOff = offAsFarAsKnown || status is null;
        List<DecisionOffReason> Reasons(bool fromSettings)
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

        // Without settings read, the runner's default: strict.
        var strictTools = status?.StrictTools ?? true;
        var holders = !usage.Declared && usage.ToolHolders.Count > 0;
        var unanswered = !usage.Declared && usage.TasksWithoutAnswer.Count > 0;
        var needsModel = usage.Declared
            ? usage.Mode == CrewDecisionMode.Required
            : (holders && strictTools) || unanswered;

        if (needsModel && offForSure)
            return Say(DecisionPreflightKind.RequiredWithoutModel, Reasons(fromSettings: known));

        if (needsModel && !known && mayBeOff)
            return Say(DecisionPreflightKind.RequiredUnknownSettings, Reasons(fromSettings: status is not null));

        if (holders && !strictTools && offAsFarAsKnown)
            return Say(DecisionPreflightKind.ToolWithheld, Reasons(fromSettings: true));

        if (status?.MissingKeyVariable is not null && !offAsFarAsKnown)
            return Say(DecisionPreflightKind.ProviderKeyMissing);

        if (offAsFarAsKnown)
            return Say(DecisionPreflightKind.OptionalWithoutModel, Reasons(fromSettings: true));

        return null;
    }
}
