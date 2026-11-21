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
}
