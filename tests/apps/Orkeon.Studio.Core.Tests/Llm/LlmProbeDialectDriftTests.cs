using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Orkeon.Constants.Llm;
using Orkeon.Domain.SharedKernel;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Infrastructure.LLMs;
using Orkeon.Infrastructure.LLMs.Capabilities;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Llm;

namespace Orkeon.Studio.Core.Tests.Llm;

/// <summary>
/// STUDIO-43 writes the probe's test completion in Studio Core, which cannot reference the
/// providers (see its csproj). The thinking level it reads per provider is a copy of what each
/// provider declares in <see cref="LlmProviderCapabilities.Thinking"/> — pinned here against
/// the real providers, built from the same URL the way the runtime builds them.
/// </summary>
public sealed class LlmProbeDialectDriftTests
{
    private sealed class OneClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    public static TheoryData<string> Endpoints() =>
    [
        LlmProviderEndpoints.OpenAI,
        LlmProviderEndpoints.Anthropic,
        LlmProviderEndpoints.DeepSeek,
        LlmProviderEndpoints.Together,
        LlmProviderEndpoints.Qwen,
        LlmProviderEndpoints.Kimi,
        LlmProviderEndpoints.HuggingFace,
        LlmProviderEndpoints.Mistral,
        LlmProviderEndpoints.Zai,
        LlmProviderEndpoints.Gemini,
        LlmProviderEndpoints.Grok,
        LlmProviderEndpoints.MiniMax,
        LlmProviderEndpoints.OpenRouter,
        LlmProviderEndpoints.Mammouth,
        LlmProviderEndpoints.OllamaDefault,
        "https://llm.example.com/v1",   // custom: the runtime drives it as OpenAI
    ];

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void The_probes_thinking_level_is_the_one_the_provider_declares(string endpoint)
    {
        var factory = new LlmProviderFactory(new OneClientFactory(), NullLoggerFactory.Instance, new Orkeon.Tests.Shared.FileSystem.FakeFileSystemService());
        var built = factory.Create(LlmConfig.Create("probe-model", "k") with { BaseUrl = new Uri(endpoint) });
        var declared = built is LlmProviderAdapter adapter
            ? adapter.UnderlyingProvider.Capabilities
            : ((ILlmProvider)built).Capabilities;

        Assert.Equal(declared.Thinking, LlmProbeDialect.ThinkingSupportOf(LlmEndpointReading.Of(endpoint, null, null).ProviderKey));
    }

    /// <summary>
    /// Every id of the runtime's built-in model table, asked as a model of that id (a gateway row
    /// <c>provider/model</c> through its provider): the probe narrows thinking the way the table does.
    /// </summary>
    public static TheoryData<string, string> ModelTableIds()
    {
#pragma warning disable ORKEXP005 // The model table is experimental in Orkeon; the probe follows it.
        var builtIn = typeof(ModelCapabilityRegistry).GetMethod("BuiltIn", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null) as IReadOnlyDictionary<string, ModelCapabilities>;
#pragma warning restore ORKEXP005
        var data = new TheoryData<string, string>();
        foreach (var id in builtIn!.Keys)
        {
            var slash = id.IndexOf('/', StringComparison.Ordinal);
            var provider = slash > 0 && KnownProvider(id[..slash]) ? id[..slash] : LlmProviderKeys.OpenAI;
            data.Add(provider, provider == LlmProviderKeys.OpenAI ? id : id[(slash + 1)..]);
        }

        return data;
    }

    private static bool KnownProvider(string key) =>
        key is LlmProviderKeys.Mammouth or LlmProviderKeys.OpenRouter or LlmProviderKeys.Together;

    [Theory]
    [MemberData(nameof(ModelTableIds))]
    public void The_probe_narrows_thinking_by_the_runtimes_model_table(string provider, string model)
    {
#pragma warning disable ORKEXP005
        var entry = ModelCapabilityRegistry.Default.TryGet(model, provider);
#pragma warning restore ORKEXP005

        Assert.Equal(entry?.Thinking, LlmProbeDialect.ModelThinkingOf(provider, model));
    }
}
