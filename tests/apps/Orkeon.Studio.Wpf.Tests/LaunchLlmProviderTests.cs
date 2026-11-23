using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Launch;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The <c>Provider</c> of the <c>Llm</c> section and of its profiles in the settings file the run reads
/// (Orkeon #457): one the host's start refuses stops « Launch now » and the check, the engine's sentence
/// said with the file to fix; while the CLI picks the file itself nothing is judged here.
/// </summary>
public sealed class LaunchLlmProviderTests : IDisposable
{
    private const string PinnedSettings = "/etc/triage/appsettings.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkeon-llm-provider-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string NewTeam()
    {
        var team = Path.Combine(_root, "triage");
        Directory.CreateDirectory(Path.Combine(team, "agents"));
        Directory.CreateDirectory(Path.Combine(team, "tasks"));
        TeamCatalog.SaveMetadata(team, new StudioTeamMetadata { Name = "triage" });
        File.WriteAllText(Path.Combine(team, "crew.yaml"), "name: triage\ngoal: triage\nprocess: sequential\n");
        File.WriteAllText(Path.Combine(team, "agents", "writer.yaml"), "role: Writer\ngoal: write\n");
        File.WriteAllText(Path.Combine(team, "tasks", "draft.yaml"), "description: draft\nexpected_output: a draft\nagent: writer\n");
        return team;
    }

    /// <summary>The launcher on a team, the run reading <paramref name="pinnedJson"/> when given.</summary>
    private LaunchTabViewModel Launcher(string? pinnedJson)
    {
        var team = NewTeam();
        var settings = new FakeAppSettingsStore();
        var tab = new LaunchTabViewModel(new LaunchTabDependencies
        {
            ProcessRunner = new OrkeonProcessRunner(
                new FakeProcessLauncher(), new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
            TargetProbe = new FakeTargetProbe().WithDirectory(team).WithDirectory(Path.Combine(team, "agents")),
            Directories = new FakeDirectoryProbe([team]),
            HistoryStore = new FakeLaunchHistoryStore(),
            SettingsStore = settings,
            MachineDecisionStatus = () => null,
            KeyStore = new FakeApiKeyStore(),
        });
        tab.Target.Select(team);
        if (pinnedJson is not null)
        {
            settings.Files[PinnedSettings] = pinnedJson;
            tab.Options.SettingsPath = PinnedSettings;
            tab.Options.SettingsMode = SettingsSelectionMode.ExplicitPath;
        }

        return tab;
    }

    [Theory]
    [InlineData("""{"Llm":{"Provider":"openai","Model":"gpt-4o"}}""", "Llm:Provider names a vendor of the catalogue")]
    [InlineData("""{"llm":{"Provider":"mock","Model":"mock","Responses":"/r.jsonl","Profiles":{"p":{"Provider":"mock","Model":"m"}}}}""", "Llm:Profiles:p:Responses: the mock provider needs")]
    public async Task A_provider_the_start_refuses_stops_the_launch_and_the_check(string pinnedJson, string sentence)
    {
        var tab = Launcher(pinnedJson);

        Assert.True(tab.IsBlockedByLlmProvider);
        Assert.Contains(sentence, tab.LlmProviderMessage, StringComparison.Ordinal);
        Assert.Contains(PinnedSettings, tab.LlmProviderMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        Assert.False(tab.ValidateCommand.CanExecute(null));
        Assert.Equal(tab.LlmProviderMessage, tab.LaunchBlockedReason);

        Assert.Null(await tab.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(tab.LlmProviderMessage, tab.StatusMessage);
    }

    [Theory]
    [InlineData("""{"Llm":{"Provider":"mock","Model":"mock","Responses":"/r.jsonl"}}""")]
    [InlineData("""{"Llm":{"Model":"gpt-4o"}}""")]
    public void A_provider_the_start_accepts_blocks_nothing(string pinnedJson)
    {
        var tab = Launcher(pinnedJson);

        Assert.False(tab.IsBlockedByLlmProvider);
        Assert.Equal("", tab.LlmProviderMessage);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void Nothing_is_judged_while_the_cli_picks_the_settings_file()
    {
        var tab = Launcher(pinnedJson: null);

        Assert.False(tab.IsBlockedByLlmProvider);
        Assert.True(tab.RunCommand.CanExecute(null));
    }
}
