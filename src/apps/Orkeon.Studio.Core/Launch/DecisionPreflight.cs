using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Launch;

/// <summary>What a <see cref="DecisionPreflightFinding"/> says of a launch.</summary>
public enum DecisionPreflightKind
{
    /// <summary>
    /// The settings name a decision provider the engine refuses (an unknown name, two providers it could
    /// deduce, a Kev address the key may not travel to, an unreadable switch, a fail-closed LLM judge
    /// while the circuit is off), or one of their decision profiles bears the name of a model profile
    /// the launch lays in the run's environment: the runner refuses to start, whatever the crew, a
    /// check included.
    /// </summary>
    ProviderRefused,

    /// <summary>
    /// An agent or a task of the crew is routed on <c>llm.profile: auto</c> and the ladder of the settings
    /// (<c>Orkeon:Decision:Route:Ladder</c>) does not serve it: there is none, or one of its rungs names a
    /// profile neither the settings define nor the launch lays in the run's environment
    /// (<see cref="DecisionPreflightFinding.UnknownRungs"/>). The engine refuses the crew at load,
    /// whatever the circuit: the launch is blocked when the settings are those of the file the run reads,
    /// warned of otherwise. A check stays offered and reports the engine's line.
    /// </summary>
    RouteWithoutLadder,

    /// <summary>
    /// The crew states <c>decision.mode: none</c> and an agent or a task is routed on
    /// <c>llm.profile: auto</c>, or names a profile the settings define as a decision profile: the engine
    /// refuses the crew at load, the mode refusing any use of System One, and it judges the mode before
    /// the ladder. A holder on <c>auto</c> is refused whatever the settings, so the launch is blocked; a
    /// decision profile is one of the settings, so the launch is blocked when they are those of the file
    /// the run reads, warned of otherwise. A check stays offered and reports the engine's line.
    /// </summary>
    ProfileRefusedByMode,

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
    /// An agent or a task names a decision profile of the settings that is fail-closed and carries a
    /// guard stage (<see cref="DecisionStatus.BlockingProfiles"/>), and this run goes without the
    /// decision models (any off cause, the provider's missing key included): the guard cannot judge, and
    /// the engine blocks every call made through the profile. The launch is blocked when the settings are those of the file the run reads, warned of
    /// otherwise; a check loads the crew and says nothing of it.
    /// </summary>
    ProfileBlockedWithoutModel,

    /// <summary>
    /// The crew cannot run without the decision models, and the settings the CLI will read are not known
    /// (it resolves them itself) while they may not give them: said, never blocking.
    /// </summary>
    RequiredUnknownSettings,

    /// <summary>
    /// The provider the run uses needs a key and none is in place, neither in the file nor in its
    /// variable: the engine turns the circuit off (<see cref="DecisionOffReason.NoKey"/>) and sends the
    /// provider no request, and the crew runs without the decision models (its relays, its fallbacks,
    /// its <c>otherwise</c> tasks). A warning, for a crew that can run without them: one that needs them
    /// is refused, a fail-closed guarded profile blocks and a lenient runner withholds the tool, as for
    /// any other off cause.
    /// </summary>
    ProviderKeyMissing,

    /// <summary>
    /// A holder of <c>system_one_decide</c> runs with the circuit off (any off cause, the provider's
    /// missing key included) while the runner resolves tools leniently (<c>Orkeon:CrewFactory:StrictTools</c> false): the engine withholds the tool with a
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

/// <summary>How much a <see cref="DecisionPreflightFinding"/> weighs on a launch, the heaviest first.</summary>
public enum DecisionPreflightSeverity
{
    /// <summary>The host refuses to start: neither a run nor a check is launched.</summary>
    BlocksValidate,

    /// <summary>The run is refused or cannot go through: « Launch now » is blocked, a check stays offered.</summary>
    BlocksLaunch,

    /// <summary>The run may fail, or loses something: said, never blocking.</summary>
    Warning,

    /// <summary>The run goes on another way: said for information.</summary>
    Information,
}

/// <summary>Why a run goes without the decision models.</summary>
public enum DecisionOffReason
{
    /// <summary>The settings configure no decision provider, and no provider's key is in the environment.</summary>
    NoProvider,

    /// <summary>The settings switch the circuit off (<c>Orkeon:Decision:Enabled</c> is false).</summary>
    SettingsSwitch,

    /// <summary>
    /// The provider needs a key and none is in place (<see cref="DecisionStatus.KeyMissing"/>): the engine
    /// turns the circuit off (cause <c>no-key</c>, Orkeon #451). Said of a crew the engine refuses for it
    /// (<see cref="DecisionPreflightKind.RequiredWithoutModel"/>, <see cref="DecisionPreflightKind.RequiredUnknownSettings"/>),
    /// of a fail-closed guarded profile it blocks (<see cref="DecisionPreflightKind.ProfileBlockedWithoutModel"/>)
    /// and of the tool a lenient runner withholds (<see cref="DecisionPreflightKind.ToolWithheld"/>); any
    /// other crew is warned of the key (<see cref="DecisionPreflightKind.ProviderKeyMissing"/>).
    /// </summary>
    NoKey,

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
/// settings switch, the provider's missing key, the run's switch); empty when the finding is not about
/// the circuit being off.
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
    /// Whether the finding blocks « Launch now »: a refused provider of the file the run reads; a crew
    /// that needs the decision models on a run that goes without them; a crew that states
    /// <c>decision.mode: none</c> and is routed on <c>llm.profile: auto</c>; and, when the settings are
    /// those of the file the run reads, a ladder that does not serve a routed crew, a decision profile
    /// under the mode none, and a fail-closed guarded profile on a run without the decision models.
    /// </summary>
    public bool BlocksLaunch => Kind switch
    {
        DecisionPreflightKind.RequiredWithoutModel => true,
        DecisionPreflightKind.ProfileRefusedByMode => SettingsKnown || RoutedHolders.Count > 0,
        DecisionPreflightKind.ProviderRefused or DecisionPreflightKind.RouteWithoutLadder
            or DecisionPreflightKind.ProfileBlockedWithoutModel => SettingsKnown,
        _ => false,
    };

    /// <summary>
    /// Whether the finding blocks a check (<c>--validate</c>) too: only a refused provider of the file the
    /// run reads, which stops the host before it loads the crew.
    /// </summary>
    public bool BlocksValidate => Kind == DecisionPreflightKind.ProviderRefused && SettingsKnown;

    /// <summary>How much the finding weighs on the launch: what a screen shows it as.</summary>
    public DecisionPreflightSeverity Severity =>
        BlocksValidate ? DecisionPreflightSeverity.BlocksValidate
        : BlocksLaunch ? DecisionPreflightSeverity.BlocksLaunch
        : Kind == DecisionPreflightKind.OptionalWithoutModel ? DecisionPreflightSeverity.Information
        : DecisionPreflightSeverity.Warning;

    /// <summary>
    /// The rungs of the ladder that name a profile neither the settings define nor the launch lays in
    /// the run's environment: what a <see cref="DecisionPreflightKind.RouteWithoutLadder"/> finding is
    /// about when the settings give a ladder. Empty when they give none.
    /// </summary>
    public IReadOnlyList<string> UnknownRungs { get; init; } = [];

    /// <summary>
    /// The agents and tasks on <c>llm.profile: auto</c>, in the crew's order: those a
    /// <see cref="DecisionPreflightKind.RouteWithoutLadder"/> finding is about, and the first a
    /// <see cref="DecisionPreflightKind.ProfileRefusedByMode"/> finding is about.
    /// </summary>
    public IReadOnlyList<string> RoutedHolders =>
        [.. Usage.OtherUses.Where(use => use.Kind == CrewSystemOneUseKind.RouteAuto).Select(use => use.Holder)];

    /// <summary>
    /// The agents and tasks that name a decision profile of the settings, in the crew's order: those a
    /// <see cref="DecisionPreflightKind.ProfileRefusedByMode"/> finding is about, beside
    /// <see cref="RoutedHolders"/>.
    /// </summary>
    public IReadOnlyList<string> DecisionProfileHolders =>
        [.. Usage.OtherUses.Where(use => use.Kind == CrewSystemOneUseKind.DecisionProfile).Select(use => use.Holder)];

    /// <summary>
    /// The agents and tasks that name a fail-closed decision profile with a guard stage
    /// (<see cref="DecisionStatus.BlockingProfiles"/>), in the crew's order: those a
    /// <see cref="DecisionPreflightKind.ProfileBlockedWithoutModel"/> finding is about.
    /// </summary>
    public IReadOnlyList<string> BlockedProfileHolders => DecisionPreflight.BlockedHolders(Usage, Status);

    /// <summary>
    /// Whether the finding is read on settings that only stand in for the run's (the machine's file,
    /// while the CLI picks the file itself) and would not hold on another file: a screen then says so
    /// and states nothing as a fact. False for what holds whatever the file: a holder on
    /// <c>llm.profile: auto</c> under the mode none, a crew that needs the decision models on a run
    /// that switches them off itself, and a run that goes without them on its own switch alone.
    /// </summary>
    public bool OnStandInSettings => !SettingsKnown && Status is not null && Kind switch
    {
        DecisionPreflightKind.ProfileRefusedByMode => RoutedHolders.Count == 0,
        DecisionPreflightKind.RequiredWithoutModel => false,
        DecisionPreflightKind.OptionalWithoutModel => Reasons.Any(reason => reason != DecisionOffReason.RunSwitch),
        _ => true,
    };

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
/// choice of provider is refused; a crew that states <c>none</c> and asks System One for a profile is
/// refused at load, then a crew routed on <c>llm.profile: auto</c> that the ladder does not serve; the
/// circuit is off without a provider, under the settings' switch or under the run's, and then no
/// consumer asks the model. A crew that states <c>required</c> is refused before its first task; one
/// that states <c>optional</c> answers each decision task by its relay; one that states no mode has
/// <c>system_one_decide</c> withheld (a refusal at load under strict tool resolution, the default; a
/// warning otherwise, and under a check), answers its decision tasks from their fallbacks, and is
/// refused for a decision task that has neither a fallback nor an <c>otherwise</c> task. A fail-closed
/// decision profile with a guard stage blocks every call made through it once the circuit is off.
/// </summary>
public static class DecisionPreflight
{
    /// <summary>
    /// The finding of a launch, or null when there is nothing to say. One finding is returned, the
    /// heaviest (<see cref="DecisionPreflightSeverity"/>): what blocks a check, then what blocks
    /// « Launch now », then a warning, then information, so that a warning never hides a refusal. Of two
    /// findings that weigh the same, the one the engine meets first: the provider (the host's start),
    /// the mode then the ladder (the crew's load), the crew's need of the models (its load, or the entry
    /// of its run), a blocking profile (the first call), the tool withheld, the provider's key.
    /// </summary>
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
    /// <param name="offeredProfiles">
    /// The ids of the LLM profiles the launch lays in the run's environment besides those of the
    /// settings: a rung of the ladder that names one is served, and a decision profile of the settings
    /// that bears the name of one is refused, two profiles sharing a name. Null or empty when the
    /// launch lays none.
    /// </param>
    public static DecisionPreflightFinding? Check(
        CrewDecisionUsage usage,
        DecisionStatus? status,
        bool settingsKnown,
        bool noDecision,
        IReadOnlyCollection<string>? offeredProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var stated = usage;
        usage = Effective(usage, status);
        var known = settingsKnown && status is not null;

        // A model profile the launch lays in the environment is registered beside those of the file: a
        // decision profile of the same name refuses the start, as two of the file do, and before the
        // judge is looked at.
        if (status is { RefusalStage: DecisionRefusalStage.None or DecisionRefusalStage.Judge }
            && offeredProfiles is { Count: > 0 }
            && status.DecisionProfiles.FirstOrDefault(name => offeredProfiles.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } twice)
        {
            status = status with { Refusal = DecisionSection.SharedProfileNameRefusal(twice), RefusalStage = DecisionRefusalStage.Profiles };
        }

        // The run's own switch meets a fail-closed LLM judge: the host refuses to start, as for a
        // switch of the settings.
        if (noDecision && status is { Refusal: null, RefusalOfRunSwitch: { } ofSwitch })
            status = status with { Refusal = ofSwitch };

        // In the engine's order; the heaviest is kept, the first of those that weigh the same.
        var findings = new List<DecisionPreflightFinding>();
        void Say(DecisionPreflightKind kind, IReadOnlyList<DecisionOffReason>? reasons = null, IReadOnlyList<string>? unknownRungs = null) =>
            findings.Add(new(kind, usage, status, known, noDecision, reasons ?? []) { UnknownRungs = unknownRungs ?? [] });

        if (status?.Refusal is not null)
            Say(DecisionPreflightKind.ProviderRefused);

        // A crew that states none refuses any use of System One, a profile it picks or a decision
        // profile included: the load fails, and the mode is judged before the ladder.
        var routed = usage.OtherUses.Any(use => use.Kind == CrewSystemOneUseKind.RouteAuto);
        if (stated is { Declared: true, Mode: CrewDecisionMode.None }
            && usage.OtherUses.Any(use => use.Kind is CrewSystemOneUseKind.RouteAuto or CrewSystemOneUseKind.DecisionProfile))
        {
            Say(DecisionPreflightKind.ProfileRefusedByMode);
        }

        // A profile to pick with no ladder to pick it on, or on a rung the run is not offered: the
        // engine refuses the crew at load.
        if (routed && status is not null)
        {
            if (!status.HasRouteLadder)
            {
                Say(DecisionPreflightKind.RouteWithoutLadder);
            }
            else if (status.UnknownRungs
                .Where(rung => offeredProfiles?.Contains(rung, StringComparer.OrdinalIgnoreCase) != true)
                .ToList() is { Count: > 0 } unknownRungs)
            {
                Say(DecisionPreflightKind.RouteWithoutLadder, unknownRungs: unknownRungs);
            }
        }

        var noProvider = status is { Provider: null };
        var settingsOff = status is { Enabled: false };
        // The engine turns the circuit the settings give off when the provider's key is missing (no-key,
        // Orkeon #451): a crew that needs the decision models is refused, a fail-closed guarded profile
        // blocks and a lenient runner withholds the tool, as for any other off cause. Any other crew keeps
        // the warning of the key below.
        var keyOff = status is { CircuitOn: true, KeyMissing: true };
        // Off for sure: the run's own switch, or the circuit of the very file the run reads.
        var offForSure = noDecision || (known && status is { CircuitOn: false });
        var offForSureForNeeds = offForSure || (known && keyOff);
        // Off as far as is known: the machine's file says so, or nothing was read.
        var offAsFarAsKnown = noDecision || status is { CircuitOn: false };
        var offWithKey = offAsFarAsKnown || keyOff;
        var mayBeOff = offAsFarAsKnown || status is null;
        // The missing key is said where the engine's circuit is off for it; a crew that runs either way is
        // warned of the key instead (ProviderKeyMissing), unless another cause turns the circuit off.
        List<DecisionOffReason> Reasons(bool fromSettings, bool withKey = true)
        {
            var reasons = new List<DecisionOffReason>();
            if (fromSettings && noProvider)
                reasons.Add(DecisionOffReason.NoProvider);
            if (fromSettings && settingsOff)
                reasons.Add(DecisionOffReason.SettingsSwitch);
            if (fromSettings && keyOff && withKey)
                reasons.Add(DecisionOffReason.NoKey);
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

        if (needsModel && offForSureForNeeds)
            Say(DecisionPreflightKind.RequiredWithoutModel, Reasons(fromSettings: known));
        else if (needsModel && !known && (mayBeOff || keyOff))
            Say(DecisionPreflightKind.RequiredUnknownSettings, Reasons(fromSettings: status is not null));

        // A fail-closed profile with a guard stage: its guard cannot judge, every call through it is
        // blocked, whatever turns the circuit off (the missing key included).
        if (offWithKey && BlockedHolders(usage, status).Count > 0)
            Say(DecisionPreflightKind.ProfileBlockedWithoutModel, Reasons(fromSettings: true));

        if (holders && !strictTools && offWithKey)
            Say(DecisionPreflightKind.ToolWithheld, Reasons(fromSettings: true));

        // A crew that asks nothing of the decision models is told nothing of their provider.
        var asks = usage.Mode != CrewDecisionMode.None;
        if (asks && status is { KeyMissing: true } && !offAsFarAsKnown)
            Say(DecisionPreflightKind.ProviderKeyMissing);

        if (asks && offAsFarAsKnown)
            Say(DecisionPreflightKind.OptionalWithoutModel, Reasons(fromSettings: true, withKey: false));

        // A stable sort: the engine's order holds among those that weigh the same.
        return findings.OrderBy(finding => finding.Severity).FirstOrDefault();
    }

    /// <summary>
    /// The agents and tasks of <paramref name="usage"/> that name a decision profile
    /// <paramref name="status"/> gives as blocking once the circuit is off
    /// (<see cref="DecisionStatus.BlockingProfiles"/>), in the crew's order.
    /// </summary>
    internal static IReadOnlyList<string> BlockedHolders(CrewDecisionUsage usage, DecisionStatus? status) =>
        status is { BlockingProfiles.Count: > 0 }
            ? [.. usage.NamedProfiles
                .Where(named => status.BlockingProfiles.Contains(named.Profile, StringComparer.OrdinalIgnoreCase))
                .Select(named => named.Holder)]
            : [];

    /// <summary>
    /// What the crew asks once the settings are read: the profiles it names that the settings define as
    /// decision profiles are uses of System One too (their stages follow the circuit), and a crew that
    /// stated no mode and asked nothing else then runs with the decision models or without them. The
    /// usage itself when the settings define no such profile, or were not read.
    /// </summary>
    public static CrewDecisionUsage Effective(CrewDecisionUsage usage, DecisionStatus? status)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (status is not { DecisionProfiles.Count: > 0 })
            return usage;

        var uses = usage.NamedProfiles
            .Where(named => status.DecisionProfiles.Contains(named.Profile, StringComparer.OrdinalIgnoreCase))
            .Select(named => new CrewSystemOneUse(CrewSystemOneUseKind.DecisionProfile, named.Holder))
            .ToList();
        if (uses.Count == 0)
            return usage;

        return usage with
        {
            OtherUses = [.. usage.OtherUses, .. uses],
            Mode = usage is { Declared: false, Mode: CrewDecisionMode.None } ? CrewDecisionMode.Optional : usage.Mode,
        };
    }
}
