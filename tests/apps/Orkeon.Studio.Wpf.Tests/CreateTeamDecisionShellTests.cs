using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Services;
using Orkeon.Studio.Wpf.ViewModels.Shell;
using Orkeon.Studio.Wpf.ViewModels.Teams;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The creation wizard inside the window: what its mode list says of the decision models on this
/// machine follows the settings while the wizard is open, through the two hooks that judge the launch
/// screens again.
/// </summary>
public sealed class CreateTeamDecisionShellTests
{
    private static (MainWindowViewModel Window, List<string?> Raised) Build()
    {
        var window = new MainWindowViewModel(new StudioServices
            {
                SettingsStore = new FakeAppSettingsStore(),
                Directories = new FakeDirectoryProbe(),
                TargetProbe = new FakeTargetProbe(),
                Picker = new FakePathPicker(),
                ProcessRunner = new OrkeonProcessRunner(
                    new FakeProcessLauncher(),
                    new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
                HistoryStore = new FakeLaunchHistoryStore(),
                KeyStore = new FakeApiKeyStore(),
            },
            globalPathOverride: "/home/user/.config/Orkeon/appsettings.json");
        // A provider is configured, and the settings switch the decision models off.
        window.Config.SetDocument(AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"Enabled":false},"Liquid":{"ApiKey":"in-file"}}}"""));
        var wizard = window.CreateTeam;
        wizard.SelectedCrewDecision = wizard.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        var raised = new List<string?>();
        wizard.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return (window, raised);
    }

    [Fact]
    public void The_decision_models_switched_on_in_the_settings_clear_the_wizards_notice()
    {
        var (window, raised) = Build();
        Assert.Contains("its trial will be refused", window.CreateTeam.CrewDecisionNotice, StringComparison.Ordinal);

        window.Config.Decision.Enabled = true;

        Assert.Contains(nameof(CreateTeamViewModel.CrewDecisionNotice), raised);
        Assert.Equal("", window.CreateTeam.CrewDecisionNotice);
    }

    [Fact]
    public void The_setting_views_refreshed_read_the_wizards_notice_again()
    {
        var (window, raised) = Build();

        // The model settings changed: every screen that reads the settings reads them again.
        window.Settings.Profiles.CommitEdit(
            new ModelProfile { Name = "Local", Provider = "Ollama", Model = "qwen2.5:14b", BaseUrl = "http://localhost:11434/v1" },
            previousName: null);

        Assert.Contains(nameof(CreateTeamViewModel.CrewDecisionNotice), raised);
    }
}
