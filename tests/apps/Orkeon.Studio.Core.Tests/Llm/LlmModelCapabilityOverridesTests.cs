using System.Text.Json;
using Orkeon.Constants.Llm;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Infrastructure.LLMs.Capabilities;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Tests.Doubles;

namespace Orkeon.Studio.Core.Tests.Llm;

/// <summary>
/// The connection probe honours the <c>Llm:ModelCapabilities</c> overrides of the settings file: read
/// from the document as the run reads them, laid over the built-in rows as the run's
/// <c>ModelCapabilityRegistry</c> lays them - held to the real registry here -, and carried by the
/// test completion.
/// </summary>
public sealed class LlmModelCapabilityOverridesTests
{
    private static AppSettingsDocument Document(string modelCapabilitiesJson) =>
        AppSettingsDocument.Parse("{ \"Llm\": { \"Model\": \"m\", \"ModelCapabilities\": " + modelCapabilitiesJson + " } }");

    /// <summary><c>id=Support;id=</c>: an id with nothing after its <c>=</c> leaves <c>Thinking</c> out.</summary>
    private static Dictionary<string, ThinkingSupport?> Overrides(string written)
    {
        var overrides = new Dictionary<string, ThinkingSupport?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in written.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var value = pair[(separator + 1)..];
            overrides[pair[..separator]] = value.Length == 0 ? null : Enum.Parse<ThinkingSupport>(value);
        }

        return overrides;
    }

    [Fact]
    public void The_reader_takes_every_entry_with_the_thinking_support_it_names()
    {
        var overrides = LlmModelCapabilityOverrides.ThinkingOf(Document("""
            {
              "glm-5.3": { "Thinking": "toggle" },
              "mammouth/gemini-3.7": { "Thinking": " Budget " },
              "gpt-4o-mini": { "Vision": true },
              "acme": "x"
            }
            """));

        Assert.Equal(4, overrides.Count);
        Assert.Equal(ThinkingSupport.Toggle, overrides["GLM-5.3"]);
        Assert.Equal(ThinkingSupport.Budget, overrides["mammouth/gemini-3.7"]);
        Assert.Null(overrides["gpt-4o-mini"]);
        Assert.Null(overrides["acme"]);
    }

    [Theory]
    // What the run refuses at start, or reads as left out, names no support here.
    [InlineData("\"Always\"")]
    [InlineData("\"EffortOnly,Toggle\"")]
    [InlineData("1")]
    [InlineData("\"  \"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"Toggle\"]")]
    [InlineData("{ \"Level\": \"Toggle\" }")]
    public void The_reader_reads_an_invalid_or_blank_thinking_as_left_out(string thinking)
    {
        var overrides = LlmModelCapabilityOverrides.ThinkingOf(Document("{ \"glm-5.3\": { \"Thinking\": " + thinking + " } }"));

        Assert.Null(Assert.Single(overrides).Value);
    }

    [Fact]
    public void The_reader_splits_an_id_at_its_colon_as_the_run_does()
    {
        var overrides = LlmModelCapabilityOverrides.ThinkingOf(Document("""{ "llama3.2:Thinking": "None", "qwen3:latest": { "Thinking": "None" } }"""));

        Assert.Equal(ThinkingSupport.None, overrides["llama3.2"]);
        Assert.Null(overrides["qwen3"]);
        Assert.Equal(2, overrides.Count);
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("""{ "Llm": { "Model": "m" } }""")]
    [InlineData("""{ "Llm": { "ModelCapabilities": {} } }""")]
    [InlineData("""{ "Llm": { "ModelCapabilities": "x" } }""")]
    [InlineData("""{ "Llm": { "Profiles": { "p": { "ModelCapabilities": { "glm-5.3": { "Thinking": "Toggle" } } } } } }""")]
    public void The_reader_finds_no_override_outside_the_top_level_section(string json)
    {
        Assert.Empty(LlmModelCapabilityOverrides.ThinkingOf(AppSettingsDocument.Parse(json)));
    }

    [Fact]
    public void The_reader_follows_the_document_as_it_changes()
    {
        var document = Document("""{ "glm-5.3": { "Thinking": "Toggle" } }""");
        Assert.Equal(ThinkingSupport.Toggle, LlmModelCapabilityOverrides.ThinkingOf(document)["glm-5.3"]);

        document.SetNode("Llm:ModelCapabilities:glm-5.3:Thinking", JsonSerializer.SerializeToNode("None"));

        Assert.Equal(ThinkingSupport.None, LlmModelCapabilityOverrides.ThinkingOf(document)["glm-5.3"]);
    }

    /// <summary>
    /// The drift guard of the merge: for each set of overrides, the probe's row is the one the real
    /// registry, built over the same overrides, answers for that model behind that provider.
    /// </summary>
    [Theory]
    // An override under the id of a built-in row replaces it.
    [InlineData("glm-5.3=Toggle", LlmProviderKeys.Zai, "glm-5.3")]
    [InlineData("glm-5.3=Toggle", LlmProviderKeys.Zai, "glm-5.3-flash")]
    [InlineData("gpt-4o=EffortOnly", LlmProviderKeys.OpenAI, "gpt-4o-mini")]
    // One that leaves Thinking out keeps what the built-in rows say of its id.
    [InlineData("gpt-4o-mini=", LlmProviderKeys.OpenAI, "gpt-4o-mini")]
    [InlineData("glm-5.3-flash=", LlmProviderKeys.Zai, "glm-5.3-flash")]
    [InlineData("zai-org/glm-5.3=", LlmProviderKeys.OpenRouter, "zai-org/glm-5.3")]
    [InlineData("acme=", LlmProviderKeys.OpenAI, "acme-1")]
    // <provider>/<model> first, a built-in gateway row included.
    [InlineData("glm-5.2=None;zai/glm-5.2=Toggle", LlmProviderKeys.Zai, "glm-5.2")]
    [InlineData("glm-5.2=None;zai/glm-5.2=Toggle", LlmProviderKeys.DeepSeek, "glm-5.2")]
    [InlineData("gemini-3.7=Toggle", LlmProviderKeys.Mammouth, "gemini-3.7-pro")]
    [InlineData("gemini-3.7=Toggle", LlmProviderKeys.Gemini, "gemini-3.7-pro")]
    [InlineData("glm-5.3=Toggle", LlmProviderKeys.Together, "zai-org/glm-5.3")]
    [InlineData("together/zai-org/glm-5.3=Budget", LlmProviderKeys.Together, "zai-org/glm-5.3")]
    // Then the longest id at a boundary, a ':' included, then the part after the last slash.
    [InlineData("acme=Toggle;acme-1=None", LlmProviderKeys.OpenAI, "acme-1-preview")]
    [InlineData("acme=Toggle;acme-1=None", LlmProviderKeys.OpenAI, "acme-2")]
    [InlineData("acme=Toggle;acme-1=None", LlmProviderKeys.OpenAI, "acme-10")]
    [InlineData("qwen3=None", LlmProviderKeys.Ollama, "qwen3:32b")]
    [InlineData("qwen3=None", LlmProviderKeys.Ollama, "qwen30")]
    [InlineData("acme=None", LlmProviderKeys.OpenRouter, "vendor/acme-1")]
    [InlineData("vendor/acme=Toggle;acme=None", LlmProviderKeys.OpenRouter, "vendor/acme-1")]
    public void The_probe_lays_overrides_over_the_model_table_as_the_runtime_does(string written, string provider, string model)
    {
        var overrides = Overrides(written);
#pragma warning disable ORKEXP005 // The model table is experimental in Orkeon; the probe follows it.
        var registry = new ModelCapabilityRegistry(
            overrides.ToDictionary(entry => entry.Key, entry => new ModelCapabilityOverride { Thinking = entry.Value }));
        var entry = registry.TryGet(model, provider);
#pragma warning restore ORKEXP005

        Assert.Equal(entry?.Thinking, LlmProbeDialect.ModelThinkingOf(provider, model, overrides));
    }

    [Fact]
    public void No_override_leaves_the_built_in_rows_alone()
    {
        Assert.Equal(ThinkingSupport.EffortOnly, LlmProbeDialect.ThinkingSupportOf(LlmProviderKeys.Zai, "glm-5.3", null));
        Assert.Equal(ThinkingSupport.EffortOnly, LlmProbeDialect.ThinkingSupportOf(LlmProviderKeys.Zai, "glm-5.3", LlmModelCapabilityOverrides.None));
        Assert.Same(LlmModelCapabilityOverrides.None, new LlmProbeRequest().ModelThinkingOverrides);
    }

    [Fact]
    public void An_override_never_lifts_a_model_above_its_provider()
    {
        // Mammouth declares effort only: the lower of the two rules, as the runtime judges it.
        var overrides = Overrides("acme=Budget");

        Assert.Equal(ThinkingSupport.EffortOnly, LlmProbeDialect.ThinkingSupportOf(LlmProviderKeys.Mammouth, "acme-1", overrides));
        Assert.Equal(ThinkingSupport.Toggle, LlmProbeDialect.ThinkingSupportOf(LlmProviderKeys.Zai, "acme-1", overrides));
    }

    private static async Task<JsonDocument> CompletionAsync(
        string endpoint, string model, bool thinking, string modelCapabilitiesJson)
    {
        using var handler = new StubHttpMessageHandler { Body = """{ "data": [] }""" };
        using var client = new HttpClient(handler, disposeHandler: false);
        using var probe = new HttpLlmEndpointProbe(client);
        var result = await probe.ProbeAsync(
            new LlmProbeRequest
            {
                BaseUrl = endpoint,
                ApiKey = "k",
                Model = model,
                ThinkingEnabled = thinking,
                CheckCompletion = true,
                ModelThinkingOverrides = LlmModelCapabilityOverrides.ThinkingOf(Document(modelCapabilitiesJson)),
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, handler.Requests.Count);
        return JsonDocument.Parse(handler.Requests[1].Body!);
    }

    [Fact]
    public async Task A_model_the_table_says_always_reasons_overridden_to_toggle_gets_the_off_switch()
    {
        using var builtIn = await CompletionAsync(LlmProviderEndpoints.Zai, "glm-5.3", thinking: false, "{}");
        Assert.False(builtIn.RootElement.TryGetProperty("thinking", out _));

        using var overridden = await CompletionAsync(
            LlmProviderEndpoints.Zai, "glm-5.3", thinking: false, """{ "glm-5.3": { "Thinking": "Toggle" } }""");
        Assert.Equal("disabled", overridden.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_toggle_model_overridden_to_none_gets_no_switch()
    {
        using var builtIn = await CompletionAsync(LlmProviderEndpoints.Zai, "glm-5.2", thinking: false, "{}");
        Assert.Equal("disabled", builtIn.RootElement.GetProperty("thinking").GetProperty("type").GetString());

        using var overridden = await CompletionAsync(
            LlmProviderEndpoints.Zai, "glm-5.2", thinking: false, """{ "glm-5.2": { "Thinking": "None" } }""");
        Assert.False(overridden.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task The_providers_own_entry_wins_over_the_models_and_the_longest_id_over_a_shorter_one()
    {
        using var served = await CompletionAsync(
            LlmProviderEndpoints.Zai, "glm-5.2", thinking: false,
            """{ "glm-5.2": { "Thinking": "None" }, "zai/glm-5.2": { "Thinking": "Toggle" } }""");
        Assert.True(served.RootElement.TryGetProperty("thinking", out _));

        using var longest = await CompletionAsync(
            LlmProviderEndpoints.Zai, "glm-5.2-air", thinking: false,
            """{ "glm": { "Thinking": "Toggle" }, "glm-5.2": { "Thinking": "None" } }""");
        Assert.False(longest.RootElement.TryGetProperty("thinking", out _));

        using var shorter = await CompletionAsync(
            LlmProviderEndpoints.Zai, "glm-5.1", thinking: false,
            """{ "glm": { "Thinking": "Toggle" }, "glm-5.2": { "Thinking": "None" } }""");
        Assert.True(shorter.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task The_other_dialects_follow_the_overrides_too()
    {
        // Anthropic: a model that refuses every off value, declared switchable by the host.
        using var claude = await CompletionAsync(
            LlmProviderEndpoints.Anthropic, "claude-opus-5-5", thinking: false, """{ "claude-opus-5-5": { "Thinking": "Toggle" } }""");
        Assert.Equal("disabled", claude.RootElement.GetProperty("thinking").GetProperty("type").GetString());

        // Ollama: a model with no reasoning pass gets no think field.
        using var ollama = await CompletionAsync(
            LlmProviderEndpoints.OllamaDefault, "llama3.2:latest", thinking: false, """{ "llama3.2": { "Thinking": "None" } }""");
        Assert.False(ollama.RootElement.TryGetProperty("think", out _));
    }
}
