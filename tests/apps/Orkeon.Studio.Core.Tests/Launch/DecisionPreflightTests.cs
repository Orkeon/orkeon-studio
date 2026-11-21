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

        var unknownRung = On with { RouteLadder = ["default", "strong"], UnknownRungs = ["strong"] };
        Assert.Equal(DecisionPreflightKind.RouteWithoutLadder, DecisionPreflight.Check(picks, unknownRung, settingsKnown: true, noDecision: false)?.Kind);

        var machine = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(picks, On, settingsKnown: false, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RouteWithoutLadder, machine.Kind);
        Assert.False(machine.BlocksLaunch);

        // No settings read: nothing is known of the ladder, nothing is said.
        Assert.Null(DecisionPreflight.Check(picks, null, settingsKnown: false, noDecision: false));

        // Thinking on auto needs no ladder.
        var thinking = picks with { OtherUses = [new CrewSystemOneUse(CrewSystemOneUseKind.EffortAuto, "writer")] };
        Assert.Null(DecisionPreflight.Check(thinking, On, settingsKnown: true, noDecision: false));
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

    [Fact]
    public void Every_reason_the_run_goes_without_the_model_is_said_in_the_order_it_is_best_fixed()
    {
        var finding = DecisionPreflight.Check(Unanswered, NoProvider with { Enabled = false }, settingsKnown: true, noDecision: true);

        Assert.Equal(
            [DecisionOffReason.NoProvider, DecisionOffReason.SettingsSwitch, DecisionOffReason.RunSwitch],
            finding?.Reasons);
        Assert.Equal(DecisionOffReason.NoProvider, finding?.OffReason);
    }

    [Fact]
    public void A_provider_without_its_key_is_warned_for_a_crew_that_asks_it()
    {
        var keyless = On with { MissingKeyVariable = "LIQUID_API_KEY" };

        Assert.Equal(DecisionPreflightKind.ProviderKeyMissing, DecisionPreflight.Check(Optional, keyless, settingsKnown: true, noDecision: false)?.Kind);
        Assert.Equal(DecisionPreflightKind.ProviderKeyMissing, DecisionPreflight.Check(Required, keyless, settingsKnown: true, noDecision: false)?.Kind);
        Assert.False(DecisionPreflight.Check(Required, keyless, settingsKnown: true, noDecision: false)!.BlocksLaunch);
        // The decision tasks go without the model: the key is not asked for.
        Assert.Equal(DecisionPreflightKind.OptionalWithoutModel, DecisionPreflight.Check(Optional, keyless, settingsKnown: true, noDecision: true)?.Kind);
        Assert.Null(DecisionPreflight.Check(CrewDecisionUsage.None, keyless, settingsKnown: true, noDecision: false));
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
        Assert.Equal(DecisionOffReason.NoProvider, off.OffReason);
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
        Assert.Equal(DecisionOffReason.RunSwitch, DecisionPreflight.Check(Unanswered, On, settingsKnown: true, noDecision: true)?.OffReason);
        Assert.Equal(DecisionOffReason.SettingsSwitch, DecisionPreflight.Check(Unanswered, SwitchedOff, settingsKnown: true, noDecision: false)?.OffReason);
        Assert.Equal(DecisionOffReason.NoProvider, DecisionPreflight.Check(Required, NoProvider, settingsKnown: true, noDecision: false)?.OffReason);
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
}
