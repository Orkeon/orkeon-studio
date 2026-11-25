using System.Text.Json.Nodes;
using Orkeon.Studio.Core.Presets;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The secrets a settings file leads to: the values of the variables it names (<c>ApiKeyEnvVar</c>,
/// <c>TokenEnvVar</c>, ...) and the ones written in it. A text copied out of Studio is cleaned of them.
/// </summary>
public static class SettingsSecrets
{
    private static readonly string[] SecretKeys = ["ApiKey", "Token", "Password", "ClientSecret"];

    /// <summary>The secrets <paramref name="document"/> leads to, variables read through <paramref name="read"/>.</summary>
    public static IReadOnlyList<string> Of(AppSettingsDocument document, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(read);

        var found = new List<string>();
        Collect(JsonNode.Parse(document.ToJson()), read, found);
        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    private static void Collect(JsonNode? node, Func<string, string?> read, List<string> found)
    {
        if (node is JsonArray items)
        {
            foreach (var item in items)
                Collect(item, read, found);
            return;
        }

        if (node is not JsonObject members)
            return;

        foreach (var (name, value) in members)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                Collect(value, read, found);
            else if (name.EndsWith("EnvVar", StringComparison.OrdinalIgnoreCase))
            {
                if (read(text.Trim()) is { Length: > 0 } secret)
                    found.Add(secret);
            }
            else if (SecretKeys.Contains(name, StringComparer.OrdinalIgnoreCase) && text != LlmPresets.DockerModelRunnerApiKeyPlaceholder)
                found.Add(text);
        }
    }
}
