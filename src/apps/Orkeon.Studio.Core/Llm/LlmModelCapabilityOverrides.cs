using System.Globalization;
using System.Text.Json.Nodes;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Llm;

/// <summary>
/// The per-model overrides of a settings file (<c>Llm:ModelCapabilities:&lt;id&gt;</c>), read the way the
/// run reads them at start (<c>LlmSettings.ReadModelCapabilities</c>): what the connection probe needs
/// of them is each entry's <c>Thinking</c>. The run reads them from the top-level <c>Llm</c> section
/// alone, never from a profile.
/// </summary>
public static class LlmModelCapabilityOverrides
{
    /// <summary>The key of the overrides under the <c>Llm</c> section.</summary>
    internal const string Key = "ModelCapabilities";

    /// <summary>The colon-separated path of the overrides.</summary>
    internal const string Path = LlmSection.SectionPath + ":" + Key;

    /// <summary>No override: the run's built-in table alone.</summary>
    public static IReadOnlyDictionary<string, ThinkingSupport?> None { get; } =
        new Dictionary<string, ThinkingSupport?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every entry of <paramref name="document"/>'s overrides by model id, with the thinking support it
    /// names; null for an entry that leaves <c>Thinking</c> out, which keeps the built-in value of that
    /// id. A value the run refuses at start (the validator reports it) is read as left out.
    /// </summary>
    /// <param name="document">The settings document, as it stands now.</param>
    public static IReadOnlyDictionary<string, ThinkingSupport?> ThinkingOf(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Read(document.GetNode(LlmSection.SectionPath)) is not { HasChildren: true } models)
            return None;

        var result = new Dictionary<string, ThinkingSupport?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in models.Children)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;

            var thinking = entry.Child("Thinking");
            result[entry.Key.Trim()] = thinking is { HasChildren: false, Text: { } text } && TryParseThinking(text, out var support)
                ? support
                : null;
        }

        return result;
    }

    /// <summary>
    /// One <see cref="ThinkingSupport"/> name only, without case, as the run reads it:
    /// <c>Enum.TryParse</c> alone would read <c>EffortOnly,Toggle</c> as flags, and <c>1</c> as a value.
    /// </summary>
    /// <param name="value">The text written under <c>Thinking</c>.</param>
    /// <param name="support">The support named, when the method returns true.</param>
    internal static bool TryParseThinking(string? value, out ThinkingSupport support)
    {
        support = default;
        return value is not null
            && !value.Contains(',', StringComparison.Ordinal)
            && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && Enum.TryParse(value, ignoreCase: true, out support)
            && Enum.IsDefined(support);
    }

    /// <summary>The overrides of an <c>Llm</c> section as the run's configuration sees them; null when the section has none.</summary>
    internal static SettingNode? Read(JsonNode? llmSection)
    {
        if (llmSection is not JsonObject section)
            return null;

        var models = section.FirstOrDefault(property => string.Equals(property.Key, Key, StringComparison.OrdinalIgnoreCase));
        return models.Key is null ? null : SettingNode.Of(models.Value, Path);
    }
}
