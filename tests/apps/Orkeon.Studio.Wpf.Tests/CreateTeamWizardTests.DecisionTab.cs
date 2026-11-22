using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Wpf.ViewModels.Teams;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// #22: System One in a « Decision models » tab of its own, first of step 1. The wizard still opens
/// on « Need », « Back » on it leads to the decision models, and the tab says the mode shown and
/// warns when the machine cannot serve it.
/// </summary>
public partial class CreateTeamWizardTests
{
    [Fact]
    public void Step_one_opens_on_the_need_and_back_leads_to_the_decision_models()
    {
        var (vm, _, _) = Build();

        Assert.Equal(CreateTeamViewModel.Step1NeedTab, vm.Step1TabIndex);
        Assert.Equal(0, CreateTeamViewModel.Step1DecisionTab);

        vm.Step1BackCommand.Execute(null);

        Assert.Equal(CreateTeamViewModel.Step1DecisionTab, vm.Step1TabIndex);
        Assert.False(vm.Step1BackCommand.CanExecute(null));

        // The index is clamped to the four tabs.
        vm.Step1TabIndex = -1;
        Assert.Equal(CreateTeamViewModel.Step1DecisionTab, vm.Step1TabIndex);
        vm.Step1TabIndex = 9;
        Assert.Equal(CreateTeamViewModel.Step1NotesTab, vm.Step1TabIndex);
    }

    [Fact]
    public void The_decision_tab_says_the_mode_shown()
    {
        var (vm, _, _) = Build();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("Not used", vm.DecisionTabSubtitle);
        Assert.Equal("Empty", vm.DecisionTabState);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
        Assert.Equal("When available", vm.DecisionTabSubtitle);
        Assert.Equal("Done", vm.DecisionTabState);
        Assert.Contains(nameof(CreateTeamViewModel.DecisionTabSubtitle), raised);
        Assert.Contains(nameof(CreateTeamViewModel.DecisionTabState), raised);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Equal("Always", vm.DecisionTabSubtitle);
        Assert.Equal("Done", vm.DecisionTabState);
    }

    [Fact]
    public void The_decision_tab_warns_when_the_machine_cannot_serve_the_mode()
    {
        var off = AppSettingsDocument.CreateEmpty().Decision.Resolve(_ => null);
        var (vm, _, _) = Build(machineDecision: () => off);
        Assert.Equal("Empty", vm.DecisionTabState);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);

        Assert.NotEqual("", vm.CrewDecisionNotice);
        Assert.Equal("Warning", vm.DecisionTabState);
        Assert.Equal("Always", vm.DecisionTabSubtitle);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.None);
        Assert.Equal("Empty", vm.DecisionTabState);
    }
}
