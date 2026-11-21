using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Run.Tests.Launcher;

/// <summary>
/// The decision models (System One) in the terminal launcher, as the engine runs them: a holder of
/// <c>system_one_decide</c> without a provider, and a decision task with no answer on a run with the
/// circuit off, are errors; the tool still asks the provider when the circuit is off, and a provider
/// without its key fails each request (warnings); a crew that runs either way is told its fallbacks; and
/// the run's switch reaches the command line as <c>--no-decision</c>.
/// </summary>
public sealed class RunLauncherDecisionModelsTests : IDisposable
{
    private const string Pinned = "/etc/triage/appsettings.json";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "orkeon-run-decision-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static Func<string, string?> LiquidKey => name => name == "LIQUID_API_KEY" ? "secret" : null;

    private string Crew(bool decideTool, bool decisionTask = false, bool fallback = true)
    {
        Directory.CreateDirectory(_folder);
        var crewFile = Path.Combine(_folder, "crew.yaml");
        File.WriteAllText(crewFile, $$"""
            name: triage
            goal: triage
            process: sequential
            agents:
              analyst:
                role: Analyst
                goal: read
                tools: [{{(decideTool ? "system_one_decide" : "file_read")}}]
            tasks:
              read:
                description: read
                expected_output: notes
                agent: analyst
            {{(decisionTask ? """
              pick:
                description: is it ready
                expected_output: an answer
                dependencies: [read]
                decision:
                  questions:
                    go: { noul: the work is ready }
            """ : "")}}
            {{(decisionTask && fallback ? """
                  fallback: { go: "no" }
            """ : "")}}
            """);
        return crewFile;
    }

    private static Orkeon.Studio.Run.Launcher.RunLauncherViewModel Pin(LauncherFixture fixture, string crewFile, string? settingsJson)
    {
        var crew = fixture.WithYamlCrew(crewFile);
        var launcher = fixture.Build();
        launcher.Target.Select(crew);
        if (settingsJson is not null)
        {
            fixture.Settings.WithFile(Pinned, settingsJson);
            launcher.Options.UseAutomaticSettings = false;
            launcher.Options.ExplicitSettingsPath = Pinned;
        }

        launcher.RefreshSettingsMounts();
        return launcher;
    }

    [Fact]
    public void A_crew_that_needs_the_models_is_an_error_without_a_provider_and_a_warning_for_a_check()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: true), "{}");

        var error = Assert.Single(launcher.Validate(), m => m.Code == ValidationCodes.DecisionRequired);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("'analyst' use system_one_decide", error.Text, StringComparison.Ordinal);
        Assert.Contains("No decision provider is configured", error.Text, StringComparison.Ordinal);
        Assert.True(launcher.HasBlockingErrors());

        // The check stays offered: the engine loads the crew and reports the withheld tool as a warning.
        var check = Assert.Single(launcher.Validate(dryRun: true), m => m.Code == ValidationCodes.DecisionRequired);
        Assert.Equal(ValidationSeverity.Warning, check.Severity);
        Assert.StartsWith("A check loads this crew, but a run will refuse it", check.Text, StringComparison.Ordinal);
        Assert.False(launcher.HasBlockingErrors(dryRun: true));
    }

    [Fact]
    public void A_lenient_runner_withholds_the_tool_without_a_provider_a_warning_for_a_run_and_a_check()
    {
        var launcher = Pin(
            new LauncherFixture().WithInstalledCli(),
            Crew(decideTool: true),
            """{"Orkeon":{"CrewFactory":{"StrictTools":false}}}""");

        foreach (var dryRun in new[] { false, true })
        {
            var messages = launcher.Validate(dryRun);
            Assert.DoesNotContain(messages, m => m.Code == ValidationCodes.DecisionRequired);
            var warning = Assert.Single(messages, m => m.Code == ValidationCodes.DecisionToolDropped);
            Assert.Equal(ValidationSeverity.Warning, warning.Severity);
            Assert.Contains("'analyst' use system_one_decide", warning.Text, StringComparison.Ordinal);
            Assert.Contains("withholds the tool", warning.Text, StringComparison.Ordinal);
            Assert.Contains("No decision provider is configured", warning.Text, StringComparison.Ordinal);
            Assert.False(launcher.HasBlockingErrors(dryRun));
        }
    }

    [Fact]
    public void A_provider_in_the_file_lets_the_tool_run_and_the_run_switch_withholds_it()
    {
        var fixture = new LauncherFixture().WithInstalledCli();
        fixture.Environment = LiquidKey;
        var launcher = Pin(fixture, Crew(decideTool: true), """{"Orkeon":{"Liquid":{}}}""");
        Assert.DoesNotContain(launcher.Validate(), m => m.Code.StartsWith("STUDIO-DECISION", StringComparison.Ordinal));

        launcher.Options.NoDecision = true;

        // With the circuit off no consumer asks the model: the engine withholds the tool, and a strict
        // runner refuses the crew that lists it.
        var error = Assert.Single(launcher.Validate(), m => m.Code == ValidationCodes.DecisionRequired);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("'analyst' use system_one_decide", error.Text, StringComparison.Ordinal);
        Assert.Contains("untick --no-decision", error.Text, StringComparison.Ordinal);
        Assert.True(launcher.HasBlockingErrors());
        Assert.False(launcher.HasBlockingErrors(dryRun: true));
    }

    [Fact]
    public void A_decision_task_without_an_answer_is_an_error_once_the_run_switches_the_models_off()
    {
        var fixture = new LauncherFixture().WithInstalledCli();
        fixture.Environment = LiquidKey;
        var launcher = Pin(fixture, Crew(decideTool: false, decisionTask: true, fallback: false), """{"Orkeon":{"Liquid":{}}}""");
        Assert.DoesNotContain(launcher.Validate(), m => m.Code == ValidationCodes.DecisionRequired);

        launcher.Options.NoDecision = true;

        var error = Assert.Single(launcher.Validate(), m => m.Code == ValidationCodes.DecisionRequired);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("the decision task 'pick' has neither a fallback nor an otherwise task", error.Text, StringComparison.Ordinal);
        Assert.Contains("untick --no-decision", error.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("system_one_decide", error.Text, StringComparison.Ordinal);
        Assert.True(launcher.HasBlockingErrors());
        Assert.False(launcher.HasBlockingErrors(dryRun: true));
        Assert.StartsWith("A check loads this crew", Assert.Single(
            launcher.Validate(dryRun: true), m => m.Code == ValidationCodes.DecisionRequired).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_need_and_every_reason_is_listed_the_missing_provider_first()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: true, decisionTask: true, fallback: false), "{}");
        launcher.Options.NoDecision = true;

        var error = Assert.Single(launcher.Validate(), m => m.Code == ValidationCodes.DecisionRequired);

        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("'analyst' use system_one_decide, which the engine withholds without the decision models", error.Text, StringComparison.Ordinal);
        Assert.Contains("the decision task 'pick' has neither", error.Text, StringComparison.Ordinal);
        var noProvider = error.Text.IndexOf("No decision provider is configured", StringComparison.Ordinal);
        var runSwitch = error.Text.IndexOf("untick --no-decision", StringComparison.Ordinal);
        Assert.True(noProvider >= 0 && runSwitch > noProvider, error.Text);
    }

    [Fact]
    public void A_provider_without_its_key_is_a_warning()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: true), """{"Orkeon":{"Liquid":{}}}""");

        var messages = launcher.Validate();

        var warning = Assert.Single(messages, m => m.Code == ValidationCodes.DecisionKeyMissing);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Contains("LIQUID_API_KEY", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(messages, m => m.Code == ValidationCodes.DecisionRequired);
        Assert.False(launcher.HasBlockingErrors());
    }

    [Fact]
    public void A_key_in_the_environment_is_a_provider()
    {
        var fixture = new LauncherFixture().WithInstalledCli();
        fixture.Environment = name => name == "TYPESAFE_API_KEY" ? "secret" : null;
        var launcher = Pin(fixture, Crew(decideTool: true), "{}");

        Assert.DoesNotContain(launcher.Validate(), m => m.Code == ValidationCodes.DecisionRequired);
    }

    [Fact]
    public void While_the_cli_resolves_the_settings_a_crew_that_needs_the_models_is_a_warning()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: true), settingsJson: null);

        var warning = Assert.Single(launcher.Validate(), m => m.Code == ValidationCodes.DecisionRequired);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Contains("pin the file", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_provider_stops_the_run_and_the_check()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: false), """{"Orkeon":{"TypeSafe":{},"Liquid":{}}}""");

        Assert.Equal(ValidationSeverity.Error, Assert.Single(launcher.Validate(), m => m.Code == ValidationCodes.DecisionProviderRefused).Severity);
        Assert.True(launcher.HasBlockingErrors(dryRun: true));
    }

    [Fact]
    public void A_crew_that_runs_either_way_is_told_its_fallbacks()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: false, decisionTask: true), "{}");

        var info = Assert.Single(launcher.Validate(), m => m.Code == ValidationCodes.DecisionFallback);
        Assert.Equal(ValidationSeverity.Information, info.Severity);
        Assert.Contains("task 'pick' answers go = no", info.Text, StringComparison.Ordinal);
        Assert.False(launcher.HasBlockingErrors());
    }

    /// <summary>
    /// The tool bridge of the pinned settings file: tools to bridge with no token in its variable are an
    /// error of the launch and of a check; an address with no tool allowed is said, and blocks nothing.
    /// </summary>
    [Fact]
    public void A_bridge_without_its_token_is_an_error_and_an_address_without_a_tool_is_information()
    {
        var fixture = new LauncherFixture().WithInstalledCli();
        var launcher = Pin(fixture, Crew(decideTool: false), """{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":["*"]}}}}""");

        foreach (var dryRun in new[] { false, true })
        {
            var error = Assert.Single(launcher.Validate(dryRun), m => m.Code == ValidationCodes.BridgeToken);
            Assert.Equal(ValidationSeverity.Error, error.Severity);
            Assert.Contains("ORKEON_BRIDGE_TOKEN, which is not set", error.Text, StringComparison.Ordinal);
            Assert.True(launcher.HasBlockingErrors(dryRun));
        }

        var idle = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: false), """{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/"}}}}""");
        var info = Assert.Single(idle.Validate(), m => m.Code == ValidationCodes.BridgeIdle);
        Assert.Equal(ValidationSeverity.Information, info.Severity);
        Assert.False(idle.HasBlockingErrors());
    }

    [Fact]
    public void A_bridge_setting_the_engine_refuses_is_an_error_at_its_key()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: false), """{"Orkeon":{"Bridge":{"Allow":["browser_*"]}}}""");

        var error = Assert.Single(launcher.Validate(), m => m.Code == ValidationCodes.EngineSettingRefused);

        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Equal("Orkeon:Bridge:Allow", error.Path);
        Assert.Contains("not a pattern", error.Text, StringComparison.Ordinal);
    }

    /// <summary>A single-file crew: <paramref name="agents"/> under <c>agents:</c>, one task for the analyst, and the mode line when given.</summary>
    private string CrewOf(string agents, string mode = "", string taskExtra = "")
    {
        Directory.CreateDirectory(_folder);
        var crewFile = Path.Combine(_folder, "crew.yaml");
        File.WriteAllText(
            crewFile,
            $"name: triage\ngoal: triage\nprocess: sequential\n{mode}agents:\n{agents}\n"
            + $"tasks:\n  read: {{ description: read, expected_output: notes, agent: analyst{taskExtra} }}\n");
        return crewFile;
    }

    private const string OnAuto = "  analyst: { role: Analyst, goal: read, llm: { profile: auto } }";
    private const string OnGuarded = "  analyst: { role: Analyst, goal: read, llm: { profile: guarded } }";
    private const string None = "decision: { mode: none }\n";
    private const string TwoRungs = """{"Llm":{"Profiles":{"strong":{"Provider":"mock"}}},"Orkeon":{"Decision":{"Route":{"Ladder":["default","strong"]}}}}""";
    private const string UnknownRung = """{"Orkeon":{"Decision":{"Route":{"Ladder":["default","strong"]}}}}""";

    private static string Guarded(bool failClosed, string provider = "") =>
        "{\"Orkeon\":{" + provider + "\"Decision\":{\"Profiles\":{\"guarded\":{\"Target\":\"default\",\"FailClosed\":"
        + (failClosed ? "true" : "false")
        + ",\"Stages\":[{\"Kind\":\"guard\",\"Risks\":{\"x\":\"y\"}}]}}}}}";

    private static ValidationMessage Only(Orkeon.Studio.Run.Launcher.RunLauncherViewModel launcher, bool dryRun = false) =>
        Assert.Single(launcher.Validate(dryRun), m => m.Code.StartsWith("STUDIO-DECISION", StringComparison.Ordinal));

    /// <summary>
    /// A crew that states none and lets System One pick a profile is refused at load whatever the
    /// settings, a ladder included: an error with a file, with none read too; a check stays offered.
    /// </summary>
    [Theory]
    [InlineData(TwoRungs)]
    [InlineData("{}")]
    [InlineData(null)]
    public void A_holder_on_profile_auto_under_the_mode_none_is_an_error_whatever_the_settings(string? settings)
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnAuto, None), settings);

        var error = Only(launcher);
        Assert.Equal(ValidationCodes.DecisionModeRefused, error.Code);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("'analyst' run on llm.profile: auto, which System One picks, and the crew states decision.mode: none", error.Text, StringComparison.Ordinal);
        Assert.True(launcher.HasBlockingErrors());

        Assert.Equal(ValidationSeverity.Warning, Only(launcher, dryRun: true).Severity);
        Assert.False(launcher.HasBlockingErrors(dryRun: true));
    }

    [Fact]
    public void A_task_on_profile_auto_under_the_mode_none_is_named()
    {
        var launcher = Pin(
            new LauncherFixture().WithInstalledCli(),
            CrewOf("  analyst: { role: Analyst, goal: read }", None, ", llm_override: { profile: auto }"),
            TwoRungs);

        var error = Only(launcher);
        Assert.Equal(ValidationCodes.DecisionModeRefused, error.Code);
        Assert.Contains("'read' run on llm.profile: auto", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decision_profile_under_the_mode_none_is_an_error_of_the_file_read_here()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnGuarded, None), Guarded(failClosed: false));

        var error = Only(launcher);
        Assert.Equal(ValidationCodes.DecisionModeRefused, error.Code);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("'analyst' name a decision profile, and the crew states decision.mode: none", error.Text, StringComparison.Ordinal);
        Assert.Equal(ValidationSeverity.Warning, Only(launcher, dryRun: true).Severity);

        // No file read: nothing tells the profile is a decision profile.
        var unread = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnGuarded, None), settingsJson: null);
        Assert.DoesNotContain(unread.Validate(), m => m.Code.StartsWith("STUDIO-DECISION", StringComparison.Ordinal));
    }

    [Fact]
    public void A_crew_on_profile_auto_is_an_error_without_a_ladder_in_the_file_read_here()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnAuto), "{}");

        var error = Only(launcher);
        Assert.Equal(ValidationCodes.DecisionLadder, error.Code);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("'analyst' run on llm.profile: auto, and the settings give no ladder of two profiles or more", error.Text, StringComparison.Ordinal);
        Assert.True(launcher.HasBlockingErrors());
        Assert.Equal(ValidationSeverity.Warning, Only(launcher, dryRun: true).Severity);
        Assert.False(launcher.HasBlockingErrors(dryRun: true));

        // While the CLI resolves the settings nothing is known of the ladder.
        var unread = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnAuto), settingsJson: null);
        Assert.DoesNotContain(unread.Validate(), m => m.Code == ValidationCodes.DecisionLadder);
    }

    /// <summary>
    /// This launcher lays no profile in the run's environment: a rung the file does not define is the
    /// engine's refusal at load, unless the environment it is started in lays that profile.
    /// </summary>
    [Fact]
    public void A_rung_the_file_does_not_define_is_an_error_unless_the_environment_lays_it()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnAuto), UnknownRung);

        var error = Only(launcher);
        Assert.Equal(ValidationCodes.DecisionLadder, error.Code);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("Orkeon:Decision:Route:Ladder names 'strong', a profile neither the settings define", error.Text, StringComparison.Ordinal);
        Assert.Equal(ValidationSeverity.Warning, Only(launcher, dryRun: true).Severity);

        // The rung defined by the file: only the circuit is left to say (no provider, a crew that runs either way).
        var defined = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnAuto), TwoRungs);
        Assert.Equal(ValidationCodes.DecisionFallback, Only(defined).Code);

        var fixture = new LauncherFixture().WithInstalledCli();
        fixture.Environment = name => name == "ORKEON_Llm__Profiles__strong__Provider" ? "mock" : null;
        var laid = Pin(fixture, CrewOf(OnAuto), UnknownRung);
        Assert.Equal(ValidationCodes.DecisionFallback, Only(laid).Code);
    }

    /// <summary>
    /// The environment this launcher is started in lays a model profile under the name of a decision
    /// profile of the file: the engine refuses to start (« Two LLM profiles are named 'x'. »), a
    /// check included; nothing of it without that variable.
    /// </summary>
    [Theory]
    [InlineData("ORKEON_Llm__Profiles__guarded__Model")]
    [InlineData("Llm__Profiles__guarded__Provider")]
    public void A_decision_profile_the_environment_lays_as_a_model_profile_is_an_error_of_the_check_too(string variable)
    {
        var fixture = new LauncherFixture().WithInstalledCli();
        fixture.Environment = name => name == variable ? "mock" : null;
        var launcher = Pin(fixture, CrewOf(OnGuarded), Guarded(failClosed: false, "\"Liquid\":{\"ApiKey\":\"in-file\"},"));

        foreach (var dryRun in new[] { false, true })
        {
            var error = Only(launcher, dryRun);
            Assert.Equal(ValidationCodes.DecisionProviderRefused, error.Code);
            Assert.Equal(ValidationSeverity.Error, error.Severity);
            Assert.EndsWith("Two LLM profiles are named 'guarded'.", error.Text, StringComparison.Ordinal);
            Assert.True(launcher.HasBlockingErrors(dryRun));
        }

        var alone = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnGuarded), Guarded(failClosed: false, "\"Liquid\":{\"ApiKey\":\"in-file\"},"));
        Assert.DoesNotContain(alone.Validate(), m => m.Code.StartsWith("STUDIO-DECISION", StringComparison.Ordinal));
    }

    /// <summary>
    /// A fail-closed decision profile with a guard stage blocks every call once the circuit is off: an
    /// error of the run, whatever turns the circuit off; a check loads the crew and is told so.
    /// </summary>
    [Fact]
    public void A_fail_closed_guarded_profile_is_an_error_once_the_run_goes_without_the_models()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnGuarded), Guarded(failClosed: true));

        var error = Only(launcher);
        Assert.Equal(ValidationCodes.DecisionProfileBlocked, error.Code);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.StartsWith("'analyst' run on a fail-closed decision profile with a guard stage", error.Text, StringComparison.Ordinal);
        Assert.Contains("blocks every call made through the profile", error.Text, StringComparison.Ordinal);
        Assert.Contains("No decision provider is configured", error.Text, StringComparison.Ordinal);
        Assert.True(launcher.HasBlockingErrors());

        var check = Only(launcher, dryRun: true);
        Assert.Equal(ValidationCodes.DecisionProfileBlocked, check.Code);
        Assert.Equal(ValidationSeverity.Warning, check.Severity);
        Assert.StartsWith("A check loads this crew, but a run will fail: 'analyst'", check.Text, StringComparison.Ordinal);
        Assert.False(launcher.HasBlockingErrors(dryRun: true));

        // With a provider the profile runs; the run's own switch blocks it again.
        var served = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnGuarded), Guarded(failClosed: true, "\"Liquid\":{\"ApiKey\":\"in-file\"},"));
        Assert.DoesNotContain(served.Validate(), m => m.Code.StartsWith("STUDIO-DECISION", StringComparison.Ordinal));
        served.Options.NoDecision = true;
        var switched = Only(served);
        Assert.Equal(ValidationSeverity.Error, switched.Severity);
        Assert.Contains("untick --no-decision", switched.Text, StringComparison.Ordinal);

        // A profile that is not fail-closed has its stages skipped: information.
        var open = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnGuarded), Guarded(failClosed: false));
        var info = Only(open);
        Assert.Equal(ValidationCodes.DecisionFallback, info.Code);
        Assert.Contains("the decision stages of the profile of 'analyst' are skipped", info.Text, StringComparison.Ordinal);
    }

    /// <summary>What System One picks, each said with what becomes of it once the circuit is off.</summary>
    [Fact]
    public void Each_pick_of_System_One_is_told_what_it_falls_back_to()
    {
        var crew = CrewOf(
            "  analyst:\n    role: Analyst\n    goal: read\n    llm:\n      profile: auto\n      thinking: { effort: auto }\n"
            + "    skills: { auto: true }\n    compaction: { prune: true }");
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), crew, TwoRungs);

        var info = Only(launcher);
        Assert.Equal(ValidationCodes.DecisionFallback, info.Code);
        Assert.Equal(ValidationSeverity.Information, info.Severity);
        Assert.Contains("'analyst' runs on the default profile, no profile being picked", info.Text, StringComparison.Ordinal);
        Assert.Contains("'analyst' keeps the thinking level of its profile, none being picked", info.Text, StringComparison.Ordinal);
        Assert.Contains("'analyst' carries its fallback skills, none being picked", info.Text, StringComparison.Ordinal);
        Assert.Contains("'analyst' prunes no tool result", info.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A warning never hides an error: a crew that states required, routed on auto, on a run that
    /// switches the models off, is an error whatever else the settings would be warned of.
    /// </summary>
    [Fact]
    public void A_required_crew_on_profile_auto_is_an_error_once_the_run_switches_the_models_off()
    {
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), CrewOf(OnAuto, "decision: { mode: required }\n"), settingsJson: null);
        launcher.Options.NoDecision = true;

        var error = Only(launcher);
        Assert.Equal(ValidationCodes.DecisionRequired, error.Code);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Contains("it states decision.mode: required", error.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The summary's profile the file does not define: nothing lays it for a run launched here, so the
    /// host refuses to start, a check included, unless the environment lays it.
    /// </summary>
    [Fact]
    public void A_compaction_profile_the_file_does_not_define_is_an_error_unless_the_environment_lays_it()
    {
        const string settings = """{"Orkeon":{"Compaction":{"Profile":"cheap"}}}""";
        var launcher = Pin(new LauncherFixture().WithInstalledCli(), Crew(decideTool: false), settings);

        foreach (var dryRun in new[] { false, true })
        {
            var error = Assert.Single(launcher.Validate(dryRun), m => m.Code == ValidationCodes.EngineSettingRefused);
            Assert.Equal(ValidationSeverity.Error, error.Severity);
            Assert.Equal("Orkeon:Compaction:Profile", error.Path);
            Assert.Contains("'cheap'", error.Text, StringComparison.Ordinal);
            Assert.True(launcher.HasBlockingErrors(dryRun));
        }

        var fixture = new LauncherFixture().WithInstalledCli();
        fixture.Environment = name => name == "ORKEON_Llm__Profiles__cheap__Model" ? "small" : null;
        Assert.DoesNotContain(Pin(fixture, Crew(decideTool: false), settings).Validate(), m => m.Code == ValidationCodes.EngineSettingRefused);

        var defined = Pin(
            new LauncherFixture().WithInstalledCli(),
            Crew(decideTool: false),
            """{"Llm":{"Profiles":{"cheap":{"Provider":"mock"}}},"Orkeon":{"Compaction":{"Profile":"cheap"}}}""");
        Assert.DoesNotContain(defined.Validate(), m => m.Code == ValidationCodes.EngineSettingRefused);
    }
}
