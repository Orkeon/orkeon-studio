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

    /// <summary>
    /// Every id of the runtime's built-in model table (<c>ModelCapabilityRegistry</c>, #356), with what
    /// its row says of thinking: <see cref="ThinkingSupport.None"/> for a model with no reasoning
    /// pass, <see cref="ThinkingSupport.EffortOnly"/> for one that always reasons and refuses every
    /// off value (<c>claude-opus-5-5</c>, <c>MiniMax-M3.1-Flash</c>...), null for a row that says
    /// nothing of it. The null rows are held too: the table answers with the longest id a model
    /// matches, so a row that says nothing wins over a shorter one that does - a built-in one
    /// (<c>together/zai-org/glm-5.3</c> over <c>glm-5.3</c>) or an override of the settings file
    /// (<c>qwen3.7-plus</c> over an override keyed <c>qwen3</c>) - and a row matched on the whole id
    /// stops the lookup before the part after the last slash is tried. Matched as the table matches:
    /// <c>&lt;provider&gt;/&lt;model&gt;</c> first, then the longest id at a boundary, then the part
    /// after the model's last slash. <c>LlmProbeDialectDriftTests</c> holds the ids and their values
    /// to the runtime's table, both ways: a row added, removed or changed there fails it. The
    /// <c>Llm:ModelCapabilities</c> overrides of the settings file are laid over these rows by
    /// <see cref="WithOverrides"/>, as the runtime lays them over its table.
    /// </summary>
    private static readonly Dictionary<string, ThinkingSupport?> ModelThinking = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI chat and audio models: no reasoning pass.
        ["gpt-3.5"] = ThinkingSupport.None,
        ["gpt-4"] = ThinkingSupport.None,
        ["gpt-4-turbo"] = ThinkingSupport.None,
        ["gpt-4o"] = ThinkingSupport.None,
        ["gpt-4.1"] = ThinkingSupport.None,
        ["gpt-4o-audio"] = ThinkingSupport.None,
        ["gpt-4o-mini-audio"] = ThinkingSupport.None,
        ["gpt-audio"] = ThinkingSupport.None,
        // OpenAI reasoning models: the provider's level.
        ["o1"] = null,
        ["o1-mini"] = null,
        ["o1-preview"] = null,
        ["o3"] = null,
        ["o3-mini"] = null,
        ["o4-mini"] = null,
        ["gpt-5"] = null,
        ["gpt-5-chat"] = ThinkingSupport.None,
        ["gpt-5-pro"] = null,
        ["gpt-5.6"] = null,
        ["gpt-6"] = null,
        ["gpt-6-astra"] = null,
        ["gpt-6.1"] = null,
        ["gpt-6.1-sol"] = null,
        // Gemini.
        ["gemini-"] = null,
        ["gemini-1.5"] = ThinkingSupport.None,
        ["gemini-2.0"] = ThinkingSupport.None,
        ["gemini-2.5"] = null,
        ["gemini-3.7"] = ThinkingSupport.EffortOnly,
        ["gemini-3.8"] = ThinkingSupport.EffortOnly,
        ["gemini-4"] = null,
        // Anthropic.
        ["claude-"] = null,
        ["claude-opus-4"] = null,
        ["claude-sonnet-4"] = null,
        ["claude-haiku-4"] = null,
        ["claude-opus-5-5"] = ThinkingSupport.EffortOnly,
        ["claude-fable-5"] = ThinkingSupport.EffortOnly,
        // Grok, Mistral, Qwen, Kimi, GLM, MiniMax.
        ["grok-4.6"] = null,
        ["grok-4.7"] = null,
        ["mistral-large-4"] = null,
        ["mistral-medium-2604"] = null,
        ["mistral-small-2603"] = null,
        ["qwen3.7-plus"] = null,
        ["kimi-k2.7-code"] = ThinkingSupport.EffortOnly,
        ["glm-5.3"] = ThinkingSupport.EffortOnly,
        ["MiniMax-M3.1-Flash"] = ThinkingSupport.EffortOnly,
        // A gateway's own row, <provider>/<model>, read before the vendor's.
        ["mammouth/gemini-3.7"] = ThinkingSupport.EffortOnly,
        ["mammouth/gemini-3.8"] = ThinkingSupport.EffortOnly,
        ["openrouter/google/gemini-3.7"] = ThinkingSupport.EffortOnly,
        ["openrouter/google/gemini-3.8"] = ThinkingSupport.EffortOnly,
        ["together/zai-org/glm-5.3"] = null,
    };

    /// <summary>
    /// The thinking control <paramref name="model"/> behind <paramref name="providerKey"/> takes, as
    /// the runtime judges it: the lower of the provider's declaration and the model's row.
    /// </summary>
    public static ThinkingSupport ThinkingSupportOf(string providerKey, string? model) =>
        ThinkingSupportOf(providerKey, model, null);

    /// <summary>
    /// The thinking control <paramref name="model"/> behind <paramref name="providerKey"/> takes on a
    /// host whose settings carry <paramref name="overrides"/>: the lower of the provider's declaration
    /// and the model's row, the overrides laid over the built-in rows.
    /// </summary>
    /// <param name="providerKey">The key the endpoint is read as.</param>
    /// <param name="model">The model id.</param>
    /// <param name="overrides">
    /// The entries of <c>Llm:ModelCapabilities</c> by model id (<see cref="LlmModelCapabilityOverrides.ThinkingOf"/>),
    /// a null value for an entry that leaves <c>Thinking</c> out; null or empty for none.
    /// </param>
    public static ThinkingSupport ThinkingSupportOf(
        string providerKey, string? model, IReadOnlyDictionary<string, ThinkingSupport?>? overrides)
    {
        var provider = ThinkingSupportOf(providerKey);
        return ModelThinkingOf(providerKey, model, overrides) is { } narrowed && narrowed < provider ? narrowed : provider;
    }

    /// <summary>What the runtime's model table says of <paramref name="model"/>'s thinking; null when nothing.</summary>
    public static ThinkingSupport? ModelThinkingOf(string? providerKey, string? model) =>
        ModelThinkingOf(providerKey, model, null);

    /// <summary>
    /// What the runtime's model table says of <paramref name="model"/>'s thinking once
    /// <paramref name="overrides"/> are laid over it; null when nothing.
    /// </summary>
    /// <param name="providerKey">The key the endpoint is read as.</param>
    /// <param name="model">The model id.</param>
    /// <param name="overrides">The entries of <c>Llm:ModelCapabilities</c> by model id; null or empty for none.</param>
    public static ThinkingSupport? ModelThinkingOf(
        string? providerKey, string? model, IReadOnlyDictionary<string, ThinkingSupport?>? overrides)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var rows = WithOverrides(overrides);
        model = model.Trim();
        if (!string.IsNullOrWhiteSpace(providerKey) && Longest(rows, $"{providerKey.Trim()}/{model}") is { } served)
            return served.Value;

        return Lookup(rows, model);
    }

    /// <summary>
    /// The built-in rows with <paramref name="overrides"/> laid over them as
    /// <c>ModelCapabilityRegistry</c> lays them: each entry becomes a row under its own id, and one
    /// that leaves <c>Thinking</c> out keeps what the built-in rows say of that id - looked up as a
    /// model without a provider, whatever the other overrides say.
    /// </summary>
    private static Dictionary<string, ThinkingSupport?> WithOverrides(IReadOnlyDictionary<string, ThinkingSupport?>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
            return ModelThinking;

        var rows = new Dictionary<string, ThinkingSupport?>(ModelThinking, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, thinking) in overrides)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var key = id.Trim();
            rows[key] = thinking ?? Lookup(ModelThinking, key);
        }

        return rows;
    }

    /// <summary>
    /// The row for <paramref name="model"/>: the id whole first, then, for an aggregator's
    /// <c>vendor/model</c> id, the part after its last slash.
    /// </summary>
    private static ThinkingSupport? Lookup(Dictionary<string, ThinkingSupport?> rows, string model)
    {
        if (Longest(rows, model) is { } whole)
            return whole.Value;

        var slash = model.LastIndexOf('/');
        return slash >= 0 && slash < model.Length - 1 ? Longest(rows, model[(slash + 1)..])?.Value : null;
    }

    /// <summary>The row of the longest id <paramref name="model"/> matches at a boundary; null when none.</summary>
    private static KeyValuePair<string, ThinkingSupport?>? Longest(Dictionary<string, ThinkingSupport?> rows, string model)
    {
        KeyValuePair<string, ThinkingSupport?>? best = null;
        foreach (var row in rows)
        {
            if (row.Key.Length > (best?.Key.Length ?? 0) && MatchesAtBoundary(model, row.Key))
                best = row;
        }

        return best;
    }

    /// <summary>Builds the test completion for <paramref name="provider"/>.</summary>
    internal static HttpRequestMessage BuildCompletion(
        string provider, Uri baseUri, string? apiKey, string model, bool? thinkingEnabled) =>
        BuildCompletion(provider, baseUri, apiKey, model, thinkingEnabled, null);

    /// <summary>
    /// Builds the test completion for <paramref name="provider"/> on a host whose settings carry
    /// <paramref name="overrides"/> (<c>Llm:ModelCapabilities</c>): the thinking switch is narrowed by
    /// them as a run narrows it. An Anthropic request carries <paramref name="workspaceId"/>, when there
    /// is one, as the run does (<see cref="AddWorkspace"/>).
    /// </summary>
    internal static HttpRequestMessage BuildCompletion(
        string provider, Uri baseUri, string? apiKey, string model, bool? thinkingEnabled,
        IReadOnlyDictionary<string, ThinkingSupport?>? overrides, string? workspaceId = null)
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
            // A model that always reasons gets no switch, as the runtime sends it none.
            if (thinkingEnabled is { } think && ThinkingSupportOf(provider, model, overrides) >= ThinkingSupport.Toggle)
                payload["think"] = think;

            return Post(url, payload);
        }

        if (string.Equals(provider, LlmProviderKeys.Anthropic, StringComparison.Ordinal))
        {
            url = $"{baseUrl}/v1/messages";
            payload["max_tokens"] = CompletionMaxTokens;
            if (thinkingEnabled is { } enabled && AnthropicThinking(model, enabled, overrides) is { } block)
                payload["thinking"] = block;

            var anthropic = Post(url, payload);
            anthropic.Headers.Add("x-api-key", apiKey ?? "");
            anthropic.Headers.Add("anthropic-version", AnthropicVersion);
            AddWorkspace(anthropic, workspaceId);
            return anthropic;
        }

        // The OpenAI dialect, as OpenAICompatibleProviderBase writes it.
        url = $"{baseUrl}/chat/completions";
        payload[string.Equals(provider, LlmProviderKeys.OpenAI, StringComparison.Ordinal)
            ? "max_completion_tokens"   // OpenAI retired max_tokens on its current chat models
            : "max_tokens"] = CompletionMaxTokens;
        if (thinkingEnabled is { } on)
            WriteThinkingSwitch(provider, model, payload, on, overrides);

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
    /// The switch is narrowed per model as the runtime narrows it (#356): a model that always
    /// reasons, such as GLM-5.3 or <c>MiniMax-M3.1-Flash</c>, gets none (<see cref="ModelThinking"/>,
    /// under the overrides of the settings file).
    /// </para>
    /// </summary>
    private static void WriteThinkingSwitch(
        string provider, string model, Dictionary<string, object> payload, bool enabled,
        IReadOnlyDictionary<string, ThinkingSupport?>? overrides)
    {
        if (ThinkingSupportOf(provider, model, overrides) < ThinkingSupport.Toggle)
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
    /// sent to a model that always reasons too; off the model's own value, never with
    /// <c>display</c>, which <c>between_tools</c> refuses, and nothing to a model that refuses every
    /// off value (<c>claude-opus-5-5</c>, <c>claude-fable-5</c>): the runtime drops it.
    /// </summary>
    private static Dictionary<string, object>? AnthropicThinking(
        string model, bool enabled, IReadOnlyDictionary<string, ThinkingSupport?>? overrides)
    {
        if (enabled)
            return new Dictionary<string, object> { ["type"] = "adaptive", ["display"] = "summarized" };

        if (ThinkingSupportOf(LlmProviderKeys.Anthropic, model, overrides) < ThinkingSupport.Toggle)
            return null;

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

    /// <summary>The header the run sends an Anthropic workspace in (<c>HttpDefaults.AnthropicWorkspaceIdHeader</c>, Orkeon #468).</summary>
    public const string AnthropicWorkspaceIdHeader = "anthropic-workspace-id";

    /// <summary>
    /// Adds <paramref name="workspaceId"/>, trimmed, to an Anthropic request: an identity-linked key
    /// refuses every call without it, the catalogue included. Nothing for a blank one.
    /// </summary>
    internal static void AddWorkspace(HttpRequestMessage request, string? workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
            request.Headers.Add(AnthropicWorkspaceIdHeader, workspaceId.Trim());
    }

    private static HttpRequestMessage Post(string url, Dictionary<string, object> payload) =>
        new(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
}
