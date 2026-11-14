using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Orkeon.Constants.Llm;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Llm;

/// <summary>
/// Writes the test completion of the connection probe (STUDIO-43) in the dialect each provider
/// speaks, carrying the profile's model and thinking switch the way the runtime carries them.
/// <para>
/// The fiche asked for the request to go through the runtime's own translation
/// (<c>OpenAICompatibleProviderBase</c>). Studio Core cannot reference Infrastructure (see its
/// csproj), so the payload is written here from each provider's declared
/// <see cref="LlmProviderCapabilities.Thinking"/> level — the same declaration the runtime
/// translates from — and that level is pinned against the real providers by
/// <c>LlmProbeDialectDriftTests</c>.
/// </para>
/// </summary>
public static class LlmProbeDialect
{
    /// <summary>The output cap of the test completion: enough for "pong", a few tokens billed.</summary>
    public const int CompletionMaxTokens = 16;

    /// <summary>The one user message of the test completion.</summary>
    public const string CompletionPrompt = "ping";

    /// <summary>The version header every Anthropic request must carry.</summary>
    internal const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// What each provider declares in <see cref="LlmProviderCapabilities.Thinking"/>, keyed by
    /// the key <see cref="LlmEndpointReading"/> reports. Custom and Docker Model Runner
    /// endpoints are driven as OpenAI by the runtime, so they read OpenAI's level.
    /// </summary>
    private static readonly Dictionary<string, ThinkingSupport> Thinking = new(StringComparer.Ordinal)
    {
        [LlmProviderKeys.OpenAI] = ThinkingSupport.EffortOnly,
        [LlmProviderKeys.Anthropic] = ThinkingSupport.Toggle,
        [LlmProviderKeys.DeepSeek] = ThinkingSupport.Toggle,
        [LlmProviderKeys.Together] = ThinkingSupport.Toggle,
        [LlmProviderKeys.Qwen] = ThinkingSupport.Budget,
        [LlmProviderKeys.Kimi] = ThinkingSupport.Toggle,
        [LlmProviderKeys.HuggingFace] = ThinkingSupport.Toggle,
        [LlmProviderKeys.Mistral] = ThinkingSupport.EffortOnly,
        [LlmProviderKeys.Zai] = ThinkingSupport.Toggle,
        [LlmProviderKeys.Gemini] = ThinkingSupport.EffortOnly,
        [LlmProviderKeys.Grok] = ThinkingSupport.EffortOnly,
        [LlmProviderKeys.MiniMax] = ThinkingSupport.Toggle,
        [LlmProviderKeys.OpenRouter] = ThinkingSupport.Budget,
        [LlmProviderKeys.Mammouth] = ThinkingSupport.EffortOnly,
        [LlmProviderKeys.Ollama] = ThinkingSupport.Toggle,
    };

    /// <summary>The thinking control the provider behind <paramref name="providerKey"/> declares.</summary>
    public static ThinkingSupport ThinkingSupportOf(string providerKey) =>
        Thinking.TryGetValue(providerKey, out var support) ? support : Thinking[LlmProviderKeys.OpenAI];

    /// <summary>Builds the test completion for <paramref name="provider"/>.</summary>
    internal static HttpRequestMessage BuildCompletion(
        string provider, Uri baseUri, string? apiKey, string model, bool? thinkingEnabled)
    {
        var baseUrl = baseUri.AbsoluteUri.TrimEnd('/');
        var messages = new object[] { new Dictionary<string, object> { ["role"] = "user", ["content"] = CompletionPrompt } };
        var payload = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages };
        string url;

        if (string.Equals(provider, LlmProviderKeys.Ollama, StringComparison.Ordinal))
        {
            // Ollama's native chat hangs off the server root, like its catalogue.
            url = $"{LlmEndpointReading.NativeOllamaRoot(baseUri.AbsoluteUri)}/api/chat";
            payload["stream"] = false;
            payload["options"] = new Dictionary<string, object> { ["num_predict"] = CompletionMaxTokens };
            if (thinkingEnabled is { } think)
                payload["think"] = think;

            return Post(url, payload);
        }

        if (string.Equals(provider, LlmProviderKeys.Anthropic, StringComparison.Ordinal))
        {
            url = $"{baseUrl}/v1/messages";
            payload["max_tokens"] = CompletionMaxTokens;
            if (thinkingEnabled is { } enabled)
                payload["thinking"] = AnthropicThinking(model, enabled);

            var anthropic = Post(url, payload);
            anthropic.Headers.Add("x-api-key", apiKey ?? "");
            anthropic.Headers.Add("anthropic-version", AnthropicVersion);
            return anthropic;
        }

        // The OpenAI dialect, as OpenAICompatibleProviderBase writes it.
        url = $"{baseUrl}/chat/completions";
        payload[string.Equals(provider, LlmProviderKeys.OpenAI, StringComparison.Ordinal)
            ? "max_completion_tokens"   // OpenAI retired max_tokens on its current chat models
            : "max_tokens"] = CompletionMaxTokens;
        if (thinkingEnabled is { } on)
            WriteThinkingSwitch(provider, payload, on);

        var message = Post(url, payload);
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return message;
    }

    /// <summary>
    /// The on/off switch, bounded by the declared level exactly as the runtime bounds it: an
    /// effort-only or switchless API gets nothing (the runtime drops it with a warning), DashScope
    /// reads <c>enable_thinking</c>, OpenRouter and Together AI a <c>reasoning</c> object, the
    /// Hugging Face router the chat template's <c>enable_thinking</c>, MiniMax the off value alone
    /// (<c>MiniMax-M3</c> answers an explicit <c>enabled</c> with a 400), the rest a
    /// <c>thinking</c> block.
    /// <para>
    /// The runtime also narrows the switch per model through its capability table (#356): a model
    /// that always reasons, such as GLM-5.3 or <c>MiniMax-M3.1-Flash</c>, gets no off switch. That
    /// table lives in Infrastructure, out of Core's reach: the probe stays at the provider's level
    /// and may send a switch the runtime would drop.
    /// </para>
    /// </summary>
    private static void WriteThinkingSwitch(string provider, Dictionary<string, object> payload, bool enabled)
    {
        if (ThinkingSupportOf(provider) < ThinkingSupport.Toggle)
            return;

        switch (provider)
        {
            case LlmProviderKeys.Qwen:
                payload["enable_thinking"] = enabled;
                break;
            case LlmProviderKeys.OpenRouter or LlmProviderKeys.Together:
                payload["reasoning"] = new Dictionary<string, object> { ["enabled"] = enabled };
                break;
            case LlmProviderKeys.HuggingFace:
                payload["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = enabled };
                break;
            case LlmProviderKeys.MiniMax:
                if (!enabled)
                    payload["thinking"] = new Dictionary<string, object> { ["type"] = "disabled" };
                break;
            default:
                payload["thinking"] = new Dictionary<string, object> { ["type"] = enabled ? "enabled" : "disabled" };
                break;
        }
    }

    /// <summary>
    /// How each Claude model turns thinking off, matched like the runtime's table (the longest id at
    /// a separator): <c>claude-sonnet-5-5</c> takes <c>between_tools</c>, the others
    /// <c>disabled</c>. The same rows as <c>AnthropicLlmProvider</c>; the probe sends no effort, so
    /// the highest effort each value goes with does not apply.
    /// </summary>
    private static readonly (string Id, string Value)[] AnthropicOffSwitches =
    [
        ("claude-sonnet-5-5", "between_tools"),
        ("claude-sonnet-5", "disabled"),
    ];

    /// <summary>
    /// The Messages API thinking block, as the runtime writes it: on is <c>adaptive</c> with
    /// <c>display: summarized</c> (without it a Claude 5 model returns its thinking blocks empty),
    /// off the model's own value, never with <c>display</c>, which <c>between_tools</c> refuses.
    /// </summary>
    private static Dictionary<string, object> AnthropicThinking(string model, bool enabled)
    {
        if (enabled)
            return new Dictionary<string, object> { ["type"] = "adaptive", ["display"] = "summarized" };

        var value = "disabled";
        var matched = 0;
        foreach (var (id, off) in AnthropicOffSwitches)
        {
            if (id.Length > matched && MatchesAtBoundary(model, id))
            {
                value = off;
                matched = id.Length;
            }
        }

        return new Dictionary<string, object> { ["type"] = value };
    }

    /// <summary>Whether <paramref name="model"/> is <paramref name="id"/> or one of its snapshots, as the runtime's table matches.</summary>
    private static bool MatchesAtBoundary(string model, string id)
    {
        if (!model.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            return false;

        return model.Length == id.Length || IsSeparator(id[^1]) || IsSeparator(model[id.Length]);
    }

    private static bool IsSeparator(char c) => c is '-' or '.' or ':' or '@' or '/';

    private static HttpRequestMessage Post(string url, Dictionary<string, object> payload) =>
        new(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
}
