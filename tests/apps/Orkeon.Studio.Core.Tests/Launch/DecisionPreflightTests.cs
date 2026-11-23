using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Tests.Launch;

/// <summary>
/// What a launch is told of the decision models, as the engine runs a crew: a refused provider stops
/// every start; a crew that states <c>required</c> needs the circuit on, one that states
/// <c>optional</c> goes on its relays; of a crew that states no mode, <c>system_one_decide</c> and a
/// decision task with no answer of its own need the circuit on, and one that runs either way is told it
/// goes on its fallbacks.
/// </summary>
public sealed class DecisionPreflightTests
{
    private static readonly CrewDecisionUsage Optional = new(
        CrewDecisionMode.Optional,
        [new CrewDecisionTask("pick", ["go"], new Dictionary<string, string> { ["go"] = "no" }, null)],
        []);

    private static readonly CrewDecisionUsage Required = new(CrewDecisionMode.Required, [], ["analyst"]);

    /// <summary>A decision task with neither a fallback nor an otherwise task: the circuit must be on.</summary>
    private static readonly CrewDecisionUsage Unanswered = new(
        CrewDecisionMode.Required,
        [new CrewDecisionTask("pick", ["go"], new Dictionary<string, string>(), null)],
        []);

    /// <summary>A crew that states <c>decision.mode: required</c>, and asks System One a decision.</summary>
    private static readonly CrewDecisionUsage DeclaredRequired = new(
        CrewDecisionMode.Required,
        [new CrewDecisionTask("pick", ["go"], new Dictionary<string, string>(), null)],
        ["analyst"])
    {
        Declared = true,
    };

    /// <summary>A crew that states <c>decision.mode: optional</c>: its decision task has a relay.</summary>
    private static readonly CrewDecisionUsage DeclaredOptional = new(
        CrewDecisionMode.Optional,
        [new CrewDecisionTask("pick", ["go"], new Dictionary<string, string>(), null) { RelayAgent = "analyst" }],
        [])
    {
        Declared = true,
    };

    private static readonly DecisionStatus On = new(true, DecisionProviderDescriptor.Liquid, DecisionProviderSelection.Deduced, [], null);

    private static readonly DecisionStatus NoProvider = new(true, null, DecisionProviderSelection.Deduced, [], null);

    private static readonly DecisionStatus SwitchedOff = On with { Enabled = false };

    private static readonly DecisionStatus Refused = NoProvider with { Refusal = "Both TypeSafe and Liquid are configured" };

    [Fact]
    public void A_crew_that_asks_nothing_is_told_nothing()
    {
        Assert.Null(DecisionPreflight.Check(CrewDecisionUsage.None, NoProvider, settingsKnown: true, noDecision: true));
        Assert.Null(DecisionPreflight.Check(CrewDecisionUsage.None, null, settingsKnown: true, noDecision: false));
    }

    [Fact]
    public void A_refused_provider_blocks_every_start_a_check_included()
    {
        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(CrewDecisionUsage.None, Refused, settingsKnown: true, noDecision: false));

        Assert.Equal(DecisionPreflightKind.ProviderRefused, finding.Kind);
        Assert.True(finding.BlocksLaunch);
        Assert.True(finding.BlocksValidate);
    }

    [Theory]
    [MemberData(nameof(RunsWithoutTheModel))]
    public void A_decision_task_with_no_answer_is_blocked_when_the_run_goes_without_the_model(DecisionStatus status, bool noDecision)
    {
        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Unanswered, status, settingsKnown: true, noDecision));

        Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, finding.Kind);
        Assert.True(finding.BlocksLaunch);
        Assert.False(finding.BlocksValidate);
        Assert.Equal(noDecision, finding.SwitchedOffForRun);
    }

    [Fact]
    public void A_lenient_runner_withholds_the_tool_once_the_circuit_is_off_a_warning_that_never_blocks()
    {
        var lenient = NoProvider with { StrictTools = false };

        var withheld = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, lenient, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ToolWithheld, withheld.Kind);
        Assert.Equal([DecisionOffReason.NoProvider], withheld.Reasons);
        Assert.False(withheld.BlocksLaunch);
        Assert.False(withheld.BlocksValidate);

        // The machine's file stands in for the run's: a lenient one warns the same, never of unknown settings.
        var unknown = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, lenient, settingsKnown: false, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ToolWithheld, unknown.Kind);

        // Whatever switches the circuit off: the run's own switch withholds the tool as well.
        var switched = Assert.IsType<DecisionPreflightFinding>(
            DecisionPreflight.Check(Required, On with { StrictTools = false }, settingsKnown: true, noDecision: true));
        Assert.Equal(DecisionPreflightKind.ToolWithheld, switched.Kind);
        Assert.Equal([DecisionOffReason.RunSwitch], switched.Reasons);

        // A decision task with no answer still needs the circuit: that block stays.
        var both = new CrewDecisionUsage(CrewDecisionMode.Required, Unanswered.DecisionTasks, ["analyst"]);
        var blocked = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(both, lenient, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, blocked.Kind);
        Assert.True(blocked.BlocksLaunch);
    }

    /// <summary>
    /// With the circuit off no consumer asks the model: the engine withholds <c>system_one_decide</c>
    /// whatever the cause, and a strict runner refuses the crew that lists it. A check reports it as a
    /// warning, so it stays offered.
    /// </summary>
    [Theory]
    [MemberData(nameof(RunsWithoutTheModel))]
    public void A_strict_runner_refuses_a_holder_of_the_tool_whatever_switches_the_circuit_off(DecisionStatus status, bool noDecision)
    {
        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, status, settingsKnown: true, noDecision));

        Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, finding.Kind);
        Assert.True(finding.BlocksLaunch);
        Assert.False(finding.BlocksValidate);
        Assert.False(finding.RefusedByMode);
    }

    [Theory]
    [MemberData(nameof(RunsWithoutTheModel))]
    public void A_crew_that_states_required_is_refused_whatever_switches_the_circuit_off(DecisionStatus status, bool noDecision)
    {
        // Lenient tools change nothing to it: the mode refuses the run, not the tool.
        var finding = Assert.IsType<DecisionPreflightFinding>(
            DecisionPreflight.Check(DeclaredRequired, status with { StrictTools = false }, settingsKnown: true, noDecision));

        Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, finding.Kind);
        Assert.True(finding.BlocksLaunch);
        Assert.False(finding.BlocksValidate);
        Assert.True(finding.RefusedByMode);
    }

    [Fact]
    public void A_crew_that_states_required_runs_with_the_circuit_on_and_is_warned_while_the_settings_are_unknown()
    {
        Assert.Null(DecisionPreflight.Check(DeclaredRequired, On, settingsKnown: true, noDecision: false));
        Assert.Equal(
            DecisionPreflightKind.RequiredUnknownSettings,
            DecisionPreflight.Check(DeclaredRequired, NoProvider, settingsKnown: false, noDecision: false)?.Kind);
        Assert.Equal(
            DecisionPreflightKind.RequiredUnknownSettings,
            DecisionPreflight.Check(DeclaredRequired, null, settingsKnown: false, noDecision: false)?.Kind);
    }

    [Theory]
    [MemberData(nameof(RunsWithoutTheModel))]
    public void A_crew_that_states_optional_is_told_its_relays_answer(DecisionStatus status, bool noDecision)
    {
        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(DeclaredOptional, status, settingsKnown: true, noDecision));

        Assert.Equal(DecisionPreflightKind.OptionalWithoutModel, finding.Kind);
        Assert.False(finding.BlocksLaunch);
        Assert.Equal("analyst", Assert.Single(finding.Usage.DecisionTasks).RelayAgent);
        Assert.Null(DecisionPreflight.Check(DeclaredOptional, On, settingsKnown: true, noDecision: false));
    }

    [Fact]
    public void A_crew_that_only_lets_System_One_pick_is_told_when_the_run_goes_without_it()
    {
        var picks = new CrewDecisionUsage(CrewDecisionMode.Optional, [], [])
        {
            OtherUses = [new CrewSystemOneUse(CrewSystemOneUseKind.RouteAuto, "analyst")],
        };

        var served = On with { RouteLadder = ["default", "strong"] };
        Assert.Equal(DecisionPreflightKind.OptionalWithoutModel, DecisionPreflight.Check(picks, served, settingsKnown: true, noDecision: true)?.Kind);
        Assert.Null(DecisionPreflight.Check(picks, served, settingsKnown: true, noDecision: false));
    }

    /// <summary>
    /// A profile to pick with no ladder to pick it on: the engine refuses the crew when it loads it,
    /// whatever the circuit. Blocked on the file the run reads, warned of on the machine's.
    /// </summary>
    [Fact]
    public void A_crew_on_profile_auto_is_stopped_without_a_ladder_that_serves_it()
    {
        var picks = new CrewDecisionUsage(CrewDecisionMode.Optional, [], [])
        {
            OtherUses =
            [
                new CrewSystemOneUse(CrewSystemOneUseKind.RouteAuto, "analyst"),
                new CrewSystemOneUse(CrewSystemOneUseKind.EffortAuto, "writer"),
            ],
        };

        var none = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(picks, On, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RouteWithoutLadder, none.Kind);
        Assert.Equal(["analyst"], none.RoutedHolders);
        Assert.True(none.BlocksLaunch);
        Assert.False(none.BlocksValidate);

        Assert.Empty(none.UnknownRungs);
        Assert.Equal(DecisionPreflightSeverity.BlocksLaunch, none.Severity);

        var machine = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(picks, On, settingsKnown: false, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RouteWithoutLadder, machine.Kind);
        Assert.False(machine.BlocksLaunch);
        Assert.Equal(DecisionPreflightSeverity.Warning, machine.Severity);
        Assert.True(machine.OnStandInSettings);

        // No settings read: nothing is known of the ladder, nothing is said.
        Assert.Null(DecisionPreflight.Check(picks, null, settingsKnown: false, noDecision: false));

        // Thinking on auto needs no ladder.
        var thinking = picks with { OtherUses = [new CrewSystemOneUse(CrewSystemOneUseKind.EffortAuto, "writer")] };
        Assert.Null(DecisionPreflight.Check(thinking, On, settingsKnown: true, noDecision: false));
    }

    /// <summary>
    /// A rung the file does not define is refused at load unless the launch lays that profile in the
    /// run's environment: blocked on the file the run reads, warned of on the machine's, and nothing
    /// is said of a rung the launch offers, in whatever letter case.
    /// </summary>
    [Fact]
    public void A_rung_neither_the_file_defines_nor_the_launch_offers_stops_a_crew_on_profile_auto()
    {
        var picks = new CrewDecisionUsage(CrewDecisionMode.Optional, [], [])
        {
            OtherUses = [new CrewSystemOneUse(CrewSystemOneUseKind.RouteAuto, "analyst")],
        };
        var ladder = On with { RouteLadder = ["default", "strong", "cheap"], UnknownRungs = ["strong", "cheap"] };

        var nothingLaid = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(picks, ladder, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RouteWithoutLadder, nothingLaid.Kind);
        Assert.Equal(["strong", "cheap"], nothingLaid.UnknownRungs);
        Assert.True(nothingLaid.BlocksLaunch);
        Assert.False(nothingLaid.BlocksValidate);

        var oneLaid = Assert.IsType<DecisionPreflightFinding>(
            DecisionPreflight.Check(picks, ladder, settingsKnown: true, noDecision: false, offeredProfiles: ["Strong"]));
        Assert.Equal(["cheap"], oneLaid.UnknownRungs);
        Assert.True(oneLaid.BlocksLaunch);

        Assert.Null(DecisionPreflight.Check(picks, ladder, settingsKnown: true, noDecision: false, offeredProfiles: ["cheap", "strong", "other"]));

        var machine = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(picks, ladder, settingsKnown: false, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RouteWithoutLadder, machine.Kind);
        Assert.False(machine.BlocksLaunch);
        Assert.True(machine.OnStandInSettings);

        // The rungs trouble no crew that picks no profile.
        Assert.Null(DecisionPreflight.Check(CrewDecisionUsage.None, ladder, settingsKnown: true, noDecision: false));
    }

    /// <summary>
    /// A crew that states none refuses a profile System One picks, whatever the settings: the engine
    /// judges the mode before the ladder, with one or without, and Studio blocks even on a file it has
    /// not read.
    /// </summary>
    [Fact]
    public void A_crew_that_states_none_is_refused_for_a_holder_on_profile_auto_whatever_the_ladder()
    {
        var none = new CrewDecisionUsage(CrewDecisionMode.None, [], [])
        {
            Declared = true,
            OtherUses = [new CrewSystemOneUse(CrewSystemOneUseKind.RouteAuto, "analyst")],
            NamedProfiles = [new CrewNamedProfile("writer", "guarded")],
        };
        var ladder = On with { RouteLadder = ["default", "strong"] };

        foreach (var (status, known) in new (DecisionStatus?, bool)[] { (ladder, true), (On, true), (NoProvider, true), (ladder, false), (null, false) })
        {
            var refused = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(none, status, known, noDecision: false));
            Assert.Equal(DecisionPreflightKind.ProfileRefusedByMode, refused.Kind);
            Assert.Equal(["analyst"], refused.RoutedHolders);
            Assert.Empty(refused.DecisionProfileHolders);
            Assert.True(refused.BlocksLaunch);
            Assert.False(refused.BlocksValidate);
            Assert.False(refused.OnStandInSettings);
        }

        // A decision profile beside it is named too once the settings define it.
        var both = Assert.IsType<DecisionPreflightFinding>(
            DecisionPreflight.Check(none, ladder with { DecisionProfiles = ["guarded"] }, settingsKnown: true, noDecision: false));
        Assert.Equal(["analyst"], both.RoutedHolders);
        Assert.Equal(["writer"], both.DecisionProfileHolders);

        // A decision profile alone is a matter of the settings: the machine's file only warns.
        var named = none with { OtherUses = [] };
        var machine = Assert.IsType<DecisionPreflightFinding>(
            DecisionPreflight.Check(named, On with { DecisionProfiles = ["guarded"] }, settingsKnown: false, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ProfileRefusedByMode, machine.Kind);
        Assert.False(machine.BlocksLaunch);
        Assert.True(machine.OnStandInSettings);
    }

    /// <summary>
    /// A fail-closed decision profile with a guard stage blocks every call once the circuit is off: the
    /// crew that names it needs the models, whatever turns them off. A check loads the crew.
    /// </summary>
    [Fact]
    public void A_fail_closed_guarded_profile_stops_the_launch_once_the_circuit_is_off()
    {
        var names = new CrewDecisionUsage(CrewDecisionMode.None, [], [])
        {
            NamedProfiles = [new CrewNamedProfile("analyst", "Guarded"), new CrewNamedProfile("writer", "strong")],
        };
        DecisionStatus Guarded(DecisionStatus status) => status with { DecisionProfiles = ["guarded", "soft"], BlockingProfiles = ["guarded"] };

        Assert.Null(DecisionPreflight.Check(names, Guarded(On), settingsKnown: true, noDecision: false));

        foreach (var (status, noDecision, reason) in new[]
        {
            (Guarded(NoProvider), false, DecisionOffReason.NoProvider),
            (Guarded(SwitchedOff), false, DecisionOffReason.SettingsSwitch),
            (Guarded(On), true, DecisionOffReason.RunSwitch),
            // The engine turns the circuit off for a missing key too (no-key, Orkeon #451).
            (Guarded(On with { MissingKeyVariable = "LIQUID_API_KEY" }), false, DecisionOffReason.NoKey),
            (Guarded(On with { MissingKeyVariableSetting = "Orkeon:Liquid:ApiKeyEnvVar" }), false, DecisionOffReason.NoKey),
        })
        {
            var blocked = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(names, status, settingsKnown: true, noDecision));
            Assert.Equal(DecisionPreflightKind.ProfileBlockedWithoutModel, blocked.Kind);
            Assert.Equal(["analyst"], blocked.BlockedProfileHolders);
            Assert.Equal([reason], blocked.Reasons);
            Assert.True(blocked.BlocksLaunch);
            Assert.False(blocked.BlocksValidate);
            Assert.Equal(DecisionPreflightSeverity.BlocksLaunch, blocked.Severity);
        }

        // The profile is one of the machine's file, which only stands in: a warning, the run's switch included.
        foreach (var (status, noDecision) in new[]
        {
            (Guarded(NoProvider), false), (Guarded(On), true), (Guarded(On with { MissingKeyVariable = "LIQUID_API_KEY" }), false),
        })
        {
            var warned = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(names, status, settingsKnown: false, noDecision));
            Assert.Equal(DecisionPreflightKind.ProfileBlockedWithoutModel, warned.Kind);
            Assert.False(warned.BlocksLaunch);
            Assert.Equal(DecisionPreflightSeverity.Warning, warned.Severity);
            Assert.True(warned.OnStandInSettings);
        }

        // A decision profile that does not block has its stages skipped: information, as before.
        var soft = names with { NamedProfiles = [new CrewNamedProfile("analyst", "soft")] };
        Assert.Equal(
            DecisionPreflightKind.OptionalWithoutModel,
            DecisionPreflight.Check(soft, Guarded(NoProvider), settingsKnown: true, noDecision: false)?.Kind);
    }

    /// <summary>
    /// One finding is returned, the heaviest: a warning never hides what blocks the launch, nor what
    /// blocks the launch what blocks a check too.
    /// </summary>
    [Fact]
    public void A_finding_that_blocks_is_never_hidden_by_one_that_does_not()
    {
        var routedAndRequired = DeclaredRequired with
        {
            OtherUses = [new CrewSystemOneUse(CrewSystemOneUseKind.RouteAuto, "analyst")],
        };
        var unknownRung = NoProvider with { RouteLadder = ["default", "strong"], UnknownRungs = ["strong"] };

        // The machine's ladder only warns; the run's own switch on a required crew blocks.
        var required = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(routedAndRequired, unknownRung, settingsKnown: false, noDecision: true));
        Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, required.Kind);
        Assert.True(required.BlocksLaunch);
        Assert.False(required.OnStandInSettings);

        // The rung offered by the launch, the crew is still refused for the circuit of its file.
        Assert.Equal(
            DecisionPreflightKind.RequiredWithoutModel,
            DecisionPreflight.Check(routedAndRequired, unknownRung, settingsKnown: true, noDecision: false, offeredProfiles: ["strong"])?.Kind);

        // Both block on the file the run reads: the load (the ladder) comes before the run's entry.
        Assert.Equal(
            DecisionPreflightKind.RouteWithoutLadder,
            DecisionPreflight.Check(routedAndRequired, unknownRung, settingsKnown: true, noDecision: false)?.Kind);

        // A refused provider of the machine's file warns; the run's switch on a required crew blocks.
        Assert.Equal(
            DecisionPreflightKind.RequiredWithoutModel,
            DecisionPreflight.Check(DeclaredRequired, Refused, settingsKnown: false, noDecision: true)?.Kind);

        // A refused provider of the file the run reads blocks a check too: nothing comes before it.
        var refused = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(routedAndRequired, Refused, settingsKnown: true, noDecision: true));
        Assert.Equal(DecisionPreflightKind.ProviderRefused, refused.Kind);
        Assert.Equal(DecisionPreflightSeverity.BlocksValidate, refused.Severity);

        // A warning comes before information: the key that is missing, the tool withheld.
        var lenient = NoProvider with { StrictTools = false };
        var withheld = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, lenient, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ToolWithheld, withheld.Kind);
        Assert.Equal(DecisionPreflightSeverity.Warning, withheld.Severity);
        Assert.Equal(
            DecisionPreflightSeverity.Information,
            DecisionPreflight.Check(Optional, NoProvider, settingsKnown: true, noDecision: false)?.Severity);
    }

    /// <summary>
    /// What is read on the machine's settings while the CLI picks the file is told apart from what holds
    /// whatever the file: the run's own switch.
    /// </summary>
    [Fact]
    public void A_finding_read_on_settings_that_stand_in_is_told_apart_from_a_fact()
    {
        var lenient = NoProvider with { StrictTools = false };

        Assert.True(DecisionPreflight.Check(Required, lenient, settingsKnown: false, noDecision: false)!.OnStandInSettings);
        Assert.False(DecisionPreflight.Check(Required, lenient, settingsKnown: true, noDecision: false)!.OnStandInSettings);
        Assert.True(DecisionPreflight.Check(Optional, NoProvider, settingsKnown: false, noDecision: false)!.OnStandInSettings);
        Assert.True(DecisionPreflight.Check(Optional, NoProvider, settingsKnown: false, noDecision: true)!.OnStandInSettings);
        // The run's switch alone: a fact, whatever file the CLI picks.
        Assert.False(DecisionPreflight.Check(Optional, On, settingsKnown: false, noDecision: true)!.OnStandInSettings);
        Assert.False(DecisionPreflight.Check(Optional, null, settingsKnown: false, noDecision: true)!.OnStandInSettings);
    }

    [Fact]
    public void The_runs_own_switch_is_refused_by_a_fail_closed_llm_judge_a_check_included()
    {
        var judged = On with { ClosedJudge = true };

        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Optional, judged, settingsKnown: true, noDecision: true));
        Assert.Equal(DecisionPreflightKind.ProviderRefused, finding.Kind);
        Assert.Contains("--no-decision", finding.Status?.Refusal, StringComparison.Ordinal);
        Assert.True(finding.BlocksLaunch);
        Assert.True(finding.BlocksValidate);

        // With the models on, the judge judges: nothing to say.
        Assert.Null(DecisionPreflight.Check(Optional, judged, settingsKnown: true, noDecision: false));
    }

    /// <summary>
    /// A fail-closed LLM judge refuses the start when the provider in use has no key (the circuit off,
    /// <c>no-key</c>), in the engine's sentence, whether the key is awaited in the provider's own variable
    /// or in one its setting names; a check included. The key is said off all the same.
    /// </summary>
    [Fact]
    public void A_fail_closed_llm_judge_refuses_a_provider_without_its_key_a_check_included()
    {
        static DecisionStatus Judged(string liquid) => AppSettingsDocument.Parse(
            $$"""{ "Orkeon": { "Liquid": {{liquid}}, "Guardian": { "Llm": { "Enabled": true } } } }""").Decision.Resolve(_ => null);

        var own = Judged("""{ "Model": "m" }""");
        var named = Judged("""{ "Model": "m", "ApiKeyEnvVar": "STUDIO_UNSET_KEY" }""");

        Assert.Equal(DecisionRefusalStage.Judge, own.RefusalStage);
        Assert.False(own.CircuitOn);
        Assert.Contains("the decision circuit is off: no Liquid API key: set the environment variable LIQUID_API_KEY, or Orkeon:Liquid:ApiKey", own.Refusal, StringComparison.Ordinal);
        Assert.Contains("no Liquid API key: set the variable named by Orkeon:Liquid:ApiKeyEnvVar, or Orkeon:Liquid:ApiKey", named.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("STUDIO_UNSET_KEY", named.Refusal, StringComparison.Ordinal);

        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(DeclaredRequired, own, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ProviderRefused, finding.Kind);
        Assert.True(finding.BlocksLaunch);
        Assert.True(finding.BlocksValidate);
        // A fail-open judge lets the run go without the models: the key is said as for any crew that needs them.
        var open = AppSettingsDocument.Parse("""{ "Orkeon": { "Liquid": { "Model": "m" }, "Guardian": { "Llm": { "Enabled": true, "FailMode": "Open" } } } }""").Decision.Resolve(_ => null);
        Assert.Null(open.Refusal);
        Assert.Equal([DecisionOffReason.NoKey], DecisionPreflight.Check(DeclaredRequired, open, settingsKnown: true, noDecision: false)?.Reasons);
    }

    [Fact]
    public void Every_reason_the_run_goes_without_the_model_is_said_in_the_order_it_is_best_fixed()
    {
        var finding = DecisionPreflight.Check(Unanswered, NoProvider with { Enabled = false }, settingsKnown: true, noDecision: true);

        Assert.Equal(
            [DecisionOffReason.NoProvider, DecisionOffReason.SettingsSwitch, DecisionOffReason.RunSwitch],
            finding?.Reasons);
        Assert.Equal(DecisionOffReason.NoProvider, finding?.Reasons[0]);
    }

    [Fact]
    public void A_provider_without_its_key_is_warned_for_a_crew_that_can_run_without_it()
    {
        var keyless = On with { MissingKeyVariable = "LIQUID_API_KEY" };

        var optional = DecisionPreflight.Check(Optional, keyless, settingsKnown: true, noDecision: false);
        Assert.Equal(DecisionPreflightKind.ProviderKeyMissing, optional?.Kind);
        Assert.False(optional!.BlocksLaunch);
        Assert.Equal(DecisionPreflightKind.ProviderKeyMissing, DecisionPreflight.Check(DeclaredOptional, keyless, settingsKnown: true, noDecision: false)?.Kind);
        // The decision tasks go without the model: the key is not asked for.
        Assert.Equal(DecisionPreflightKind.OptionalWithoutModel, DecisionPreflight.Check(Optional, keyless, settingsKnown: true, noDecision: true)?.Kind);
        Assert.Null(DecisionPreflight.Check(CrewDecisionUsage.None, keyless, settingsKnown: true, noDecision: false));
        // A variable named by its setting is warned of all the same.
        Assert.Equal(
            DecisionPreflightKind.ProviderKeyMissing,
            DecisionPreflight.Check(Optional, On with { MissingKeyVariableSetting = "Orkeon:Liquid:ApiKeyEnvVar" }, settingsKnown: true, noDecision: false)?.Kind);
    }

    /// <summary>
    /// The engine turns the circuit off when the provider's key is missing (cause <c>no-key</c>, Orkeon #451):
    /// a crew that needs the decision models is refused as for the other off causes, « Launch now » blocked
    /// (exit code 69 for a crew that states <c>required</c>) on the file the run reads, warned of on the
    /// machine's while the CLI picks the file; the reason says the key.
    /// </summary>
    [Fact]
    public void A_provider_without_its_key_refuses_a_crew_that_needs_the_model()
    {
        foreach (var keyless in new[] { On with { MissingKeyVariable = "LIQUID_API_KEY" }, On with { MissingKeyVariableSetting = "Orkeon:Liquid:ApiKeyEnvVar" } })
        {
            var declared = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(DeclaredRequired, keyless, settingsKnown: true, noDecision: false));
            Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, declared.Kind);
            Assert.Equal(DecisionPreflightSeverity.BlocksLaunch, declared.Severity);
            Assert.True(declared.BlocksLaunch);
            Assert.False(declared.BlocksValidate);
            Assert.True(declared.RefusedByMode);
            Assert.Equal([DecisionOffReason.NoKey], declared.Reasons);

            // A crew that states no mode and holds the tool, withheld under strict tools.
            var holders = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, keyless, settingsKnown: true, noDecision: false));
            Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, holders.Kind);
            Assert.False(holders.RefusedByMode);
            // Lenient tools withhold the tool and go on, the circuit off for the key as for any other cause.
            var withheld = Assert.IsType<DecisionPreflightFinding>(
                DecisionPreflight.Check(Required, keyless with { StrictTools = false }, settingsKnown: true, noDecision: false));
            Assert.Equal(DecisionPreflightKind.ToolWithheld, withheld.Kind);
            Assert.Equal([DecisionOffReason.NoKey], withheld.Reasons);
            Assert.False(withheld.BlocksLaunch);

            // On the machine's settings, standing in for the file the CLI picks: said, never blocking.
            var standIn = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(DeclaredRequired, keyless, settingsKnown: false, noDecision: false));
            Assert.Equal(DecisionPreflightKind.RequiredUnknownSettings, standIn.Kind);
            Assert.False(standIn.BlocksLaunch);
            Assert.Equal([DecisionOffReason.NoKey], standIn.Reasons);

            // The run's own switch says both, the key first.
            Assert.Equal(
                [DecisionOffReason.NoKey, DecisionOffReason.RunSwitch],
                DecisionPreflight.Check(DeclaredRequired, keyless, settingsKnown: true, noDecision: true)?.Reasons);
        }

        // With the key in place, nothing to say.
        Assert.Null(DecisionPreflight.Check(DeclaredRequired, On, settingsKnown: true, noDecision: false));
    }

    public static TheoryData<DecisionStatus, bool> RunsWithoutTheModel() => new()
    {
        { NoProvider, false },
        { SwitchedOff, false },
        { On, true },
    };

    [Fact]
    public void A_crew_that_needs_the_model_runs_when_the_circuit_is_on()
    {
        Assert.Null(DecisionPreflight.Check(Required, On, settingsKnown: true, noDecision: false));
        Assert.Null(DecisionPreflight.Check(Optional, On, settingsKnown: true, noDecision: false));
    }

    [Fact]
    public void Unknown_settings_are_said_for_a_crew_that_needs_the_model_and_never_block()
    {
        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, null, settingsKnown: true, noDecision: false));

        Assert.Equal(DecisionPreflightKind.RequiredUnknownSettings, finding.Kind);
        Assert.False(finding.BlocksLaunch);
        Assert.False(finding.SettingsKnown);
        Assert.Null(DecisionPreflight.Check(Optional, null, settingsKnown: true, noDecision: false));
    }

    [Fact]
    public void The_machines_settings_warn_and_never_block_while_the_cli_picks_the_file()
    {
        var off = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, NoProvider, settingsKnown: false, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RequiredUnknownSettings, off.Kind);
        Assert.Equal(DecisionOffReason.NoProvider, off.Reasons[0]);
        Assert.False(off.BlocksLaunch);

        var refused = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, Refused, settingsKnown: false, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ProviderRefused, refused.Kind);
        Assert.False(refused.BlocksLaunch);
        Assert.False(refused.BlocksValidate);

        Assert.Null(DecisionPreflight.Check(Required, On, settingsKnown: false, noDecision: false));
        Assert.Equal(
            DecisionPreflightKind.OptionalWithoutModel,
            DecisionPreflight.Check(Optional, SwitchedOff, settingsKnown: false, noDecision: false)?.Kind);
    }

    [Fact]
    public void The_reason_the_run_goes_without_the_model_is_said()
    {
        Assert.Equal(DecisionOffReason.RunSwitch, DecisionPreflight.Check(Unanswered, On, settingsKnown: true, noDecision: true)?.Reasons[0]);
        Assert.Equal(DecisionOffReason.SettingsSwitch, DecisionPreflight.Check(Unanswered, SwitchedOff, settingsKnown: true, noDecision: false)?.Reasons[0]);
        Assert.Equal(DecisionOffReason.NoProvider, DecisionPreflight.Check(Required, NoProvider, settingsKnown: true, noDecision: false)?.Reasons[0]);
    }

    [Theory]
    [MemberData(nameof(RunsWithoutTheModel))]
    public void A_crew_that_runs_either_way_is_told_it_goes_on_its_fallbacks(DecisionStatus status, bool noDecision)
    {
        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Optional, status, settingsKnown: true, noDecision));

        Assert.Equal(DecisionPreflightKind.OptionalWithoutModel, finding.Kind);
        Assert.False(finding.BlocksLaunch);
        Assert.False(finding.BlocksValidate);
    }

    [Fact]
    public void The_run_switch_off_is_enough_without_known_settings()
    {
        Assert.Equal(
            DecisionPreflightKind.RequiredWithoutModel,
            DecisionPreflight.Check(Unanswered, null, settingsKnown: true, noDecision: true)?.Kind);
        Assert.Equal(
            DecisionPreflightKind.OptionalWithoutModel,
            DecisionPreflight.Check(Optional, null, settingsKnown: true, noDecision: true)?.Kind);
    }

    /// <summary>
    /// Only the settings tell that a profile a crew names is a decision profile: its stages follow the
    /// circuit, so the crew asks System One after all, and a crew that states none is refused at load.
    /// </summary>
    [Fact]
    public void A_profile_the_settings_define_as_a_decision_profile_is_a_use_of_System_One()
    {
        var names = new CrewDecisionUsage(CrewDecisionMode.None, [], [])
        {
            NamedProfiles = [new CrewNamedProfile("analyst", "Guarded"), new CrewNamedProfile("writer", "strong")],
        };
        var guarded = On with { DecisionProfiles = ["guarded"] };

        var effective = DecisionPreflight.Effective(names, guarded);
        Assert.Equal(CrewDecisionMode.Optional, effective.Mode);
        Assert.Equal(new CrewSystemOneUse(CrewSystemOneUseKind.DecisionProfile, "analyst"), Assert.Single(effective.OtherUses));
        Assert.Same(names, DecisionPreflight.Effective(names, On));
        Assert.Same(names, DecisionPreflight.Effective(names, null));

        Assert.Null(DecisionPreflight.Check(names, guarded, settingsKnown: true, noDecision: false));
        var without = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(names, guarded, settingsKnown: true, noDecision: true));
        Assert.Equal(DecisionPreflightKind.OptionalWithoutModel, without.Kind);
        Assert.Equal(["analyst"], without.DecisionProfileHolders);

        var none = names with { Declared = true };
        var refused = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(none, guarded, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ProfileRefusedByMode, refused.Kind);
        Assert.True(refused.BlocksLaunch);
        Assert.False(refused.BlocksValidate);
        Assert.False(DecisionPreflight.Check(none, guarded, settingsKnown: false, noDecision: false)!.BlocksLaunch);
        // A crew that states none and names no decision profile is told nothing, the provider's key included.
        Assert.Null(DecisionPreflight.Check(none, On, settingsKnown: true, noDecision: true));
        Assert.Null(DecisionPreflight.Check(none, On with { MissingKeyVariable = "LIQUID_API_KEY" }, settingsKnown: true, noDecision: false));
    }

    /// <summary>
    /// A model profile the launch lays in the run's environment is a profile beside those of the file: a
    /// decision profile of the same name, in whatever letter case, refuses the start as two of the file
    /// do (the engine: « Two LLM profiles are named 'x'. »), a check included and whatever the crew.
    /// Blocked on the file the run reads, warned of on the machine's; a refusal the host meets earlier
    /// stays the one said, and a fail-closed judge, met later, gives way.
    /// </summary>
    [Fact]
    public void A_decision_profile_named_as_a_profile_the_launch_lays_refuses_the_start()
    {
        var plain = new CrewDecisionUsage(CrewDecisionMode.None, [], []);
        var guarded = On with { DecisionProfiles = ["guarded"] };

        var refused = Assert.IsType<DecisionPreflightFinding>(
            DecisionPreflight.Check(plain, guarded, settingsKnown: true, noDecision: false, offeredProfiles: ["fast", "Guarded"]));
        Assert.Equal(DecisionPreflightKind.ProviderRefused, refused.Kind);
        Assert.Equal("Two LLM profiles are named 'guarded'.", refused.Status?.Refusal);
        Assert.Equal(DecisionRefusalStage.Profiles, refused.Status?.RefusalStage);
        Assert.True(refused.BlocksLaunch);
        Assert.True(refused.BlocksValidate);

        var machine = Assert.IsType<DecisionPreflightFinding>(
            DecisionPreflight.Check(plain, guarded, settingsKnown: false, noDecision: false, offeredProfiles: ["guarded"]));
        Assert.Equal(DecisionPreflightKind.ProviderRefused, machine.Kind);
        Assert.Equal(DecisionPreflightSeverity.Warning, machine.Severity);

        // Another name, or nothing laid: nothing to say.
        Assert.Null(DecisionPreflight.Check(plain, guarded, settingsKnown: true, noDecision: false, offeredProfiles: ["fast"]));
        Assert.Null(DecisionPreflight.Check(plain, guarded, settingsKnown: true, noDecision: false));

        var earlier = guarded with { Refusal = "Both TypeSafe and Liquid are configured", RefusalStage = DecisionRefusalStage.Composition };
        Assert.Equal(
            "Both TypeSafe and Liquid are configured",
            DecisionPreflight.Check(plain, earlier, settingsKnown: true, noDecision: false, offeredProfiles: ["guarded"])?.Status?.Refusal);

        var judge = guarded with { Refusal = "the judge", RefusalStage = DecisionRefusalStage.Judge };
        Assert.Equal(
            "Two LLM profiles are named 'guarded'.",
            DecisionPreflight.Check(plain, judge, settingsKnown: true, noDecision: false, offeredProfiles: ["guarded"])?.Status?.Refusal);
    }
}
