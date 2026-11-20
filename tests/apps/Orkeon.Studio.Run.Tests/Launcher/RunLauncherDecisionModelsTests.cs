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

        // The check stays offered, and says what the engine answers it: the unknown-tool line.
        var check = Assert.Single(launcher.Validate(dryRun: true), m => m.Code == ValidationCodes.DecisionRequired);
        Assert.Equal(ValidationSeverity.Warning, check.Severity);
        Assert.StartsWith("The check reports the engine's unknown-tool line for system_one_decide", check.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("loads this crew", check.Text, StringComparison.Ordinal);
        Assert.False(launcher.HasBlockingErrors(dryRun: true));
    }

    [Fact]
    public void A_lenient_runner_drops_the_tool_without_a_provider_a_warning_for_a_run_and_a_check()
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
            Assert.Contains("drops the tool", warning.Text, StringComparison.Ordinal);
            Assert.False(launcher.HasBlockingErrors(dryRun));
        }
    }

    [Fact]
    public void A_provider_in_the_file_lets_the_tool_run_and_the_run_switch_only_warns_that_it_still_asks()
    {
        var fixture = new LauncherFixture().WithInstalledCli();
        fixture.Environment = LiquidKey;
        var launcher = Pin(fixture, Crew(decideTool: true), """{"Orkeon":{"Liquid":{}}}""");
        Assert.DoesNotContain(launcher.Validate(), m => m.Code.StartsWith("STUDIO-DECISION", StringComparison.Ordinal));

        launcher.Options.NoDecision = true;

        var messages = launcher.Validate();
        Assert.DoesNotContain(messages, m => m.Code == ValidationCodes.DecisionRequired);
        var warning = Assert.Single(messages, m => m.Code == ValidationCodes.DecisionToolIgnoresSwitch);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Contains("'analyst' use system_one_decide", warning.Text, StringComparison.Ordinal);
        Assert.Contains("the tool still asks Liquid", warning.Text, StringComparison.Ordinal);
        Assert.False(launcher.HasBlockingErrors());
        Assert.Contains("--no-decision", launcher.BuildArguments());
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
        Assert.Contains("'analyst' use system_one_decide, which needs a decision provider", error.Text, StringComparison.Ordinal);
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
}
