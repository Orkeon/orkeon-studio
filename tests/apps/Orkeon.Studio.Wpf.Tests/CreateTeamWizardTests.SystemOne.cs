using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Forge;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// System One in the creation wizard: what the team to compose asks of the decision models is chosen
/// on the first step and passed to a new creation, and the assistant can compose without asking them.
/// </summary>
public partial class CreateTeamWizardTests
{
    [Fact]
    public async Task A_new_creation_passes_neither_option_until_the_user_asks()
    {
        var (vm, _, _) = Build();
        FillStepOne(vm);
        Assert.Equal(ForgeCrewDecisions.None, vm.SelectedCrewDecision.Mode);
        Assert.Equal(["none", "optional", "required"], vm.CrewDecisionChoices.Select(choice => choice.Mode));

        await Compose(vm);

        Assert.DoesNotContain("--crew-decision", vm.EngineCommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-decision", vm.EngineCommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mode_chosen_and_the_assistants_switch_reach_the_engine_of_a_new_creation()
    {
        var (vm, _, _) = Build();
        FillStepOne(vm);
        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
        vm.DesignWithoutSystemOne = true;

        await Compose(vm);

        Assert.Contains("--crew-decision optional", vm.EngineCommandLine, StringComparison.Ordinal);
        Assert.Contains("--no-decision", vm.EngineCommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mode_the_session_was_created_with_is_the_one_the_list_shows()
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange(
        [
            Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","crewDecision":"required"}"""),
            Out("""{"v":2,"seq":2,"ts":"t","kind":"session.finished","status":"paused","exitCode":0}"""),
        ]);
        FillStepOne(vm);

        await Compose(vm);

        Assert.Equal(ForgeCrewDecisions.Required, vm.SelectedCrewDecision.Mode);
    }

    /// <summary>
    /// The mode and the switch belong to a creation: what a session stated, or the user chose, does not
    /// become the next team's, and a session's mode cannot be picked again.
    /// </summary>
    [Fact]
    public async Task Starting_over_gives_the_next_creation_the_default_mode_and_no_switch()
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange(
        [
            Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","crewDecision":"required"}"""),
            Out("""{"v":2,"seq":2,"ts":"t","kind":"session.finished","status":"paused","exitCode":0}"""),
        ]);
        FillStepOne(vm);
        Assert.True(vm.CanChooseCrewDecision);
        vm.DesignWithoutSystemOne = true;
        await Compose(vm);
        Assert.Equal(ForgeCrewDecisions.Required, vm.SelectedCrewDecision.Mode);
        Assert.False(vm.CanChooseCrewDecision);

        vm.RestartCommand.Execute(null);

        Assert.Equal(ForgeCrewDecisions.None, vm.SelectedCrewDecision.Mode);
        Assert.False(vm.DesignWithoutSystemOne);
        Assert.True(vm.CanChooseCrewDecision);
    }

    /// <summary>
    /// A mode that needs the decision models, chosen on a machine where they are off: said under the
    /// list before anything is composed, since the engine would compose the team and refuse its trial.
    /// </summary>
    [Fact]
    public void A_mode_the_machine_cannot_serve_is_said_before_anything_is_composed()
    {
        var off = AppSettingsDocument.CreateEmpty().Decision.Resolve(_ => null);
        var (vm, _, _) = Build(machineDecision: () => off);
        Assert.Equal("", vm.CrewDecisionNotice);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Contains("its trial will be refused", vm.CrewDecisionNotice, StringComparison.Ordinal);
        Assert.Contains("no decision provider is configured", vm.CrewDecisionNotice, StringComparison.Ordinal);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
        Assert.Contains("the trial will run without them only", vm.CrewDecisionNotice, StringComparison.Ordinal);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.None);
        Assert.Equal("", vm.CrewDecisionNotice);

        // With the models on, or their state unknown, nothing is said.
        var on = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{"ApiKey":"in-file"}}}""").Decision.Resolve(_ => null);
        var (served, _, _) = Build(machineDecision: () => on);
        served.SelectedCrewDecision = served.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Equal("", served.CrewDecisionNotice);
        var (unknown, _, _) = Build();
        unknown.SelectedCrewDecision = unknown.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Equal("", unknown.CrewDecisionNotice);
    }

    /// <summary>
    /// A warning of the assistant reaches the status line as the engine words it, and a trial in two
    /// passes says which pass is under way, which pass each line belongs to, and each pass's verdict.
    /// </summary>
    [Fact]
    public async Task A_two_pass_trial_shows_its_passes_and_the_assistants_warning()
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange(
        [
            Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","crewDecision":"optional"}"""),
            Out("""{"v":2,"seq":2,"ts":"t","kind":"warning","code":"FORGE-DECISION-PASS-SKIPPED","message":"The pass with System One was skipped."}"""),
            Out("""{"v":2,"seq":3,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":1,"circuit":"on"}"""),
            Out("""{"v":2,"seq":4,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":true,"durationMs":1200,"deliverableMissing":true,"circuit":"on"}"""),
            Out("""{"v":2,"seq":5,"ts":"t","kind":"run.finished","run":1,"circuit":"on"}"""),
            Out("""{"v":2,"seq":6,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":2,"circuit":"off"}"""),
            Out("""{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":true,"durationMs":900,"circuit":"off"}"""),
        ]);
        FillStepOne(vm);

        await Compose(vm);

        Assert.Equal("FORGE-DECISION-PASS-SKIPPED: The pass with System One was skipped.", vm.StatusMessage);
        Assert.Equal(2, vm.Activity.Count);
        // The pass without the models wrote its file: that says nothing of the pass with them.
        Assert.True(vm.Activity[0].FileMissing);
        Assert.Contains("with the decision models", vm.Activity[0].Detail, StringComparison.Ordinal);
        Assert.Contains("without the decision models", vm.Activity[1].Detail, StringComparison.Ordinal);
    }
}
