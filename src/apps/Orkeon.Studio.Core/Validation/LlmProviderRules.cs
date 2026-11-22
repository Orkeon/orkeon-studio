using System.Text.Json.Nodes;
using Orkeon.Constants.Llm;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Validation;

/// <summary>
/// The <c>Provider</c> of the <c>Llm</c> section and of each <c>Llm:Profiles</c> entry, judged as the
/// engine's start judges it (Orkeon #457): <c>mock</c> with its <c>Responses</c> file, or the key of a
/// provider module. A vendor of the catalogue is refused there (its address picks it), so is a name no
/// module answers, a <c>mock</c> without its file, and a <c>Responses</c> beside a module's key. The
/// sentences are the engine's (<c>LlmSettings.ReadMock</c>, <c>ReadModuleKey</c> and
/// <c>SettingsValidation.UnansweredProviderKeys</c>), word for word. Studio never writes a
/// <c>Provider</c>: only a hand-written file holds one.
/// </summary>
public static class LlmProviderRules
{
    /// <summary>The key, relative to the section.</summary>
    public const string ProviderKey = "Provider";

    /// <summary>The JSONL file the mock answers from, relative to the section.</summary>
    public const string ResponsesKey = "Responses";

    /// <summary>
    /// The provider modules the <c>orkeon</c> tool registers in every host it builds, by key: Studio's
    /// mirror of <c>CliLlmProviderModules.All</c>, which ships none yet. A drift test reads that list.
    /// </summary>
    public static IReadOnlyList<string> CliModuleKeys { get; } = [];

    /// <summary>
    /// The vendor keys of the catalogue, which <c>Provider</c> never names: Studio's mirror of
    /// <c>MockProviderSettings.IsBuiltInKey</c>, held to it by a drift test.
    /// </summary>
    public static bool IsVendorKey(string key) =>
        LlmProviderKeys.All.Contains(key, StringComparer.Ordinal)
        || LlmProviderKeys.Aliases.ContainsKey(key)
        || key is LlmProviderKeys.Custom or LlmProviderKeys.DockerModelRunner or LlmProviderKeys.AzureShort;

    /// <summary>
    /// The refusals of <paramref name="section"/>'s <c>Provider</c>, at <paramref name="sectionPath"/>
    /// (<c>Llm</c>, or <c>Llm:Profiles:&lt;name&gt;</c>); none when it names no provider.
    /// </summary>
    public static IReadOnlyList<ValidationMessage> Check(JsonObject section, string sectionPath)
    {
        if (Value(section, ProviderKey) is not { } provider || string.IsNullOrWhiteSpace(provider))
            return [];

        var responses = Value(section, ResponsesKey);
        var named = provider.Trim();
        // The engine matches the mock untrimmed, then any other name trimmed, in lowercase.
        if (string.Equals(provider, LlmProviderKeys.Mock, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(responses)
                ? [Refused($"{sectionPath}:{ResponsesKey}", $"{sectionPath}:{ResponsesKey}: the mock provider needs the virtual path of its JSONL responses file.")]
                : [];
        }

#pragma warning disable CA1308 // the key is matched in its lowercase wire form, as the engine does
        var key = named.ToLowerInvariant();
#pragma warning restore CA1308
        if (IsVendorKey(key))
        {
            return [Refused($"{sectionPath}:{ProviderKey}",
                $"{sectionPath}:{ProviderKey}: '{named}' is a vendor of the catalogue, which Provider never names. "
                + "Leave Provider out and the provider follows from BaseUrl, the model's name and the key; "
                + "Provider takes 'mock' or the key of a provider module.")];
        }

        if (!string.IsNullOrWhiteSpace(responses))
        {
            return [Refused($"{sectionPath}:{ResponsesKey}",
                $"{sectionPath}:{ResponsesKey}: only the mock provider reads a responses file; Provider is '{named}'.")];
        }

        if (key == LlmProviderKeys.Mock || CliModuleKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            return [];

        var known = CliModuleKeys.Count == 0 ? "this host registers none" : "registered: " + string.Join(", ", CliModuleKeys.Order(StringComparer.Ordinal));
        return [Refused($"{sectionPath}:{ProviderKey}",
            $"{sectionPath}:{ProviderKey}: '{named}' names no provider of this host. "
            + $"Provider takes '{LlmProviderKeys.Mock}' or the key of a provider module ({known}); "
            + "leave it out and the provider follows from BaseUrl, the model's name and the key.")];
    }

    private static ValidationMessage Refused(string path, string text) =>
        ValidationMessage.Error(ValidationCodes.LlmProvider, text, path);

    /// <summary>The single value at <paramref name="key"/>, the key compared without case as the configuration binds it.</summary>
    private static string? Value(JsonObject section, string key) =>
        section.FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
            ? AppSettingsDocument.ReadString(value)
            : null;
}
