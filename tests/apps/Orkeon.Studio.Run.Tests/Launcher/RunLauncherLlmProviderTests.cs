using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Run.Tests.Launcher;

/// <summary>
/// The <c>Provider</c> of the <c>Llm</c> section and of its profiles in the settings file the terminal
/// launcher reads (Orkeon #457): one the host's start refuses is an error of the launch and of a check,
/// at its key, in the engine's sentence; nothing is said while the CLI resolves the file.
/// </summary>
public sealed class RunLauncherLlmProviderTests : IDisposable
{
    private const string Pinned = "/etc/triage/appsettings.json";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "orkeon-run-llm-provider-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private Orkeon.Studio.Run.Launcher.RunLauncherViewModel Pin(string? settingsJson)
    {
        Directory.CreateDirectory(_folder);
        var crewFile = Path.Combine(_folder, "crew.yaml");
        File.WriteAllText(crewFile, """
            name: triage
            goal: triage
            process: sequential
            agents:
              analyst: { role: Analyst, goal: read }
            tasks:
              read: { description: read, expected_output: notes, agent: analyst }
            """);
        var fixture = new LauncherFixture().WithInstalledCli();
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
    public void A_provider_the_start_refuses_is_an_error_of_the_launch_and_of_a_check()
    {
        var launcher = Pin("""{ "LLM": { "Provider": "acme", "Model": "m", "Profiles": { "p": { "Provider": "openai", "Model": "m" } } } }""");

        foreach (var dryRun in new[] { false, true })
        {
            var errors = launcher.Validate(dryRun).Where(m => m.Code == ValidationCodes.LlmProvider).ToList();
            Assert.Equal(["Llm:Provider", "Llm:Profiles:p:Provider"], errors.Select(error => error.Path));
            Assert.All(errors, error => Assert.Equal(ValidationSeverity.Error, error.Severity));
            Assert.StartsWith("The model settings stop the run at start: Llm:Provider names no provider of this host: write mock or the key of a provider module", errors[0].Text, StringComparison.Ordinal);
            Assert.True(launcher.HasBlockingErrors(dryRun));
        }
    }

    [Fact]
    public void A_provider_the_start_accepts_and_a_file_the_cli_resolves_say_nothing()
    {
        Assert.DoesNotContain(
            Pin("""{ "Llm": { "Provider": "mock", "Model": "mock", "Responses": "/r.jsonl" } }""").Validate(),
            m => m.Code == ValidationCodes.LlmProvider);
        Assert.DoesNotContain(Pin(settingsJson: null).Validate(), m => m.Code == ValidationCodes.LlmProvider);
    }
}
