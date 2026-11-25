using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Domain.SharedKernel.ValueObjects;

namespace Orkeon.Studio.Core.Llm;

/// <summary>Why a model setting is not tested: nothing is sent.</summary>
public enum ModelTestRefusal
{
    /// <summary>It is tested.</summary>
    None,

    /// <summary>The setting names no model and no address: a run answers as an echo.</summary>
    NoModel,

    /// <summary>The setting needs a key and none is in place.</summary>
    KeyMissing,

    /// <summary>An Ollama setting holds an address a run reads as another provider.</summary>
    OllamaElsewhere,
}

/// <summary>The probe request a model setting gives, or why it gives none.</summary>
/// <param name="Request">The request; null when refused.</param>
/// <param name="Refusal">Why there is none.</param>
/// <param name="KeyVariable">The variable the setting reads its key from; null when it needs none.</param>
public sealed record ModelTest(LlmProbeRequest? Request, ModelTestRefusal Refusal, string? KeyVariable)
{
    /// <summary>How long a message of the user's own may take: a reasoning model thinks before it answers.</summary>
    public static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The test of <paramref name="profile"/>: the key <paramref name="key"/> gives for its variable and
    /// it alone, the catalogue and a ping — or <paramref name="prompt"/> when one is given.
    /// </summary>
    public static ModelTest Of(
        ModelProfile profile,
        Func<string, string?> key,
        IReadOnlyDictionary<string, ThinkingSupport?>? thinkingOverrides = null,
        string? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(key);

        var card = LlmPresets.CardOf(profile);
        var variable = KeyVariableOf(profile, card);
        if (!profile.DescribesProvider)
            return new(null, ModelTestRefusal.NoModel, variable);
        if (IsOllamaReadElsewhere(card, profile.BaseUrl))
            return new(null, ModelTestRefusal.OllamaElsewhere, variable);

        var value = variable is null ? LlmPresets.PlaceholderKeyOf(card) : key(variable);
        if (variable is not null && string.IsNullOrWhiteSpace(value))
            return new(null, ModelTestRefusal.KeyMissing, variable);

        return new(
            new LlmProbeRequest
            {
                BaseUrl = profile.BaseUrl,
                ApiKey = value,
                WorkspaceId = string.IsNullOrWhiteSpace(profile.WorkspaceId) ? null : profile.WorkspaceId.Trim(),
                Model = profile.Model,
                ThinkingEnabled = profile.ThinkingEnabled,
                CheckCompletion = true,
                Prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt,
                Timeout = string.IsNullOrWhiteSpace(prompt)
                    ? LlmProbeRequest.TimeoutFor(profile.TimeoutSeconds)
                    : PromptTimeoutFor(profile.TimeoutSeconds),
                ModelThinkingOverrides = thinkingOverrides ?? LlmModelCapabilityOverrides.None,
            },
            ModelTestRefusal.None,
            variable);
    }

    /// <summary>Whether an Ollama card holds an address a run reads as another provider.</summary>
    public static bool IsOllamaReadElsewhere(string? card, string? address) =>
        card == LlmPresets.Ollama
        && StudioEndpointReading.Of(address) is { Notice: not LlmEndpointNotice.Refused } reading
        && reading.ProviderKey != LlmPresets.Ollama;

    private static string? KeyVariableOf(ModelProfile profile, string? card)
    {
        if (!string.IsNullOrWhiteSpace(profile.KeyEnvName))
            return profile.KeyEnvName.Trim();

        var provider = LlmPresets.ProviderCatalogFor(Localization.EnglishStudioStrings.Instance)
            .FirstOrDefault(info => info.Name == card);
        return provider is { RequiresApiKey: true } ? provider.DefaultApiKeyEnv ?? LlmPresets.DefaultApiKeyEnv : null;
    }

    private static TimeSpan PromptTimeoutFor(int? seconds) =>
        seconds is > 0 && TimeSpan.FromSeconds(seconds.Value) < PromptTimeout ? TimeSpan.FromSeconds(seconds.Value) : PromptTimeout;
}
