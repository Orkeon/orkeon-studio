using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Tests.Launch;

/// <summary>
/// What a launch is told of the decision models, as the engine runs a crew: a refused provider stops
/// every start; <c>system_one_decide</c> needs a provider whatever the circuit; a decision task with no
/// answer once the circuit is off needs the circuit on; a crew that runs either way is told it goes on
/// its fallbacks.
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
    public void A_lenient_runner_drops_the_tool_without_a_provider_a_warning_that_never_blocks()
    {
        var lenient = NoProvider with { StrictTools = false };

        var dropped = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, lenient, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ToolDroppedWithoutProvider, dropped.Kind);
        Assert.Equal([DecisionOffReason.NoProvider], dropped.Reasons);
        Assert.False(dropped.BlocksLaunch);
        Assert.False(dropped.BlocksValidate);
        Assert.False(dropped.RefusesToolAtLoad);

        // The machine's file stands in for the run's: a lenient one warns the same, never of unknown settings.
        var unknown = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, lenient, settingsKnown: false, noDecision: false));
        Assert.Equal(DecisionPreflightKind.ToolDroppedWithoutProvider, unknown.Kind);

        // A decision task with no answer still needs the circuit: that block stays, the tool no longer in it.
        var both = new CrewDecisionUsage(CrewDecisionMode.Required, Unanswered.DecisionTasks, ["analyst"]);
        var blocked = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(both, lenient, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, blocked.Kind);
        Assert.True(blocked.BlocksLaunch);
        Assert.False(blocked.RefusesToolAtLoad);
    }

    [Fact]
    public void A_strict_runner_refuses_the_holder_at_load_which_a_check_reports()
    {
        var finding = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, NoProvider, settingsKnown: true, noDecision: false));

        Assert.True(finding.RefusesToolAtLoad);
        Assert.False(finding.BlocksValidate);
        Assert.False(Assert.IsType<DecisionPreflightFinding>(
            DecisionPreflight.Check(Unanswered, NoProvider, settingsKnown: true, noDecision: false)).RefusesToolAtLoad);
    }

    [Fact]
    public void The_tool_is_blocked_only_without_a_provider_and_warned_when_the_circuit_is_off()
    {
        var blocked = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, NoProvider, settingsKnown: true, noDecision: false));
        Assert.Equal(DecisionPreflightKind.RequiredWithoutModel, blocked.Kind);
        Assert.Equal([DecisionOffReason.NoProvider], blocked.Reasons);

        // The engine registers system_one_decide whenever a provider resolves: the circuit does not stop it.
        foreach (var (status, noDecision) in new[] { (SwitchedOff, false), (On, true) })
        {
            var warned = Assert.IsType<DecisionPreflightFinding>(DecisionPreflight.Check(Required, status, settingsKnown: true, noDecision));
            Assert.Equal(DecisionPreflightKind.ToolIgnoresSwitch, warned.Kind);
            Assert.False(warned.BlocksLaunch);
        }
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
