using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Launch;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The decision models (System One) on the Launch screen: a team that cannot run without them is
/// stopped before the engine refuses it, a team that runs either way is told which answers its
/// decision tasks fall back to, a provider without its key or a tool the switch does not govern is
/// warned of, and the run's own switch reaches the command line as <c>--no-decision</c> for a team
/// that asks the models, never for one that does not.
/// </summary>
public sealed class LaunchDecisionModelsTests : IDisposable
{
    private const string PinnedSettings = "/etc/triage/appsettings.json";
    private const string NoProvider = """{"Orkeon":{}}""";
    private const string Liquid = """{"Orkeon":{"Liquid":{}}}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkeon-decision-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static readonly string LiquidKey = DecisionProviderDescriptor.Liquid.DefaultApiKeyVariable;

    /// <summary>
    /// A promoted team: a crew directory whose writer agent drafts, then a decision task when asked —
    /// with a fallback, or with neither a fallback nor an otherwise task (<paramref name="noAnswer"/>).
    /// </summary>
    private string NewTeam(bool decisionTask = false, bool decideTool = false, bool noAnswer = false, string name = "triage")
    {
        var team = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(team, "agents"));
        Directory.CreateDirectory(Path.Combine(team, "tasks"));
        TeamCatalog.SaveMetadata(team, new StudioTeamMetadata { Name = "triage" });
        File.WriteAllText(Path.Combine(team, "crew.yaml"), "name: triage\ngoal: triage\nprocess: sequential\n");
        File.WriteAllText(
            Path.Combine(team, "agents", "writer.yaml"),
            "role: Writer\ngoal: write\n" + (decideTool ? "tools: [system_one_decide]\n" : ""));
        File.WriteAllText(Path.Combine(team, "tasks", "draft.yaml"), "description: draft\nexpected_output: a draft\nagent: writer\n");
        if (decisionTask)
        {
            File.WriteAllText(
                Path.Combine(team, "tasks", "pick.yaml"),
                "description: is it ready\nexpected_output: an answer\ndependencies: [draft]\n"
                + "decision:\n  questions:\n    go: { noul: the draft is ready }\n"
                + (noAnswer ? "" : "  fallback: { go: \"no\" }\n"));
        }

        return team;
    }

    /// <summary>
    /// The launcher on <paramref name="team"/>, the run reading <paramref name="pinnedJson"/> when given;
    /// on the <see cref="Liquid"/> file, Liquid's key is in place unless <paramref name="keys"/> says
    /// otherwise. Never on another: a key in the environment is enough for the engine to deduce Liquid.
    /// </summary>
    private static LaunchTabViewModel Launcher(
        string team, string? pinnedJson, DecisionStatus? machine = null, FakeApiKeyStore? keys = null, params string[] otherTeams)
    {
        var settings = new FakeAppSettingsStore();
        if (keys is null)
        {
            keys = new FakeApiKeyStore();
            if (pinnedJson == Liquid)
                keys.Stage(LiquidKey, "liquid-key");
        }

        var probe = new FakeTargetProbe().WithDirectory(team).WithDirectory(Path.Combine(team, "agents"));
        foreach (var other in otherTeams)
            probe.WithDirectory(other).WithDirectory(Path.Combine(other, "agents"));
        var tab = new LaunchTabViewModel(new LaunchTabDependencies
        {
            ProcessRunner = new OrkeonProcessRunner(
                new FakeProcessLauncher(), new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
            TargetProbe = probe,
            Directories = new FakeDirectoryProbe([team, .. otherTeams]),
            HistoryStore = new FakeLaunchHistoryStore(),
            SettingsStore = settings,
            MachineDecisionStatus = () => machine,
            KeyStore = keys,
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

    [Fact]
    public void A_team_that_asks_nothing_of_the_decision_models_is_told_nothing()
    {
        var tab = Launcher(NewTeam(), NoProvider);

        Assert.False(tab.UsesDecisionModels);
        Assert.False(tab.IsBlockedByDecision);
        Assert.False(tab.HasDecisionWarning);
        Assert.False(tab.HasDecisionInfo);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void A_team_that_needs_the_decision_models_does_not_start_without_a_provider()
    {
        var tab = Launcher(NewTeam(decideTool: true), NoProvider);

        Assert.True(tab.UsesDecisionModels);
        Assert.True(tab.IsBlockedByDecision);
        Assert.Contains("'writer'", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.Contains("No decision provider is configured", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        // A check stays offered: it reports the engine's unknown-tool line, and starts no agent.
        Assert.True(tab.ValidateCommand.CanExecute(null));
        // The action bar says why « Launch now » is greyed, and the team tab is amber.
        Assert.Equal(tab.DecisionMessage, tab.LaunchBlockedReason);
        Assert.Equal(tab.DecisionMessage, tab.LaunchReadinessText);
        Assert.Equal("fail", tab.LaunchReadinessTone);
        Assert.Equal("Warning", tab.TeamTabState);

        // The alert's action lands on the decision models settings.
        var opened = 0;
        tab.OpenDecisionSettingsRequested += (_, _) => opened++;
        tab.OpenDecisionSettingsCommand.Execute(null);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void A_lenient_runner_withholds_the_tool_without_a_provider_a_warning_that_never_blocks()
    {
        var tab = Launcher(NewTeam(decideTool: true), """{"Orkeon":{"CrewFactory":{"StrictTools":false}}}""");

        Assert.False(tab.IsBlockedByDecision);
        Assert.True(tab.HasDecisionWarning);
        Assert.Contains("'writer'", tab.DecisionWarning, StringComparison.Ordinal);
        Assert.Contains("the engine withholds the tool", tab.DecisionWarning, StringComparison.Ordinal);
        Assert.Contains("No decision provider is configured", tab.DecisionWarning, StringComparison.Ordinal);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Another_team_is_judged_on_its_own_file_never_on_the_file_of_the_team_before_it()
    {
        var blocked = NewTeam(decideTool: true, name: "blocked");
        var served = NewTeam(decideTool: true, name: "served");
        var settings = new FakeAppSettingsStore();
        settings.Files[WorkshopLayout.SettingsFileOf(_root, "blocked")] = NoProvider;
        settings.Files[WorkshopLayout.SettingsFileOf(_root, "served")] = """{"Orkeon":{"Liquid":{"ApiKey":"in-file"}}}""";
        var probe = new FakeTargetProbe();
        foreach (var team in new[] { blocked, served })
            probe.WithDirectory(team).WithDirectory(Path.Combine(team, "agents"));
        var tab = new LaunchTabViewModel(new LaunchTabDependencies
        {
            ProcessRunner = new OrkeonProcessRunner(
                new FakeProcessLauncher(), new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
            TargetProbe = probe,
            Directories = new FakeDirectoryProbe(_root, blocked, served),
            HistoryStore = new FakeLaunchHistoryStore(),
            SettingsStore = settings,
            MachineDecisionStatus = () => null,
            KeyStore = new FakeApiKeyStore(),
            TeamsRoot = _root,
        });

        tab.Target.Select(blocked);
        await tab.RefreshSettingsMountsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopLayout.SettingsFileOf(_root, "blocked"), tab.ResolvedSettingsPath);
        Assert.True(tab.IsBlockedByDecision);

        // The next team's file is still being read: the verdict of the file before it is not this team's.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.LoadGate = gate;
        tab.Target.Select(served);

        Assert.False(tab.IsBlockedByDecision);
        Assert.True(tab.HasDecisionWarning);

        gate.SetResult();
        await tab.RefreshSettingsMountsAsync(TestContext.Current.CancellationToken);

        Assert.False(tab.IsBlockedByDecision);
        Assert.False(tab.HasDecisionWarning);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void Unticking_the_decision_models_stops_a_holder_of_the_tool_which_the_engine_withholds()
    {
        var tab = Launcher(NewTeam(decideTool: true), Liquid);
        Assert.False(tab.IsBlockedByDecision);
        Assert.False(tab.HasDecisionWarning);
        Assert.True(tab.RunCommand.CanExecute(null));

        tab.Options.UseDecisionModels = false;

        // With the models off no consumer asks them: the engine withholds the tool, and a strict runner
        // refuses the team that lists it.
        Assert.True(tab.IsBlockedByDecision);
        Assert.False(tab.HasDecisionWarning);
        Assert.Contains("'writer' use system_one_decide, which the engine withholds", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.Contains("tick « Use the decision models »", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        Assert.Contains("--no-decision", tab.CommandLinePreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Unticking_the_decision_models_stops_a_decision_task_that_has_no_answer_of_its_own()
    {
        var tab = Launcher(NewTeam(decisionTask: true, noAnswer: true), Liquid);
        Assert.True(tab.UsesDecisionModels);
        Assert.False(tab.IsBlockedByDecision);
        Assert.True(tab.RunCommand.CanExecute(null));

        tab.Options.UseDecisionModels = false;

        Assert.True(tab.IsBlockedByDecision);
        Assert.Contains("the decision task 'pick' has neither a fallback", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.Contains("tick « Use the decision models »", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("No decision provider", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        Assert.Equal(tab.DecisionMessage, tab.LaunchBlockedReason);
        Assert.Equal("fail", tab.LaunchReadinessTone);

        tab.Options.UseDecisionModels = true;

        Assert.False(tab.IsBlockedByDecision);
        Assert.Null(tab.LaunchBlockedReason);
        Assert.Equal("ok", tab.LaunchReadinessTone);
    }

    [Fact]
    public void Without_a_provider_a_decision_task_that_has_no_answer_of_its_own_is_stopped_and_every_reason_said()
    {
        var tab = Launcher(NewTeam(decisionTask: true, decideTool: true, noAnswer: true), NoProvider);

        Assert.True(tab.IsBlockedByDecision);
        Assert.Contains(
            "'writer' use system_one_decide, which the engine withholds without the decision models; the decision task 'pick' has neither",
            tab.DecisionMessage, StringComparison.Ordinal);
        Assert.Contains("No decision provider is configured", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("tick « Use the decision models »", tab.DecisionMessage, StringComparison.Ordinal);

        tab.Options.UseDecisionModels = false;

        // Each reason, in the order it is best fixed.
        var noProvider = tab.DecisionMessage.IndexOf("No decision provider is configured", StringComparison.Ordinal);
        var runSwitch = tab.DecisionMessage.IndexOf("tick « Use the decision models »", StringComparison.Ordinal);
        Assert.True(noProvider >= 0 && runSwitch > noProvider, tab.DecisionMessage);
    }

    [Fact]
    public void A_provider_without_its_key_is_warned_of_until_the_key_is_stored()
    {
        var keys = new FakeApiKeyStore();
        var tab = Launcher(NewTeam(decisionTask: true), Liquid, keys: keys);

        Assert.False(tab.IsBlockedByDecision);
        Assert.True(tab.HasDecisionWarning);
        Assert.Contains("Liquid has no key", tab.DecisionWarning, StringComparison.Ordinal);
        Assert.Contains(LiquidKey, tab.DecisionWarning, StringComparison.Ordinal);
        Assert.True(tab.RunCommand.CanExecute(null));

        // The key stored in Settings › Decision models: the file read once is judged again on it.
        keys.Stage(LiquidKey, "liquid-key");
        tab.RefreshDecision();

        Assert.False(tab.HasDecisionWarning);
        Assert.Equal("", tab.DecisionWarning);
    }

    [Fact]
    public void A_team_that_asks_nothing_sends_no_switch_whatever_the_box_was_left_at()
    {
        var other = NewTeam(name: "plain");
        var team = NewTeam(decisionTask: true, name: "triage");
        var tab = Launcher(team, Liquid, otherTeams: [other]);
        tab.Options.UseDecisionModels = false;
        Assert.Contains("--no-decision", tab.CommandLinePreview, StringComparison.Ordinal);

        tab.Target.Select(other);

        Assert.False(tab.UsesDecisionModels);
        Assert.DoesNotContain("--no-decision", tab.CommandLinePreview, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-decision", tab.BuildArguments());

        // Back on the team that asks them, the box as left goes on the command line again.
        tab.Target.Select(team);

        Assert.Contains("--no-decision", tab.CommandLinePreview, StringComparison.Ordinal);
    }

    [Fact]
    public void A_team_that_runs_either_way_is_told_which_answers_its_decisions_fall_back_to()
    {
        var tab = Launcher(NewTeam(decisionTask: true), NoProvider);

        Assert.True(tab.UsesDecisionModels);
        Assert.False(tab.IsBlockedByDecision);
        Assert.True(tab.HasDecisionInfo);
        var lines = tab.DecisionInfo.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("No decision provider is configured", lines[0], StringComparison.Ordinal);
        Assert.Equal("• Task 'pick': go = no", lines[1]);
        Assert.True(tab.RunCommand.CanExecute(null));
        // The settings give the provider: the alert offers them.
        Assert.True(tab.ShowsDecisionSettingsAction);
    }

    [Fact]
    public void A_run_that_goes_without_the_models_on_its_own_box_alone_offers_no_settings()
    {
        var tab = Launcher(NewTeam(decisionTask: true), Liquid);

        tab.Options.UseDecisionModels = false;

        Assert.True(tab.HasDecisionInfo);
        Assert.Contains("tick « Use the decision models »", tab.DecisionInfo, StringComparison.Ordinal);
        Assert.False(tab.ShowsDecisionSettingsAction);
    }

    [Fact]
    public void With_a_provider_a_team_that_runs_either_way_is_told_nothing()
    {
        var tab = Launcher(NewTeam(decisionTask: true), Liquid);

        Assert.False(tab.HasDecisionInfo);
        Assert.False(tab.HasDecisionWarning);
        Assert.DoesNotContain("--no-decision", tab.CommandLinePreview, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_the_engine_refuses_stops_the_launch_and_the_check()
    {
        var tab = Launcher(NewTeam(decisionTask: true), """{"Orkeon":{"Decision":{"Provider":"acme"}}}""");

        Assert.True(tab.IsBlockedByDecision);
        Assert.Contains("acme", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        Assert.False(tab.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public void While_the_cli_picks_the_settings_file_the_machines_file_warns_and_never_blocks()
    {
        var machine = new DecisionStatus(true, null, DecisionProviderSelection.Deduced, [], null);
        var tab = Launcher(NewTeam(decideTool: true), pinnedJson: null, machine);

        Assert.False(tab.IsBlockedByDecision);
        Assert.True(tab.HasDecisionWarning);
        Assert.Contains("pin the file", tab.DecisionWarning, StringComparison.Ordinal);
        Assert.Contains("No decision provider is configured", tab.DecisionWarning, StringComparison.Ordinal);
        Assert.True(tab.RunCommand.CanExecute(null));

        // The machine's file turns the models on: nothing to say.
        var on = Launcher(NewTeam(decideTool: true), pinnedJson: null, machine with { Provider = DecisionProviderDescriptor.Kev });
        Assert.False(on.HasDecisionWarning);
    }

    [Fact]
    public async Task A_launch_reads_the_crew_again_and_refuses_a_team_that_now_needs_the_models()
    {
        var team = NewTeam();
        var tab = Launcher(team, NoProvider);
        Assert.False(tab.IsBlockedByDecision);

        await File.WriteAllTextAsync(
            Path.Combine(team, "agents", "writer.yaml"),
            "role: Writer\ngoal: write\ntools: [system_one_decide]\n",
            TestContext.Current.CancellationToken);
        var result = await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.True(tab.IsBlockedByDecision);
        Assert.Equal(tab.DecisionMessage, tab.StatusMessage);
    }

    /// <summary>
    /// The tool bridge of the file the run reads: tools to bridge with no token in its variable stop the
    /// run at start, a check included; the token stored, the launch is offered again.
    /// </summary>
    [Fact]
    public void A_bridge_without_its_token_stops_the_launch_and_the_check_until_the_token_is_in_place()
    {
        var keys = new FakeApiKeyStore();
        var tab = Launcher(
            NewTeam(),
            """{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":["*"]}}}}""",
            keys: keys);

        Assert.True(tab.IsBlockedByBridge);
        Assert.Contains("ORKEON_BRIDGE_TOKEN, which is not set", tab.BridgeMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        Assert.False(tab.ValidateCommand.CanExecute(null));
        Assert.Equal(tab.BridgeMessage, tab.LaunchBlockedReason);

        keys.Stage("ORKEON_BRIDGE_TOKEN", "0123456789abcdef0123456789abcdef");
        tab.RefreshDecision();

        Assert.False(tab.IsBlockedByBridge);
        Assert.Equal("", tab.BridgeMessage);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void A_bridge_setting_the_engine_refuses_stops_the_launch_and_an_address_without_a_tool_only_informs()
    {
        var refused = Launcher(NewTeam(), """{"Orkeon":{"Bridge":{"MaxSessions":0}}}""");
        Assert.True(refused.IsBlockedByBridge);
        Assert.Contains("Orkeon:Bridge:MaxSessions must lie between 1 and 1024", refused.BridgeMessage, StringComparison.Ordinal);
        Assert.False(refused.HasBridgeInfo);

        var idle = Launcher(NewTeam(name: "idle"), """{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/"}}}}""");
        Assert.False(idle.IsBlockedByBridge);
        Assert.True(idle.HasBridgeInfo);
        Assert.Contains("https://desk.example:7410/", idle.BridgeInfo, StringComparison.Ordinal);
        Assert.True(idle.RunCommand.CanExecute(null));
    }
}
