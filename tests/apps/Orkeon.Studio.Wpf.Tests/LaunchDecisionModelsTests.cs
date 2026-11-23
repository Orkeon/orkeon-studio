using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Launch;
using Orkeon.Studio.Wpf.ViewModels.Services;
using Orkeon.Studio.Wpf.ViewModels.Shell;

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
        string team, string? pinnedJson, DecisionStatus? machine = null, FakeApiKeyStore? keys = null, params string[] otherTeams) =>
        Launcher(team, pinnedJson, machine, keys, wire: null, otherTeams);

    /// <summary>The same, <paramref name="wire"/> adding what a test needs of the shell's seams.</summary>
    private static LaunchTabViewModel Launcher(
        string team,
        string? pinnedJson,
        DecisionStatus? machine,
        FakeApiKeyStore? keys,
        Func<LaunchTabDependencies, LaunchTabDependencies>? wire,
        params string[] otherTeams)
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
        var seams = new LaunchTabDependencies
        {
            ProcessRunner = new OrkeonProcessRunner(
                new FakeProcessLauncher(), new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
            TargetProbe = probe,
            Directories = new FakeDirectoryProbe([team, .. otherTeams]),
            HistoryStore = new FakeLaunchHistoryStore(),
            SettingsStore = settings,
            MachineDecisionStatus = () => machine,
            KeyStore = keys,
        };
        var tab = new LaunchTabViewModel(wire?.Invoke(seams) ?? seams);
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
        // A check stays offered: it loads the crew, reports the withheld tool as a warning, and starts no agent.
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

    /// <summary>
    /// A provider without its key turns the circuit off (cause <c>no-key</c>, Orkeon #451) as the other
    /// causes do: a fail-closed guarded profile stops the launch, and a lenient runner withholds the tool,
    /// each telling which variable to fill rather than warning of the key alone.
    /// </summary>
    [Fact]
    public void A_provider_without_its_key_blocks_a_fail_closed_profile_and_withholds_the_tool()
    {
        var strings = EnglishStudioStrings.Instance;
        var noKey = strings.Format(StudioStringKeys.RunDecisionOffNoKey, "Liquid", LiquidKey);
        var guarded = """{"Orkeon":{"Liquid":{},"Decision":{"Profiles":{"guarded":{"Target":"default","FailClosed":true,"Stages":[{"Kind":"guard","Risks":{"x":"y"}}]}}}}}""";

        var blocked = Launcher(TeamOf("llm: { profile: guarded }"), guarded, keys: new FakeApiKeyStore());
        Assert.True(blocked.IsBlockedByDecision);
        Assert.StartsWith("'writer' run on a decision profile whose guard stage blocks when it cannot judge (FailClosed)", blocked.DecisionMessage, StringComparison.Ordinal);
        Assert.Contains(noKey, blocked.DecisionMessage, StringComparison.Ordinal);
        Assert.False(blocked.RunCommand.CanExecute(null));
        Assert.True(blocked.ValidateCommand.CanExecute(null));

        var lenient = Launcher(
            NewTeam(decideTool: true, name: "lenient"), """{"Orkeon":{"Liquid":{},"CrewFactory":{"StrictTools":false}}}""", keys: new FakeApiKeyStore());
        Assert.False(lenient.IsBlockedByDecision);
        Assert.True(lenient.HasDecisionWarning);
        Assert.Contains("the engine withholds the tool", lenient.DecisionWarning, StringComparison.Ordinal);
        Assert.Contains(noKey, lenient.DecisionWarning, StringComparison.Ordinal);
        Assert.True(lenient.RunCommand.CanExecute(null));
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

    /// <summary>
    /// The engine turns the circuit off when the provider's key is missing (cause <c>no-key</c>, Orkeon #451):
    /// a team that needs the decision models is stopped, as for the other off causes, and told which
    /// variable to fill; one that can go without them is warned of the key. A variable other than the
    /// provider's own is named by its setting, never by what is written there (Orkeon #467).
    /// </summary>
    [Fact]
    public void A_provider_without_its_key_stops_a_team_that_needs_the_models_and_warns_the_others()
    {
        var strings = EnglishStudioStrings.Instance;
        // The Liquid file, its key left out of the store.
        var holder = Launcher(NewTeam(decideTool: true, name: "holder"), Liquid, keys: new FakeApiKeyStore());
        Assert.True(holder.IsBlockedByDecision);
        Assert.False(holder.RunCommand.CanExecute(null));
        Assert.True(holder.ValidateCommand.CanExecute(null));
        Assert.Contains(strings.Format(StudioStringKeys.RunDecisionOffNoKey, "Liquid", LiquidKey), holder.DecisionMessage, StringComparison.Ordinal);

        var optional = Launcher(NewTeam(decisionTask: true, name: "optional"), Liquid, keys: new FakeApiKeyStore());
        Assert.False(optional.IsBlockedByDecision);
        Assert.True(optional.RunCommand.CanExecute(null));
        Assert.True(optional.HasDecisionWarning);
        Assert.Equal(strings.Format(StudioStringKeys.RunWarnDecisionKeyMissing, "Liquid", LiquidKey), optional.DecisionWarning);

        const string Pasted = "sk_live_0123456789abcdef";
        var pasted = "{\"Orkeon\":{\"Liquid\":{\"ApiKeyEnvVar\":\"" + Pasted + "\"}}}";
        var customHolder = Launcher(NewTeam(decideTool: true, name: "custom-holder"), pasted);
        Assert.True(customHolder.IsBlockedByDecision);
        Assert.Contains(
            strings.Format(StudioStringKeys.RunDecisionOffNoKeyNamedBySetting, "Liquid", "Orkeon:Liquid:ApiKeyEnvVar"),
            customHolder.DecisionMessage,
            StringComparison.Ordinal);
        var customOptional = Launcher(NewTeam(decisionTask: true, name: "custom-optional"), pasted);
        Assert.Equal(
            strings.Format(StudioStringKeys.RunWarnDecisionKeyMissingNamedBySetting, "Liquid", "Orkeon:Liquid:ApiKeyEnvVar"),
            customOptional.DecisionWarning);
        Assert.DoesNotContain(Pasted, customHolder.DecisionMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Pasted, customOptional.DecisionWarning, StringComparison.Ordinal);
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
        Assert.Contains("turn on « Use the decision models »", tab.DecisionMessage, StringComparison.Ordinal);
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
        Assert.Contains("turn on « Use the decision models »", tab.DecisionMessage, StringComparison.Ordinal);
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
        Assert.DoesNotContain("turn on « Use the decision models »", tab.DecisionMessage, StringComparison.Ordinal);

        tab.Options.UseDecisionModels = false;

        // Each reason, in the order it is best fixed.
        var noProvider = tab.DecisionMessage.IndexOf("No decision provider is configured", StringComparison.Ordinal);
        var runSwitch = tab.DecisionMessage.IndexOf("turn on « Use the decision models »", StringComparison.Ordinal);
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
        Assert.Contains("turn on « Use the decision models »", tab.DecisionInfo, StringComparison.Ordinal);
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

    /// <summary>
    /// Settings known to be the run's are those of a pinned or team file, which the Settings screen does
    /// not edit: what blocks names that file. Only what is judged on the machine's settings, while the
    /// CLI picks the file, sends to Settings › Decision models.
    /// </summary>
    [Fact]
    public void What_a_pinned_file_blocks_names_the_file_to_fix_and_not_the_settings_screen()
    {
        const string ToSettings = "Settings › Decision models";

        var refused = Launcher(NewTeam(decisionTask: true), """{"Orkeon":{"Decision":{"Provider":"acme"}}}""");
        Assert.EndsWith($"Fix it in {PinnedSettings}.", refused.DecisionMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ToSettings, refused.DecisionMessage, StringComparison.Ordinal);

        var profile = Launcher(NewTeam(decisionTask: true, name: "profile"), """{"Orkeon":{"Decision":{"Profiles":{"p":{"Target":"default"}}}}}""");
        Assert.True(profile.IsBlockedByDecision);
        Assert.Contains("Orkeon:Decision:Profiles:p:Stages: a decision profile needs at least one stage.", profile.DecisionMessage, StringComparison.Ordinal);
        Assert.EndsWith($"Fix it in {PinnedSettings}.", profile.DecisionMessage, StringComparison.Ordinal);

        var noLadder = Launcher(TeamOf("llm: { profile: auto }"), Liquid);
        Assert.EndsWith($"Set the ladder (Orkeon:Decision:Route:Ladder) in {PinnedSettings}.", noLadder.DecisionMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ToSettings, noLadder.DecisionMessage, StringComparison.Ordinal);

        var machineLadder = Launcher(TeamOf("llm: { profile: auto }", name: "machine"), pinnedJson: null, MachineOn);
        Assert.EndsWith("Set the ladder in Settings › Decision models.", machineLadder.DecisionWarning, StringComparison.Ordinal);

        var off = Launcher(NewTeam(decideTool: true, name: "off"), """{"Orkeon":{"Liquid":{},"Decision":{"Enabled":false}}}""");
        Assert.True(off.IsBlockedByDecision);
        Assert.Contains($"set Orkeon:Decision:Enabled to true in {PinnedSettings}.", off.DecisionMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ToSettings, off.DecisionMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name a decision profile and a model profile share is mended by renaming one of the two, in the
    /// file or in Studio's model settings: the message says so, and sends to no screen that cannot.
    /// </summary>
    [Fact]
    public void A_name_two_profiles_share_says_to_rename_one_of_them()
    {
        static LaunchTabDependencies Laid(LaunchTabDependencies seams) => seams with { OfferedLlmProfiles = () => ["Guarded"] };

        var laid = Launcher(TeamOf("llm: { profile: guarded }"), GuardedSettings(failClosed: false, provider: true), machine: null, keys: null, wire: Laid);
        var inFile = Launcher(
            TeamOf("llm: { profile: guarded }", name: "file"),
            GuardedSettings(failClosed: false, provider: true).Replace("{\"Orkeon\":", "{\"Llm\":{\"Profiles\":{\"GUARDED\":{\"Provider\":\"mock\"}}},\"Orkeon\":", StringComparison.Ordinal));

        foreach (var tab in new[] { laid, inFile })
        {
            Assert.True(tab.IsBlockedByDecision);
            Assert.Contains("Two LLM profiles are named 'guarded'.", tab.DecisionMessage, StringComparison.Ordinal);
            Assert.Contains("rename one of the two", tab.DecisionMessage, StringComparison.Ordinal);
            Assert.EndsWith($"The decision profile is under Orkeon:Decision:Profiles in {PinnedSettings}.", tab.DecisionMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("Fix it in", tab.DecisionMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("Settings ›", tab.DecisionMessage, StringComparison.Ordinal);
        }
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

    /// <summary>
    /// While the CLI picks the settings file itself, the bridge of this machine's settings stands in for
    /// it as the decision circuit does: what would stop the run is a warning, and blocks nothing.
    /// </summary>
    [Fact]
    public void The_bridge_of_the_machines_settings_warns_and_never_blocks_while_the_cli_picks_the_file()
    {
        var team = NewTeam();
        var machine = AppSettingsDocument.Parse("""{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":["*"]}}}}""");
        var tab = new LaunchTabViewModel(new LaunchTabDependencies
        {
            ProcessRunner = new OrkeonProcessRunner(
                new FakeProcessLauncher(), new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
            TargetProbe = new FakeTargetProbe().WithDirectory(team).WithDirectory(Path.Combine(team, "agents")),
            Directories = new FakeDirectoryProbe([team]),
            HistoryStore = new FakeLaunchHistoryStore(),
            SettingsStore = new FakeAppSettingsStore(),
            MachineDecisionStatus = () => null,
            MachineBridgeFinding = () => ToolBridgePreflight.Check(machine, _ => null),
            KeyStore = new FakeApiKeyStore(),
        });
        tab.Target.Select(team);
        tab.RefreshDecision();

        Assert.False(tab.IsBlockedByBridge);
        Assert.True(tab.HasBridgeWarning);
        Assert.Contains("On this machine's settings", tab.BridgeWarning, StringComparison.Ordinal);
        Assert.Contains("ORKEON_BRIDGE_TOKEN, which is not set", tab.BridgeWarning, StringComparison.Ordinal);
        Assert.True(tab.RunCommand.CanExecute(null));

        var asked = 0;
        tab.OpenBridgeSettingsRequested += (_, _) => asked++;
        tab.OpenBridgeSettingsCommand.Execute(null);
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// A profile the crew names is a decision profile of the settings the run reads: the team asks
    /// System One after all, the box is offered, and unticking it says what becomes of the profile.
    /// </summary>
    [Fact]
    public void A_team_on_a_decision_profile_of_the_settings_is_offered_the_box()
    {
        var team = NewTeam();
        File.WriteAllText(Path.Combine(team, "agents", "writer.yaml"), "role: Writer\ngoal: write\nllm: { profile: guarded }\n");
        var plain = Launcher(team, Liquid);
        Assert.False(plain.UsesDecisionModels);

        var tab = Launcher(team, """{"Orkeon":{"Liquid":{"ApiKey":"in-file"},"Decision":{"Profiles":{"guarded":{"Target":"default","Stages":[{"Kind":"guard","Risks":{"x":"y"}}]}}}}}""");
        Assert.True(tab.UsesDecisionModels);
        Assert.False(tab.HasDecisionInfo);

        tab.Options.UseDecisionModels = false;

        Assert.True(tab.HasDecisionInfo);
        Assert.Contains("'writer': the decision stages of its profile are skipped.", tab.DecisionInfo, StringComparison.Ordinal);
    }

    private const string StandIn = "Judged on this machine's settings; the settings file the CLI picks may say otherwise. ";
    private const string TwoRungs = """{"Llm":{"Profiles":{"strong":{"Provider":"mock","Responses":"/r.jsonl"}}},"Orkeon":{"Liquid":{"ApiKey":"in-file"},"Decision":{"Route":{"Ladder":["default","strong"]}}}}""";
    private const string UnknownRung = """{"Orkeon":{"Liquid":{"ApiKey":"in-file"},"Decision":{"Route":{"Ladder":["default","strong"]}}}}""";
    private const string BridgeWithoutToken = """{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":["*"]}}}}""";

    private static readonly DecisionStatus MachineOn =
        new(true, DecisionProviderDescriptor.Liquid, DecisionProviderSelection.Deduced, [], null);

    private static string GuardedSettings(bool failClosed, bool provider) =>
        "{\"Orkeon\":{" + (provider ? "\"Liquid\":{\"ApiKey\":\"in-file\"}," : "")
        + "\"Decision\":{\"Profiles\":{\"guarded\":{\"Target\":\"default\",\"FailClosed\":" + (failClosed ? "true" : "false")
        + ",\"Stages\":[{\"Kind\":\"guard\",\"Risks\":{\"x\":\"y\"}}]}}}}}";

    /// <summary>A team whose writer has <paramref name="writer"/> in its file, the crew stating <paramref name="mode"/> when given.</summary>
    private string TeamOf(string writer, string? mode = null, string name = "triage")
    {
        var team = NewTeam(name: name);
        File.WriteAllText(Path.Combine(team, "agents", "writer.yaml"), "role: Writer\ngoal: write\n" + writer + "\n");
        if (mode is not null)
            File.WriteAllText(Path.Combine(team, "crew.yaml"), $"name: triage\ngoal: triage\nprocess: sequential\ndecision: {{ mode: {mode} }}\n");
        return team;
    }

    /// <summary>
    /// A team that states none and lets System One pick a profile is refused at load whatever the
    /// settings, a ladder included: blocked on a pinned file and while the CLI picks the file alike,
    /// said as a fact; a check stays offered and reports the engine's line.
    /// </summary>
    [Fact]
    public void A_holder_on_profile_auto_under_the_mode_none_stops_the_launch_whatever_the_settings()
    {
        var team = TeamOf("llm: { profile: auto }", mode: "none");

        foreach (var tab in new[]
        {
            Launcher(team, TwoRungs),
            Launcher(team, NoProvider),
            Launcher(team, pinnedJson: null, MachineOn with { RouteLadder = ["default", "strong"] }),
            Launcher(team, pinnedJson: null),
        })
        {
            Assert.True(tab.IsBlockedByDecision);
            Assert.False(tab.HasDecisionWarning);
            Assert.StartsWith("'writer' let System One pick their profile (llm.profile: auto), and the team states decision.mode: none", tab.DecisionMessage, StringComparison.Ordinal);
            Assert.False(tab.RunCommand.CanExecute(null));
            Assert.True(tab.ValidateCommand.CanExecute(null));
            // The mode refuses the box too: nothing to untick.
            Assert.False(tab.UsesDecisionModels);
        }
    }

    [Fact]
    public void A_decision_profile_under_the_mode_none_stops_the_launch_on_a_pinned_file_and_warns_on_the_machines()
    {
        var team = TeamOf("llm: { profile: guarded }", mode: "none");

        var pinned = Launcher(team, GuardedSettings(failClosed: false, provider: true));
        Assert.True(pinned.IsBlockedByDecision);
        Assert.StartsWith("'writer' name a decision profile, and the team states decision.mode: none", pinned.DecisionMessage, StringComparison.Ordinal);
        Assert.False(pinned.RunCommand.CanExecute(null));
        Assert.True(pinned.ValidateCommand.CanExecute(null));

        var machine = Launcher(team, pinnedJson: null, MachineOn with { DecisionProfiles = ["guarded"] });
        Assert.False(machine.IsBlockedByDecision);
        Assert.True(machine.HasDecisionWarning);
        Assert.StartsWith(StandIn + "'writer' name a decision profile", machine.DecisionWarning, StringComparison.Ordinal);
        Assert.True(machine.RunCommand.CanExecute(null));
    }

    /// <summary>
    /// A team routed on <c>llm.profile: auto</c>: no ladder, or a rung neither the file defines nor the
    /// launch lays in the environment, is the engine's refusal at load. Blocked on a pinned file, a
    /// warning on the machine's; a rung the launch offers is served and nothing is said.
    /// </summary>
    [Fact]
    public void A_team_on_profile_auto_is_stopped_without_a_ladder_or_on_a_rung_the_run_is_not_offered()
    {
        var team = TeamOf("llm: { profile: auto }");

        var noLadder = Launcher(team, Liquid);
        Assert.True(noLadder.IsBlockedByDecision);
        Assert.StartsWith(
            "'writer' let System One pick their profile (llm.profile: auto), and the settings give no ladder of two profiles or more to pick on: the engine refuses the team",
            noLadder.DecisionMessage,
            StringComparison.Ordinal);
        Assert.False(noLadder.RunCommand.CanExecute(null));
        Assert.True(noLadder.ValidateCommand.CanExecute(null));

        var unknownRung = Launcher(team, UnknownRung);
        Assert.True(unknownRung.IsBlockedByDecision);
        Assert.Contains("the ladder names 'strong', defined by no model profile of the settings and not given to the run by Studio", unknownRung.DecisionMessage, StringComparison.Ordinal);
        Assert.False(unknownRung.RunCommand.CanExecute(null));

        // The launch lays the profile in the run's environment: the rung is served.
        var offered = Launcher(team, UnknownRung, machine: null, keys: null, wire: seams => seams with { OfferedLlmProfiles = () => ["strong"] });
        Assert.False(offered.IsBlockedByDecision);
        Assert.False(offered.HasDecisionWarning);
        Assert.True(offered.RunCommand.CanExecute(null));
        Assert.False(Launcher(team, TwoRungs).IsBlockedByDecision);

        // The machine's file stands in: a warning that says so once, and never blocks.
        var machine = Launcher(team, pinnedJson: null, MachineOn);
        Assert.False(machine.IsBlockedByDecision);
        Assert.True(machine.HasDecisionWarning);
        Assert.StartsWith(StandIn + "'writer' let System One pick their profile (llm.profile: auto), and the settings give no ladder", machine.DecisionWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("settings the settings", machine.DecisionWarning, StringComparison.Ordinal);
        Assert.True(machine.RunCommand.CanExecute(null));

        var machineRung = Launcher(team, pinnedJson: null, MachineOn with { RouteLadder = ["default", "strong"], UnknownRungs = ["strong"] });
        Assert.True(machineRung.HasDecisionWarning);
        Assert.StartsWith(StandIn, machineRung.DecisionWarning, StringComparison.Ordinal);
        Assert.Contains("the ladder names 'strong'", machineRung.DecisionWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window counts the profiles the environment Studio runs in lays (<c>ORKEON_Llm__Profiles__x__*</c>),
    /// which the spawned CLI inherits, as the terminal launcher does: a rung served by one alone does not
    /// block the launch, and without that variable it does.
    /// </summary>
    [Theory]
    [InlineData("ORKEON_Llm__Profiles__strong__Model", false)]
    [InlineData("llm__profiles__STRONG__Provider", false)]
    [InlineData("ORKEON_Llm__Profiles__stronger__Model", true)]
    public void The_window_serves_a_rung_the_environment_lays_as_the_terminal_does(string variable, bool blocked)
    {
        var team = TeamOf("llm: { profile: auto }");
        var settings = new FakeAppSettingsStore();
        settings.Files[PinnedSettings] = UnknownRung;
        var window = new MainWindowViewModel(
            new StudioServices
            {
                SettingsStore = settings,
                Directories = new FakeDirectoryProbe(team),
                TargetProbe = new FakeTargetProbe().WithDirectory(team).WithDirectory(Path.Combine(team, "agents")),
                Picker = new FakePathPicker(),
                ProcessRunner = new OrkeonProcessRunner(
                    new FakeProcessLauncher(), new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
                HistoryStore = new FakeLaunchHistoryStore(),
                KeyStore = new FakeApiKeyStore(),
                EnvironmentNames = () => ["HOME", variable],
            },
            globalPathOverride: Path.Combine(_root, "global", "appsettings.json"),
            forgeWorkspace: Path.Combine(_root, "forge"),
            teamsRoot: Path.Combine(_root, "teams"));

        var tab = window.Launch;
        tab.Target.Select(team);
        tab.Options.SettingsPath = PinnedSettings;
        tab.Options.SettingsMode = SettingsSelectionMode.ExplicitPath;

        Assert.Equal(blocked, tab.IsBlockedByDecision);
        Assert.Equal(!blocked, tab.RunCommand.CanExecute(null));
    }

    /// <summary>
    /// While no settings file is saved, the machine's bridge the launcher warns of is the document of
    /// the settings screen: an expert's edit of the bridge's section is judged at once, not at the next save.
    /// </summary>
    [Fact]
    public void An_edit_of_the_bridge_before_any_save_reaches_the_launchers_warning()
    {
        var team = NewTeam();
        var window = new MainWindowViewModel(
            new StudioServices
            {
                SettingsStore = new FakeAppSettingsStore(),
                Directories = new FakeDirectoryProbe(team),
                TargetProbe = new FakeTargetProbe().WithDirectory(team).WithDirectory(Path.Combine(team, "agents")),
                Picker = new FakePathPicker(),
                ProcessRunner = new OrkeonProcessRunner(
                    new FakeProcessLauncher(), new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
                HistoryStore = new FakeLaunchHistoryStore(),
                KeyStore = new FakeApiKeyStore(),
                EnvironmentNames = () => [],
            },
            globalPathOverride: Path.Combine(_root, "global", "appsettings.json"),
            forgeWorkspace: Path.Combine(_root, "forge"),
            teamsRoot: Path.Combine(_root, "teams"));
        // The expert's edits wait for an explicit save; the novice's save at once.
        window.Mode.SetExpertCommand.Execute(null);
        var tab = window.Launch;
        tab.Target.Select(team);
        Assert.False(tab.HasBridgeWarning);

        window.Config.ToolBridge.Address = "https://desk.example:7410/";
        window.Config.ToolBridge.AllowText = "*";

        Assert.True(tab.HasBridgeWarning);
        Assert.Contains("ORKEON_BRIDGE_TOKEN, which is not set", tab.BridgeWarning, StringComparison.Ordinal);

        window.Config.ToolBridge.AllowText = "";

        Assert.DoesNotContain("ORKEON_BRIDGE_TOKEN", tab.BridgeWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fail-closed decision profile with a guard stage blocks every call once the circuit is off:
    /// the launch is stopped with its own words, whatever turns the circuit off; a check loads the crew.
    /// </summary>
    [Fact]
    public void A_fail_closed_guarded_profile_stops_the_launch_once_the_run_goes_without_the_models()
    {
        var team = TeamOf("llm: { profile: guarded }");

        var noProvider = Launcher(team, GuardedSettings(failClosed: true, provider: false));
        Assert.True(noProvider.UsesDecisionModels);
        Assert.True(noProvider.IsBlockedByDecision);
        Assert.StartsWith("'writer' run on a decision profile whose guard stage blocks when it cannot judge (FailClosed)", noProvider.DecisionMessage, StringComparison.Ordinal);
        Assert.Contains("the engine blocks every call made through that profile", noProvider.DecisionMessage, StringComparison.Ordinal);
        Assert.Contains("No decision provider is configured", noProvider.DecisionMessage, StringComparison.Ordinal);
        Assert.False(noProvider.RunCommand.CanExecute(null));
        Assert.True(noProvider.ValidateCommand.CanExecute(null));

        var served = Launcher(team, GuardedSettings(failClosed: true, provider: true));
        Assert.False(served.IsBlockedByDecision);
        served.Options.UseDecisionModels = false;
        Assert.True(served.IsBlockedByDecision);
        Assert.Contains("turn on « Use the decision models »", served.DecisionMessage, StringComparison.Ordinal);
        Assert.False(served.RunCommand.CanExecute(null));

        // On the machine's file the profile is not known to be the run's: a warning.
        var machine = Launcher(
            team,
            pinnedJson: null,
            new DecisionStatus(true, null, DecisionProviderSelection.Deduced, [], null) { DecisionProfiles = ["guarded"], BlockingProfiles = ["guarded"] });
        Assert.False(machine.IsBlockedByDecision);
        Assert.True(machine.HasDecisionWarning);
        Assert.StartsWith(StandIn + "'writer' run on a decision profile whose guard stage blocks", machine.DecisionWarning, StringComparison.Ordinal);

        // A profile that does not block: information, its stages skipped.
        var open = Launcher(team, GuardedSettings(failClosed: false, provider: false));
        Assert.False(open.IsBlockedByDecision);
        Assert.True(open.HasDecisionInfo);
    }

    /// <summary>
    /// A warning never hides what blocks: a team that states required and is routed on auto, on a file
    /// whose ladder is served and whose circuit is off, is stopped for the models it requires.
    /// </summary>
    [Fact]
    public void A_required_team_on_profile_auto_is_stopped_for_its_mode_once_the_ladder_is_served()
    {
        var team = TeamOf("llm: { profile: auto }", mode: "required");
        const string offUnknownRung = """{"Orkeon":{"Decision":{"Route":{"Ladder":["default","strong"]}}}}""";

        var tab = Launcher(team, offUnknownRung, machine: null, keys: null, wire: seams => seams with { OfferedLlmProfiles = () => ["strong"] });

        Assert.True(tab.IsBlockedByDecision);
        Assert.Contains("it states decision.mode: required", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));

        // The rung not offered, the load is refused first: the ladder is what is said.
        var unserved = Launcher(team, offUnknownRung);
        Assert.True(unserved.IsBlockedByDecision);
        Assert.Contains("the ladder names 'strong'", unserved.DecisionMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The box left unticked on a team that requires the models, another team selected, then the first
    /// again: the verdict is computed with the switch the command line carries, in both directions.
    /// </summary>
    [Fact]
    public void The_box_left_unticked_is_judged_again_when_the_team_comes_back()
    {
        var plain = NewTeam(name: "plain");
        var required = NewTeam(decideTool: true, name: "required");
        var tab = Launcher(required, Liquid, otherTeams: [plain]);
        Assert.False(tab.IsBlockedByDecision);

        tab.Options.UseDecisionModels = false;
        Assert.True(tab.IsBlockedByDecision);

        tab.Target.Select(plain);
        // The team asks nothing: the box is withdrawn, its switch with it, and nothing is said.
        Assert.False(tab.UsesDecisionModels);
        Assert.False(tab.IsBlockedByDecision);
        Assert.False(tab.HasDecisionWarning);
        Assert.False(tab.HasDecisionInfo);
        Assert.DoesNotContain("--no-decision", tab.CommandLinePreview, StringComparison.Ordinal);
        Assert.True(tab.RunCommand.CanExecute(null));

        tab.Target.Select(required);
        // Back on the first: the command line carries the switch again, and the launch is stopped for it.
        Assert.True(tab.UsesDecisionModels);
        Assert.Contains("--no-decision", tab.CommandLinePreview, StringComparison.Ordinal);
        Assert.True(tab.IsBlockedByDecision);
        Assert.Contains("turn on « Use the decision models »", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
    }

    /// <summary>
    /// What is read on the machine's settings while the CLI picks the file says so, and states nothing
    /// as a fact; the run's own box alone is a fact.
    /// </summary>
    [Fact]
    public void A_verdict_of_the_machines_settings_says_it_stands_in_for_the_file_the_cli_picks()
    {
        var noProvider = new DecisionStatus(true, null, DecisionProviderSelection.Deduced, [], null);

        var withheld = Launcher(NewTeam(decideTool: true, name: "holder"), pinnedJson: null, noProvider with { StrictTools = false });
        Assert.True(withheld.HasDecisionWarning);
        Assert.StartsWith(StandIn + "'writer' use system_one_decide", withheld.DecisionWarning, StringComparison.Ordinal);

        var optional = Launcher(NewTeam(decisionTask: true), pinnedJson: null, noProvider);
        Assert.True(optional.HasDecisionInfo);
        Assert.StartsWith(StandIn + "This run goes without the decision models", optional.DecisionInfo, StringComparison.Ordinal);

        // A provider of the machine's file without its key: the file the CLI picks may hold one.
        var keyless = Launcher(NewTeam(decisionTask: true, name: "keyless"), pinnedJson: null, MachineOn with { MissingKeyVariable = LiquidKey });
        Assert.True(keyless.HasDecisionWarning);
        Assert.StartsWith(StandIn + "Liquid has no key", keyless.DecisionWarning, StringComparison.Ordinal);

        // The run's own box: whatever file the CLI picks, the run goes without the models.
        var unticked = Launcher(NewTeam(decisionTask: true), pinnedJson: null, MachineOn);
        unticked.Options.UseDecisionModels = false;
        Assert.True(unticked.HasDecisionInfo);
        Assert.StartsWith("This run goes without the decision models", unticked.DecisionInfo, StringComparison.Ordinal);

        // A pinned file is the run's: said as it is.
        var pinned = Launcher(NewTeam(decisionTask: true), NoProvider);
        Assert.StartsWith("This run goes without the decision models", pinned.DecisionInfo, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model profile Studio lays in the run's environment under the name of a decision profile of the
    /// file: the engine refuses to start (« Two LLM profiles are named 'x'. »), so the launch and the
    /// check are stopped on a pinned file; on the machine's file it is a warning that never blocks.
    /// </summary>
    [Fact]
    public void A_decision_profile_named_as_a_profile_Studio_lays_stops_the_launch_and_the_check()
    {
        static LaunchTabDependencies Laid(LaunchTabDependencies seams) => seams with { OfferedLlmProfiles = () => ["Guarded"] };

        var tab = Launcher(TeamOf("llm: { profile: guarded }"), GuardedSettings(failClosed: false, provider: true), machine: null, keys: null, wire: Laid);
        Assert.True(tab.IsBlockedByDecision);
        Assert.Contains("Two LLM profiles are named 'guarded'.", tab.DecisionMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        Assert.False(tab.ValidateCommand.CanExecute(null));

        // Nothing laid under that name: the file is the run's and has nothing against it.
        var alone = Launcher(TeamOf("llm: { profile: guarded }", name: "alone"), GuardedSettings(failClosed: false, provider: true));
        Assert.False(alone.IsBlockedByDecision);
        Assert.False(alone.HasDecisionWarning);

        var machine = Launcher(
            TeamOf("llm: { profile: guarded }", name: "machine"), pinnedJson: null,
            MachineOn with { DecisionProfiles = ["guarded"] }, keys: null, wire: Laid);
        Assert.False(machine.IsBlockedByDecision);
        Assert.True(machine.HasDecisionWarning);
        Assert.Contains("Two LLM profiles are named 'guarded'.", machine.DecisionWarning, StringComparison.Ordinal);
        Assert.True(machine.RunCommand.CanExecute(null));
    }

    /// <summary>What System One picks, each line saying what becomes of it once the circuit is off.</summary>
    [Fact]
    public void Each_pick_of_System_One_is_told_what_it_falls_back_to()
    {
        var team = TeamOf("llm:\n  profile: auto\n  thinking: { effort: auto }\nskills: { auto: true }\ncompaction: { prune: true }");
        var tab = Launcher(team, """{"Llm":{"Profiles":{"strong":{"Provider":"mock","Responses":"/r.jsonl"}}},"Orkeon":{"Decision":{"Route":{"Ladder":["default","strong"]}}}}""");

        Assert.True(tab.HasDecisionInfo);
        Assert.Contains("\u2022 'writer': no profile is picked, it runs on the default profile.", tab.DecisionInfo, StringComparison.Ordinal);
        Assert.Contains("\u2022 'writer': no thinking level is picked, it keeps the one of its profile.", tab.DecisionInfo, StringComparison.Ordinal);
        Assert.Contains("\u2022 'writer': no skill is picked, it carries its fallback skills, if any.", tab.DecisionInfo, StringComparison.Ordinal);
        Assert.Contains("\u2022 'writer': no tool result is pruned.", tab.DecisionInfo, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing is known of a script, nor of a crew the engine refuses at load: the box is offered rather
    /// than withheld on a guess, and nothing is said of the decision models.
    /// </summary>
    [Fact]
    public void The_box_is_offered_for_a_script_and_for_a_crew_the_engine_refuses()
    {
        var folder = Path.Combine(_root, "scripted");
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "crew.ork.ts");
        File.WriteAllText(script, "// a script Studio does not read\n");
        var scripted = new LaunchTabViewModel(new LaunchTabDependencies
        {
            ProcessRunner = new OrkeonProcessRunner(
                new FakeProcessLauncher(), new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
            TargetProbe = new FakeTargetProbe().WithDirectory(folder).WithFile(script),
            Directories = new FakeDirectoryProbe([folder]),
            HistoryStore = new FakeLaunchHistoryStore(),
            SettingsStore = new FakeAppSettingsStore(),
            KeyStore = new FakeApiKeyStore(),
        });
        scripted.Target.Select(script);
        Assert.True(scripted.Target.IsResolved);
        Assert.True(scripted.UsesDecisionModels);
        Assert.False(scripted.IsBlockedByDecision);
        Assert.False(scripted.HasDecisionWarning);
        Assert.False(scripted.HasDecisionInfo);

        // A crew that states none and lists the tool: the engine's loader refuses it.
        var refused = Launcher(TeamOf("tools: [system_one_decide]", mode: "none", name: "refused"), NoProvider);
        Assert.True(refused.UsesDecisionModels);
        Assert.False(refused.IsBlockedByDecision);
        Assert.False(refused.HasDecisionInfo);
        // The box offered, its switch reaches the command line.
        refused.Options.UseDecisionModels = false;
        Assert.Contains("--no-decision", refused.CommandLinePreview, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warning of the machine's bridge goes once a file is pinned: that file's own bridge is judged,
    /// clean (nothing is said) or without its token (the launch is stopped).
    /// </summary>
    [Fact]
    public void The_warning_of_the_machines_bridge_goes_once_a_file_is_pinned()
    {
        var machine = AppSettingsDocument.Parse(BridgeWithoutToken);
        LaunchTabDependencies WithMachineBridge(LaunchTabDependencies seams) =>
            seams with { MachineBridgeFinding = () => ToolBridgePreflight.Check(machine, _ => null) };

        var unpinned = Launcher(NewTeam(), pinnedJson: null, machine: null, keys: null, wire: WithMachineBridge);
        unpinned.RefreshDecision();
        Assert.True(unpinned.HasBridgeWarning);
        Assert.False(unpinned.IsBlockedByBridge);

        var clean = Launcher(NewTeam(name: "clean"), NoProvider, machine: null, keys: null, wire: WithMachineBridge);
        Assert.False(clean.HasBridgeWarning);
        Assert.Equal("", clean.BridgeWarning);
        Assert.False(clean.IsBlockedByBridge);
        Assert.True(clean.RunCommand.CanExecute(null));

        var tokenless = Launcher(NewTeam(name: "tokenless"), BridgeWithoutToken, machine: null, keys: null, wire: WithMachineBridge);
        Assert.False(tokenless.HasBridgeWarning);
        Assert.True(tokenless.IsBlockedByBridge);
        Assert.False(tokenless.RunCommand.CanExecute(null));
    }

    /// <summary>
    /// The bridge's cards are on a tab only Expert mode shows: the alerts offer « Tool bridge settings »
    /// in that mode alone, and the token's advice names the mode.
    /// </summary>
    [Fact]
    public void The_bridge_alerts_offer_their_settings_in_expert_mode_only()
    {
        var tab = Launcher(NewTeam(), BridgeWithoutToken);
        Assert.True(tab.IsBlockedByBridge);
        Assert.False(tab.ShowsBridgeSettingsAction);
        Assert.Contains("Settings › Limits & index (Expert mode)", tab.BridgeMessage, StringComparison.Ordinal);

        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        tab.IsExpertMode = true;

        Assert.True(tab.ShowsBridgeSettingsAction);
        Assert.Contains(nameof(LaunchTabViewModel.ShowsBridgeSettingsAction), raised);

        tab.IsExpertMode = false;
        Assert.False(tab.ShowsBridgeSettingsAction);
    }

    /// <summary>
    /// The card of Settings keeps the token under the variable the machine's settings name: a pinned
    /// file that reads another is told its own variable, the card lifting nothing for it.
    /// </summary>
    [Fact]
    public void A_pinned_file_that_reads_its_token_from_another_variable_is_told_which()
    {
        const string ownVariable = """{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":["*"],"TokenEnvVar":"TEAM_BRIDGE_TOKEN"}}}}""";
        LaunchTabDependencies CardOnDefault(LaunchTabDependencies seams) => seams with { MachineBridgeTokenVariable = () => "ORKEON_BRIDGE_TOKEN" };

        var other = Launcher(NewTeam(), ownVariable, machine: null, keys: null, wire: CardOnDefault);
        Assert.True(other.IsBlockedByBridge);
        Assert.Contains("TEAM_BRIDGE_TOKEN, which is not set.", other.BridgeMessage, StringComparison.Ordinal);
        Assert.Contains(
            "This settings file reads the token from the variable TEAM_BRIDGE_TOKEN, while the tool bridge's card in Settings › Limits & index (Expert mode) keeps it under ORKEON_BRIDGE_TOKEN",
            other.BridgeMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Store the token in Settings", other.BridgeMessage, StringComparison.Ordinal);

        // The same variable as the card's: the card lifts the refusal, and is where the advice sends.
        var same = Launcher(NewTeam(name: "same"), BridgeWithoutToken, machine: null, keys: null, wire: CardOnDefault);
        Assert.Contains("Store the token in Settings › Limits & index (Expert mode)", same.BridgeMessage, StringComparison.Ordinal);
    }

    /// <summary>An idle bridge of the machine's settings is said as theirs while the CLI picks the file.</summary>
    [Fact]
    public void An_idle_bridge_of_the_machines_settings_is_said_as_standing_in()
    {
        var machine = AppSettingsDocument.Parse("""{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/"}}}}""");
        var tab = Launcher(
            NewTeam(), pinnedJson: null, machine: null, keys: null,
            wire: seams => seams with { MachineBridgeFinding = () => ToolBridgePreflight.Check(machine, _ => null) });
        tab.RefreshDecision();

        Assert.True(tab.HasBridgeInfo);
        Assert.StartsWith(StandIn + "These settings name a tool bridge (https://desk.example:7410/)", tab.BridgeInfo, StringComparison.Ordinal);
    }
}
