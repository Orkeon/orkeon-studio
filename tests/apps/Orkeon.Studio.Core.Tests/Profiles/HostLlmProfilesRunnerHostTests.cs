using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkeon.Application.Interfaces.Ports;
using Orkeon.Domain.FileSystem;
using Orkeon.Hosting;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Core.Storage;
using Orkeon.Studio.Core.Tests.Doubles;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Tests.Profiles;

/// <summary>
/// STUDIO-48, end to end: a settings file Studio wrote — its model settings mirrored into
/// <c>Llm:Profiles</c>, an entry written by hand beside them, the RAG on one of them — passes the
/// profile validation the real runner host runs at start (<c>LlmSettings</c>, GAP-17, and the RAG
/// profile check of GAP-19), and every profile is built on the key the environment carries: the
/// one a Studio launch lays over its child, or — STUDIO-49 — the variable Studio remembers it in,
/// which the file names, for a terminal run or a scheduled team. A team launched on another
/// setting than the default never receives the default's key.
/// <para>
/// The variables these tests set carry a unique name (<c>ORKEON_TEST_&lt;guid&gt;</c>), which the
/// test's setting takes as its <see cref="ModelProfile.KeyEnvName"/>: the suites run in parallel.
/// </para>
/// </summary>
public sealed class HostLlmProfilesRunnerHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkeon-studio-host-profiles-" + Guid.NewGuid().ToString("N"));

    public HostLlmProfilesRunnerHostTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static readonly ModelProfile DeepSeek = new()
    {
        Name = "DeepSeek",
        Provider = "DeepSeek",
        BaseUrl = "https://api.deepseek.com",
        Model = "deepseek-v4-flash",
        KeyEnvName = "DEEPSEEK_API_KEY",
        TimeoutSeconds = 600,
    };

    private static readonly ModelProfile Zai = new()
    {
        Name = "Z.AI",
        Provider = "Z.AI",
        BaseUrl = "https://api.z.ai/api/paas/v4",
        Model = "glm-5.2",
        KeyEnvName = "ZAI_API_KEY",
        ThinkingEnabled = false,
    };

    private static readonly ModelProfile Ollama = new()
    {
        Name = "Local",
        Provider = "Ollama",
        BaseUrl = "http://localhost:11434",
        Model = "qwen3",
    };

    private static readonly ModelProfile Docker = new()
    {
        Name = "Docker",
        Provider = "docker-model-runner",
        BaseUrl = "http://localhost:12434/engines/llama.cpp/v1",
        Model = "ai/granite-4.0-h-tiny",
    };

    private static string UniqueVariable() => "ORKEON_TEST_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Writes the settings file Studio writes once <paramref name="settings"/> exist, the first one
    /// elected default — its whole entry in <c>Llm</c> (STUDIO-49) — and returns its path.
    /// </summary>
    private async Task<(string Path, ModelProfileSet Set)> WriteElectedSettingsAsync(params ModelProfile[] settings)
    {
        var document = AppSettingsDocument.Parse("""{ "RaggableTree": { "Enabled": false } }""");
        var set = settings.Aggregate(ModelProfileSet.Empty, (current, setting) => current.Upsert(setting));
        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);
        HostLlmProfiles.ElectDefault(document, set.Default!);

        var path = Path.Combine(_root, AppSettingsDocument.FileName);
        await AppSettingsFile.SaveAsync(document, path, TestContext.Current.CancellationToken);
        KeyTripwire.AssertNamesNoKey(
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken),
            [.. settings.Select(setting => setting.KeyEnvName).OfType<string>()]);
        return (path, set);
    }

    /// <summary>Writes the settings file the way Studio does, and returns its path.</summary>
    private async Task<(string Path, ModelProfileSet Set)> WriteStudioSettingsAsync()
    {
        var document = AppSettingsDocument.Parse("""
            {
              "Llm": {
                "BaseUrl": "http://localhost:11434", "Model": "qwen3",
                "Profiles": { "local-gpu": { "BaseUrl": "http://localhost:11500", "Model": "qwen3:32b" } }
              },
              "RaggableTree": { "Enabled": false }
            }
            """);
        var set = ModelProfileSet.Empty.Upsert(DeepSeek).Upsert(Zai);
        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);
        document.Rag.LlmProfile = "z-ai";

        var path = Path.Combine(_root, AppSettingsDocument.FileName);
        await AppSettingsFile.SaveAsync(document, path, TestContext.Current.CancellationToken);
        return (path, set);
    }

    /// <summary>
    /// Builds the runner host over <paramref name="settingsPath"/> with <paramref name="environment"/>
    /// laid last, as the child process reads it: <c>ORKEON_A__B</c> is the key <c>A:B</c> — the
    /// .NET rule <c>AddEnvironmentVariables("ORKEON_")</c> applies, which cannot be fed a dictionary.
    /// </summary>
    private IHost Build(string settingsPath, IReadOnlyDictionary<string, string> environment)
    {
        var overlay = environment.ToDictionary(
            entry => entry.Key["ORKEON_".Length..].Replace("__", ":", StringComparison.Ordinal),
            entry => (string?)entry.Value,
            StringComparer.OrdinalIgnoreCase);

        return RunnerHost.Build(
            settingsPath,
            new RunnerMountPlan
            {
                CliMounts = [$"{FileSystemMount.Quote(_root)}:/crew:ro"],
                AllowExternalMounts = true,
            },
            configureLogging: (_, logging) => logging.SetMinimumLevel(LogLevel.None),
            configureBuilder: builder => builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(overlay)));
    }

    [Fact]
    public async Task A_file_studio_wrote_starts_the_host_and_a_studio_launch_brings_each_profile_its_key()
    {
        var (settingsPath, set) = await WriteStudioSettingsAsync();
        KeyTripwire.AssertNamesNoKey(
            await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken), "DEEPSEEK_API_KEY", "ZAI_API_KEY");
        var keys = new FakeApiKeyStore();
        keys.Stage("DEEPSEEK_API_KEY", "sk-ds");
        keys.Stage("ZAI_API_KEY", "sk-zai");

        using var host = Build(settingsPath, HostLlmProfiles.LaunchEnvironment(set, keys.Peek));

        var profiles = host.Services.GetRequiredService<ILlmProfileRegistry>();
        // The configuration binder lists a section's children sorted by key.
        Assert.Equal(["deepseek", "local-gpu", "z-ai"], profiles.Names);
        var deepseek = profiles.Resolve("deepseek").Provider.BaseConfig!;
        Assert.Equal("sk-ds", deepseek.ApiKey);
        Assert.Equal("deepseek-v4-flash", deepseek.Model);
        Assert.Equal(new Uri("https://api.deepseek.com"), deepseek.BaseUrl);
        Assert.Equal(600, deepseek.TimeoutSeconds);
        var zai = profiles.Resolve("z-ai").Provider.BaseConfig!;
        Assert.Equal("sk-zai", zai.ApiKey);
        Assert.False(zai.Thinking?.Enabled);
        // The entry written by hand is offered beside them, untouched.
        Assert.Equal("qwen3:32b", profiles.Resolve("local-gpu").Provider.BaseConfig!.Model);
    }

    [Fact]
    public async Task A_terminal_run_on_the_same_file_needs_only_the_variable_studio_remembers_each_key_in()
    {
        // STUDIO-49: no ORKEON_Llm__* at all — a terminal, a scheduled team: only the provider's
        // variables, where Studio remembers the keys, which the file names.
        var deepseekVariable = UniqueVariable();
        var zaiVariable = UniqueVariable();
        var (settingsPath, _) = await WriteElectedSettingsAsync(
            DeepSeek with { KeyEnvName = deepseekVariable }, Zai with { KeyEnvName = zaiVariable });
        Environment.SetEnvironmentVariable(deepseekVariable, "sk-ds-terminal");
        Environment.SetEnvironmentVariable(zaiVariable, "sk-zai-terminal");
        try
        {
            using var host = Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal));

            var profiles = host.Services.GetRequiredService<ILlmProfileRegistry>();
            var deepseek = profiles.Resolve("deepseek").Provider.BaseConfig!;
            Assert.Equal("sk-ds-terminal", deepseek.ApiKey);
            Assert.Equal("deepseek-v4-flash", deepseek.Model);
            Assert.Equal(new Uri("https://api.deepseek.com"), deepseek.BaseUrl);
            Assert.Equal("sk-zai-terminal", profiles.Resolve("z-ai").Provider.BaseConfig!.ApiKey);

            // The default is the election, written whole: its key the same way, and its 600 s.
            var elected = profiles.Resolve(null).Provider.BaseConfig!;
            Assert.Equal("sk-ds-terminal", elected.ApiKey);
            Assert.Equal(new Uri("https://api.deepseek.com"), elected.BaseUrl);
            Assert.Equal(600, elected.TimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable(deepseekVariable, null);
            Environment.SetEnvironmentVariable(zaiVariable, null);
        }
    }

    /// <summary>
    /// Decision 4, with the real runner host: a team launched on another setting than the default
    /// lays every default field, blank when its setting leaves it unset — the default's key and
    /// the variable holding it first. A team on Z.AI whose key is not remembered fails without a
    /// key instead of sending the DeepSeek key of the default to Z.AI.
    /// </summary>
    [Theory]
    [InlineData("Z.AI", false)]
    [InlineData("Local", false)]
    [InlineData("Z.AI", true)]
    public async Task A_team_launched_on_another_setting_never_receives_the_key_of_the_default(string teamSetting, bool keyRemembered)
    {
        var deepseekVariable = UniqueVariable();
        // Unique, and never set: a machine that really holds ZAI_API_KEY must not answer for Studio.
        var zaiVariable = UniqueVariable();
        var (settingsPath, set) = await WriteElectedSettingsAsync(
            DeepSeek with { KeyEnvName = deepseekVariable }, Zai with { KeyEnvName = zaiVariable }, Ollama);
        // The default's key, where Studio remembers it: in every process of the user.
        Environment.SetEnvironmentVariable(deepseekVariable, "sk-ds-default");
        try
        {
            var keys = new FakeApiKeyStore();
            keys.Stage(deepseekVariable, "sk-ds-default");
            if (keyRemembered)
                keys.Stage(zaiVariable, "sk-zai");
            var team = set.Find(teamSetting)!;
            var launch = new Dictionary<string, string>(HostLlmProfiles.LaunchEnvironment(set, keys.Peek), StringComparer.Ordinal);
            foreach (var (key, value) in team.EnvironmentOverrides(keys.Peek))
                launch[key] = value;

            using var host = Build(settingsPath, launch);

            var runsOn = host.Services.GetRequiredService<ILlmProfileRegistry>().Resolve(null).Provider.BaseConfig!;
            Assert.Equal(new Uri(team.BaseUrl!), runsOn.BaseUrl);
            Assert.Equal(team.Model, runsOn.Model);
            Assert.Equal(keyRemembered ? "sk-zai" : null, runsOn.ApiKey);
            // What the team's setting leaves unset is not the default's either: no 600 s.
            Assert.Null(runsOn.TimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable(deepseekVariable, null);
        }
    }

    /// <summary>
    /// STUDIO-54, decision 4, with the real runner host: the run reads Docker Model Runner as
    /// OpenAI, whose dialect refuses to call without a key. On the file Studio writes, the elected
    /// default and the <c>docker</c> profile are built with the placeholder <c>not-needed</c> — no
    /// variable anywhere: a terminal, a scheduled team.
    /// </summary>
    [Fact]
    public async Task A_docker_model_runner_setting_runs_from_the_file_alone_with_its_placeholder_key()
    {
        var deepseekVariable = UniqueVariable();
        var (settingsPath, _) = await WriteElectedSettingsAsync(Docker, DeepSeek with { KeyEnvName = deepseekVariable });

        using var host = Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal));

        var profiles = host.Services.GetRequiredService<ILlmProfileRegistry>();
        Assert.Equal("not-needed", profiles.Resolve("docker").Provider.BaseConfig!.ApiKey);
        var elected = profiles.Resolve(null).Provider.BaseConfig!;
        Assert.Equal(new Uri(Docker.BaseUrl!), elected.BaseUrl);
        Assert.Equal("not-needed", elected.ApiKey);
    }

    /// <summary>A team launched on the Docker Model Runner setting in place of the elected DeepSeek carries the placeholder too.</summary>
    [Fact]
    public async Task A_team_launched_on_a_docker_model_runner_setting_carries_its_placeholder_key()
    {
        var deepseekVariable = UniqueVariable();
        var (settingsPath, set) = await WriteElectedSettingsAsync(DeepSeek with { KeyEnvName = deepseekVariable }, Docker);
        Environment.SetEnvironmentVariable(deepseekVariable, "sk-ds-default");
        try
        {
            var keys = new FakeApiKeyStore();
            keys.Stage(deepseekVariable, "sk-ds-default");
            var launch = new Dictionary<string, string>(HostLlmProfiles.LaunchEnvironment(set, keys.Peek), StringComparer.Ordinal);
            foreach (var (key, value) in set.Find("Docker")!.EnvironmentOverrides(keys.Peek))
                launch[key] = value;

            using var host = Build(settingsPath, launch);

            var runsOn = host.Services.GetRequiredService<ILlmProfileRegistry>().Resolve(null).Provider.BaseConfig!;
            Assert.Equal(new Uri(Docker.BaseUrl!), runsOn.BaseUrl);
            Assert.Equal("not-needed", runsOn.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(deepseekVariable, null);
        }
    }

    /// <summary>Values of the <c>Llm</c> shape the real host refuses at start (GAP-40), at their path.</summary>
    public static TheoryData<string, string> ValuesTheHostRefuses() => new()
    {
        { "Llm:Profiles:local-gpu:Temperature", "\"warm\"" },
        { "Llm:Profiles:local-gpu:MaxTokens", "600.0" },
        { "Llm:Profiles:local-gpu:TimeoutSeconds", "\"600s\"" },
        { "Llm:Profiles:local-gpu:StreamIdleSeconds", "\"45s\"" },
        { "Llm:Profiles:local-gpu:MaxRetries", "1.5" },
        { "Llm:Profiles:local-gpu:BaseUrl", "\"not a url\"" },
        { "Llm:Profiles:local-gpu:ApiKeyEnvVar", "\"MY KEY\"" },
        { "Llm:Profiles:local-gpu:ApiKey", "\"${LOCAL_KEY}\"" },
        { "Llm:Profiles:local-gpu:Grammar", "\"yes\"" },
        { "Llm:Temperature", "\"Infinity\"" },
        { "Llm:MaxTokens", "\"4096x\"" },
        { "Llm:TimeoutSeconds", "600.5" },
        { "Llm:StreamIdleSeconds", "45.5" },
        { "Llm:ApiKey", "\"${OPENAI_API_KEY}\"" },
        { "Llm:Thinking:Enabled", "\"on\"" },
        // The run does not trim an effort: a padded level is no level.
        { "Llm:Thinking:Effort", "\" high\"" },
        // A list or an object that holds anything, even a null or an empty one, under a single value.
        { "Llm:Thinking:Effort", "[\"high\"]" },
        { "Llm:Thinking:Effort", "{ \"level\": \"high\" }" },
        { "Llm:Thinking:Effort", "[[]]" },
        { "Llm:Thinking:Effort", "{ \"a\": {} }" },
        { "Llm:Thinking:Effort", "[null]" },
        { "Llm:Model", "[\"qwen3\"]" },
        { "Llm:BaseUrl", "{ \"a\": \"http://localhost:11434\" }" },
        { "Llm:ApiKey", "[\"k\"]" },
        { "Llm:ApiKeyEnvVar", "[\"MY_KEY\"]" },
        { "Llm:MaxTokens", "[4096]" },
        { "Llm:Temperature", "{ \"a\": 0.5 }" },
        { "Llm:Thinking:Enabled", "[true]" },
        { "Llm:Profiles:local-gpu:Model", "{ \"a\": \"qwen3\" }" },
        { "Llm:Profiles:local-gpu:Thinking:Effort", "[\"high\"]" },
        { "Llm:ModelCapabilities:gpt-4o:Thinking", "\"Always\"" },
        { "Llm:ModelCapabilities:gpt-4o:Vision", "\"yes\"" },
        // A switch and Thinking are single values: a list or an object that holds anything is refused.
        { "Llm:ModelCapabilities:gpt-4o:Thinking", "[\"Toggle\"]" },
        { "Llm:ModelCapabilities:gpt-4o:Thinking", "{ \"Level\": \"Toggle\" }" },
        { "Llm:ModelCapabilities:gpt-4o:Vision", "[true]" },
        { "Llm:ModelCapabilities:gpt-4o:AudioInput", "[null]" },
        { "Llm:ModelCapabilities:gpt-4o:AudioOutput", "{ \"a\": true }" },
        // An entry carries its five keys and no other.
        { "Llm:ModelCapabilities:gpt-4o:Thinkng", "\"Toggle\"" },
        { "Llm:ModelCapabilities:gpt-4o:Thinkng", "{}" },
        { "Llm:ModelCapabilities:gpt-4o:ReasoningEfforts", "[[\"low\"]]" },
        { "Llm:ModelCapabilities:gpt-4o:ReasoningEfforts", "[\"low\", { \"a\": \"high\" }]" },
    };

    /// <summary>
    /// <c>Llm:ModelCapabilities</c> written in a way the real host refuses at start, with the path
    /// Studio reports it at: a model id holding a ':', an entry or the section written as a list.
    /// </summary>
    public static TheoryData<string, string> ModelCapabilitiesTheHostRefuses() => new()
    {
        { """{ "llama3.2:latest": { "Thinking": "None" } }""", "Llm:ModelCapabilities:llama3.2:latest" },
        { """{ "llama3.2:latest": {} }""", "Llm:ModelCapabilities:llama3.2:latest" },
        { """{ "gpt-4o": ["x"] }""", "Llm:ModelCapabilities:gpt-4o:0" },
        { """{ "gpt-4o": { "Thinking:Level": "Toggle" } }""", "Llm:ModelCapabilities:gpt-4o:Thinking" },
        { """[ { "Thinking": "Always" } ]""", "Llm:ModelCapabilities:0:Thinking" },
        { """[ ["x"] ]""", "Llm:ModelCapabilities:0:0" },
        // Two keys that come to one same path, a value on both sides: the file does not load.
        { """{ "gpt-4o": { "Vision": true, "vision": false } }""", "Llm:ModelCapabilities:gpt-4o:Vision" },
        { """{ "llama3.2:Vision": true, "llama3.2": { "Vision": false } }""", "Llm:ModelCapabilities:llama3.2:Vision" },
        { """{ "x": { "ReasoningEfforts": ["a"], "reasoningefforts": ["b"] } }""", "Llm:ModelCapabilities:x:ReasoningEfforts:0" },
        { """{ "gpt-4o": { "Thinking": "Toggle" }, "GPT-4O": { "thinking": "None" } }""", "Llm:ModelCapabilities:gpt-4o:Thinking" },
        { """{ "gpt-4o": "x", "GPT-4O": "y" }""", "Llm:ModelCapabilities:gpt-4o" },
        // A null is a value; an empty list or object holds the key, and the value after it is refused.
        { """{ "gpt-4o": { "Vision": null, "vision": true } }""", "Llm:ModelCapabilities:gpt-4o:Vision" },
        { """{ "gpt-4o": { "Vision": true, "vision": null } }""", "Llm:ModelCapabilities:gpt-4o:Vision" },
        { """{ "gpt-4o": { "Vision": {}, "vision": true } }""", "Llm:ModelCapabilities:gpt-4o:Vision" },
        { """{ "gpt-4o": { "Vision": [], "vision": true } }""", "Llm:ModelCapabilities:gpt-4o:Vision" },
    };

    /// <summary>
    /// <c>Llm:ModelCapabilities</c> keyed by a blank model id, with the path Studio reports it at. The
    /// real host does not start on it: since Orkeon 1.2.0 (#385) the settings reader refuses the entry
    /// (<c>LlmSettings.ReadModelCapabilities</c>) and the host reports it as a settings refusal that
    /// names <c>Llm:ModelCapabilities</c>, where 1.1.0 threw the model table's own exception.
    /// </summary>
    public static TheoryData<string, string> BlankModelIds() => new()
    {
        { """{ "": { "Vision": true } }""", "Llm:ModelCapabilities:" },
        { """{ " ": { "Vision": true } }""", "Llm:ModelCapabilities: " },
        { """{ "": {} }""", "Llm:ModelCapabilities:" },
        { """{ " ": "x" }""", "Llm:ModelCapabilities: " },
        { """{ "": null }""", "Llm:ModelCapabilities:" },
        { """{ ":Vision": true }""", "Llm:ModelCapabilities:" },
        { """{ "gpt-4o": { "Vision": true }, "  ": { "Thinking": "None" } }""", "Llm:ModelCapabilities:  " },
    };

    /// <summary>A blank model id stops the real host at start, and Studio's check before saving.</summary>
    [Theory]
    [MemberData(nameof(BlankModelIds))]
    public async Task A_blank_model_id_stops_the_host_at_start_and_studio_refuses_it_before_saving(string json, string path)
    {
        var (document, settingsPath) = await WriteModelCapabilitiesAsync(json);

        // A settings refusal of the host (Orkeon #385, from 1.2.0 on), never another exception.
        var refused = Assert.Throws<RunnerSettingsException>(() => Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal)));
        // The refusal of the blank id, not another one the key could earn.
        Assert.Contains("Llm:ModelCapabilities", refused.Message, StringComparison.Ordinal);
        Assert.Contains("model id", refused.Message, StringComparison.Ordinal);
        var message = Assert.Single(
            new AppSettingsValidator(new FakeDirectoryProbe()).Validate(document),
            message => message.Path?.StartsWith("Llm:ModelCapabilities", StringComparison.Ordinal) == true);
        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal(path, message.Path);
    }

    /// <summary>The drift guard of <c>Llm:ModelCapabilities</c> for what a path of the document cannot write.</summary>
    [Theory]
    [MemberData(nameof(ModelCapabilitiesTheHostRefuses))]
    public async Task Model_capabilities_the_host_refuses_at_start_studio_refuses_before_saving(string json, string path)
    {
        var (document, settingsPath) = await WriteModelCapabilitiesAsync(json);

        // The refusal is the one of that key, not another the file would earn: it names the path
        // Studio reports (keys are compared without case, and the host spells the one it met).
        var refused = Assert.Throws<RunnerSettingsException>(() => Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Contains(path, refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            new AppSettingsValidator(new FakeDirectoryProbe()).Validate(document),
            message => message.Severity == ValidationSeverity.Error && message.Path == path);
    }

    /// <summary><c>Llm:ModelCapabilities</c> written in a way the real host starts on.</summary>
    public static TheoryData<string> ModelCapabilitiesTheHostAccepts() => new()
    {
        // Left out, each in its own way: an empty list, an empty object, a null, a blank.
        """{ "gpt-4o": { "Thinking": [], "Vision": {}, "AudioInput": null, "AudioOutput": " ", "ReasoningEfforts": [] } }""",
        """{ "gpt-4o": { "Thinking": {}, "Vision": [] } }""",
        // An entry that holds nothing to judge.
        """{ "gpt-4o": "x" }""",
        """{ "gpt-4o": [] }""",
        """{ "gpt-4o": {} }""",
        """[ "x" ]""",
        """[ { "Thinking": "Toggle" } ]""",
        // Vendor efforts are the run's to send; a list may be written as an object, and its items left empty.
        """{ "gpt-4o": { "ReasoningEfforts": ["none", "minimal", 3] } }""",
        """{ "gpt-4o": { "ReasoningEfforts": "low, high" } }""",
        """{ "gpt-4o": { "ReasoningEfforts": { "a": "low" } } }""",
        """{ "gpt-4o": { "ReasoningEfforts": [[], {}, null] } }""",
        """{ "gpt-4o": { "ReasoningEfforts:0": "low" } }""",
        // Keys are read without case, and a ':' that lands on a key of the entry is that key.
        """{ "gpt-4o": { "thinking": "toggle", "VISION": true } }""",
        """{ "llama3.2:Vision": true }""",
        """{ "mammouth/gemini-3.7": { "Thinking": "EffortOnly" }, "llama3.2": { "Thinking": "None" } }""",
        // Two keys that come to one same path and set no same key below: read as one entry.
        """{ "gpt-4o": { "Vision": true }, "GPT-4O": { "AudioInput": true } }""",
        """{ "llama3.2:Vision": true, "llama3.2": { "AudioInput": false } }""",
        """{ "x": { "ReasoningEfforts": ["a"], "reasoningefforts": { "1": "b" } } }""",
        // An empty list or object after a value replaces it with none, without a refusal.
        """{ "gpt-4o": { "Vision": true, "vision": {} } }""",
        """{ "gpt-4o": { "Vision": "yes", "vision": [] } }""",
        """{ "gpt-4o": { "Vision": {}, "vision": [] } }""",
        """{ "gpt-4o": { "Vision": true }, "gpt-4o:Vision": {} }""",
    };

    /// <summary>The other side of the drift guard: Studio reports nothing on what the real host starts on.</summary>
    [Theory]
    [MemberData(nameof(ModelCapabilitiesTheHostAccepts))]
    public async Task Model_capabilities_the_host_starts_on_studio_does_not_refuse(string json)
    {
        var (document, settingsPath) = await WriteModelCapabilitiesAsync(json);

        using var host = Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.DoesNotContain(
            new AppSettingsValidator(new FakeDirectoryProbe()).Validate(document),
            message => message.Path?.StartsWith("Llm:ModelCapabilities", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Keys of the <c>Llm</c> shape written as an empty list or object, which the real host reads as
    /// left out: the <c>Llm</c> section, then the same keys in a profile.
    /// </summary>
    public static TheoryData<string> EmptyContainersTheHostStartsOn()
    {
        string[] keys =
        [
            """ "Model": [] """, """ "Model": {} """,
            """ "BaseUrl": [] """, """ "BaseUrl": {} """,
            """ "ApiKey": [] """, """ "ApiKey": {} """,
            """ "ApiKeyEnvVar": [] """, """ "ApiKeyEnvVar": {} """,
            """ "Thinking": { "Effort": [] } """, """ "Thinking": { "Effort": {} } """,
            """ "Thinking": [] """, """ "Thinking": { "Enabled": [], "Effort": "high" } """,
            """ "Temperature": [], "MaxTokens": {}, "TimeoutSeconds": [], "StreamIdleSeconds": {}, "MaxRetries": [], "Grammar": {} """,
        ];
        var data = new TheoryData<string>();
        foreach (var key in keys)
        {
            data.Add("{" + Beside(key, "http://localhost:11434", "qwen3") + key + "}");
            data.Add("""{ "BaseUrl": "http://localhost:11434", "Model": "qwen3", "Profiles": { "local-gpu": {"""
                + Beside(key, "http://localhost:11500", "qwen3:32b") + key + "} } }");
        }

        return data;
    }

    /// <summary>The address and the model of a section, each left to <paramref name="key"/> when it writes that one.</summary>
    private static string Beside(string key, string baseUrl, string model) =>
        (key.Contains("\"BaseUrl\"", StringComparison.Ordinal) ? string.Empty : $" \"BaseUrl\": \"{baseUrl}\",")
        + (key.Contains("\"Model\"", StringComparison.Ordinal) ? string.Empty : $" \"Model\": \"{model}\",");

    /// <summary>
    /// The other side of the drift guard of the <c>Llm</c> shape: an empty list or object under a key
    /// the run reads as a single value is a key left out, the real host starts, and Studio reports nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmptyContainersTheHostStartsOn))]
    public async Task An_empty_list_or_object_under_a_key_of_the_llm_shape_starts_the_host_and_studio_does_not_refuse_it(string llm)
    {
        var json = """{ "Llm": """ + llm + """, "RaggableTree": { "Enabled": false } }""";
        var settingsPath = Path.Combine(_root, AppSettingsDocument.FileName);
        await File.WriteAllTextAsync(settingsPath, json, TestContext.Current.CancellationToken);

        using var host = Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.DoesNotContain(
            new AppSettingsValidator(new FakeDirectoryProbe()).Validate(AppSettingsDocument.Parse(json)),
            message => message.Severity == ValidationSeverity.Error);
    }

    /// <summary>An effort made of whitespace alone is unset for the real host, and for Studio's check.</summary>
    [Fact]
    public async Task A_whitespace_effort_starts_the_host_and_studio_does_not_refuse_it()
    {
        var document = AppSettingsDocument.Parse("""
            {
              "Llm": { "BaseUrl": "http://localhost:11434", "Model": "qwen3", "Thinking": { "Effort": "   " } },
              "RaggableTree": { "Enabled": false }
            }
            """);
        var settingsPath = Path.Combine(_root, AppSettingsDocument.FileName);
        await AppSettingsFile.SaveAsync(document, settingsPath, TestContext.Current.CancellationToken);

        using var host = Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.DoesNotContain(
            new AppSettingsValidator(new FakeDirectoryProbe()).Validate(document),
            message => message.Path == "Llm:Thinking:Effort");
    }

    private async Task<(AppSettingsDocument Document, string SettingsPath)> WriteModelCapabilitiesAsync(string json)
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "BaseUrl": "http://localhost:11434", "Model": "qwen3", "ModelCapabilities": """
            + json
            + """ }, "RaggableTree": { "Enabled": false } }""");
        var settingsPath = Path.Combine(_root, AppSettingsDocument.FileName);
        await AppSettingsFile.SaveAsync(document, settingsPath, TestContext.Current.CancellationToken);
        return (document, settingsPath);
    }

    /// <summary>
    /// STUDIO-55, the drift guard: each value of <c>Llm</c> or of an entry of <c>Llm:Profiles</c> that
    /// the real runner host refuses at start is refused by Studio's check before saving, at the same
    /// path — Studio.Core copies the run's rules, and this test holds the copy to the original.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValuesTheHostRefuses))]
    public async Task What_the_host_refuses_at_start_studio_refuses_before_saving_at_the_same_path(string path, string json)
    {
        var document = AppSettingsDocument.Parse("""
            {
              "Llm": {
                "BaseUrl": "http://localhost:11434", "Model": "qwen3",
                "Profiles": { "local-gpu": { "BaseUrl": "http://localhost:11500", "Model": "qwen3:32b" } }
              },
              "RaggableTree": { "Enabled": false }
            }
            """);
        document.SetNode(path, System.Text.Json.Nodes.JsonNode.Parse(json));
        var settingsPath = Path.Combine(_root, AppSettingsDocument.FileName);
        await AppSettingsFile.SaveAsync(document, settingsPath, TestContext.Current.CancellationToken);

        var refused = Assert.Throws<RunnerSettingsException>(() => Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Contains(path, refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            new AppSettingsValidator(new FakeDirectoryProbe()).Validate(document),
            message => message.Severity == ValidationSeverity.Error && message.Path == path);
    }

    /// <summary>The reserved name: an entry <c>default</c> written by hand refuses the host, and Studio's check.</summary>
    [Fact]
    public async Task An_entry_named_default_refuses_the_host_and_studio_refuses_it_before_saving()
    {
        var document = AppSettingsDocument.Parse("""
            {
              "Llm": {
                "BaseUrl": "http://localhost:11434", "Model": "qwen3",
                "Profiles": { "default": { "BaseUrl": "http://localhost:11500", "Model": "qwen3:32b" } }
              },
              "RaggableTree": { "Enabled": false }
            }
            """);
        var settingsPath = Path.Combine(_root, AppSettingsDocument.FileName);
        await AppSettingsFile.SaveAsync(document, settingsPath, TestContext.Current.CancellationToken);

        Assert.Throws<RunnerSettingsException>(() => Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Contains(
            new AppSettingsValidator(new FakeDirectoryProbe()).Validate(document),
            message => message.Code == ValidationCodes.LlmProfileReservedName && message.Path == "Llm:Profiles:default");
    }

    /// <summary>One row per entry of the list, the two API keys included, the daemon's paths too.</summary>
    public static TheoryData<string> PlaceholderPaths()
    {
        var data = new TheoryData<string>();
        foreach (var path in AppSettingsValidatorTests.PlaceholderPaths())
            data.Add(path);
        return data;
    }

    /// <summary>CH-15: the same reference at the same path is refused by the real host and by Studio's check.</summary>
    [Theory]
    [Trait("BR", "CH-15")]
    [MemberData(nameof(PlaceholderPaths))]
    public async Task ShouldRefuseTheSameValueAtTheSamePath_WhenStudioAndTheHostJudgeIt(string path)
    {
        var json = AppSettingsValidatorTests.NestedJson(path, "${X}");
        var document = AppSettingsDocument.Parse(json);
        var settingsPath = Path.Combine(_root, AppSettingsDocument.FileName);
        await File.WriteAllTextAsync(settingsPath, json, TestContext.Current.CancellationToken);

        var refused = Assert.Throws<RunnerSettingsException>(() => Build(settingsPath, new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Contains(path, refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            new AppSettingsValidator(new FakeDirectoryProbe()).Validate(document),
            message => message.Severity == ValidationSeverity.Error
                && string.Equals(message.Path, path, StringComparison.OrdinalIgnoreCase));
    }
}
