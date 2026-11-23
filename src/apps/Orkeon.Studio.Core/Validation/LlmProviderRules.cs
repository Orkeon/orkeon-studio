using System.Text.Json.Nodes;
using Orkeon.Constants.Configuration;
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
    /// The key of the experimental <c>agent-cli</c> provider module (Orkeon ADR-020): Claude Code, run on
    /// the subscription its CLI is signed in to, configured by <c>Llm:AgentCli</c>. The engine's
    /// <c>AgentCliDiagnostics.ProviderKey</c>, held to it by a drift test.
    /// </summary>
    public const string AgentCliKey = "agent-cli";

    /// <summary>
    /// The provider modules the <c>orkeon</c> tool registers in the hosts of a run, a check and
    /// <c>doctor</c>, by key: Studio's mirror of <c>CliLlmProviderModules.All</c>. A drift test reads that list.
    /// </summary>
    public static IReadOnlyList<string> CliModuleKeys { get; } = [AgentCliKey];

    /// <summary>
    /// The vendor keys of the catalogue, which <c>Provider</c> never names: Studio's mirror of
    /// <c>MockProviderSettings.IsBuiltInKey</c>, held to it by a drift test.
    /// </summary>
    public static bool IsVendorKey(string key) =>
        LlmProviderKeys.All.Contains(key, StringComparer.Ordinal)
        || LlmProviderKeys.Aliases.ContainsKey(key)
        || key is LlmProviderKeys.Custom or LlmProviderKeys.DockerModelRunner or LlmProviderKeys.AzureShort;

    /// <summary>
    /// The refusals of every <c>Provider</c> of <paramref name="document"/>, the keys found whatever their
    /// letter case, as the configuration reads them, in the order the host meets them: those it raises
    /// while it reads the sections (a vendor, a mock without its file, a file beside a module's key),
    /// each <c>Llm:Profiles</c> entry before the <c>Llm</c> section (<c>RegisterLlmProvider</c>); then a
    /// name no module answers, the <c>Llm</c> section's first (<c>UnansweredProviderKeys</c>, said after
    /// every section). A profile under a reserved name (<c>default</c>, <c>auto</c>) is refused for its
    /// name before its <c>Provider</c> is read (<c>LlmSettings.ReadProfiles</c>): its <c>Provider</c> is
    /// not judged. What a launch is told of the settings file the run reads.
    /// </summary>
    public static IReadOnlyList<ValidationMessage> Check(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (SectionOf(document) is not { } section)
            return [];

        var profiles = new List<ValidationMessage>();
        foreach (var (id, entry) in Profiles(section))
        {
            if (!IsReserved(id) && entry is JsonObject profile)
                profiles.AddRange(Check(profile, $"{LlmProfilesSection.SectionPath}:{id}"));
        }

        var own = Check(section, LlmSection.SectionPath);
        return
        [
            .. profiles.Where(message => !IsUnanswered(message)),
            .. own.Where(message => !IsUnanswered(message)),
            .. own.Where(IsUnanswered),
            .. profiles.Where(IsUnanswered),
        ];
    }

    /// <summary>
    /// What stops a run at start in the model settings of <paramref name="document"/>, as the host of a run
    /// says it. What the host raises while it reads the profiles, then the <c>Llm</c> section, stops it
    /// there and is the only refusal: a profile under a reserved name (in the engine's sentence), a
    /// <c>Provider</c> refused where it is read (<see cref="Check(AppSettingsDocument)"/>). Otherwise the
    /// agent-cli module's section (<see cref="AgentCliSection.Problems"/>), which the host of a run
    /// validates whatever provider is named, then a name no module answers. What a launch and a check are
    /// blocked on.
    /// </summary>
    public static IReadOnlyList<ValidationMessage> StartRefusals(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (SectionOf(document) is { } section)
        {
            foreach (var (id, entry) in Profiles(section))
            {
                var path = $"{LlmProfilesSection.SectionPath}:{id}";
                if (ReservedRefusal(id, path) is { } reserved)
                    return [ValidationMessage.Error(ValidationCodes.LlmProfileReservedName, reserved, path)];
                if (entry is JsonObject profile && Check(profile, path).FirstOrDefault(message => !IsUnanswered(message)) is { } read)
                    return [read];
            }

            if (Check(section, LlmSection.SectionPath).FirstOrDefault(message => !IsUnanswered(message)) is { } own)
                return [own];
        }

        return
        [
            .. document.AgentCli.Problems().Select(problem => ValidationMessage.Error(ValidationCodes.EngineSettingRefused, problem.Problem, problem.Path)),
            .. Check(document).Where(IsUnanswered),
        ];
    }

    // The profiles of the section, in the order the configuration gives them (its keys sorted).
    private static IEnumerable<(string Id, JsonNode? Entry)> Profiles(JsonObject section) =>
        Node(section, ConfigurationKeys.LlmProfiles) is JsonObject profiles
            ? profiles.OrderBy(pair => pair.Key, Microsoft.Extensions.Configuration.ConfigurationKeyComparer.Instance).Select(pair => (pair.Key, pair.Value))
            : [];

    private static bool IsReserved(string id) =>
        Orkeon.Application.Interfaces.Ports.LlmProfiles.IsDefault(id) || Orkeon.Application.Interfaces.Ports.LlmProfiles.IsAuto(id);

    // The host's sentence for a profile under a reserved name (LlmSettings.ReadProfiles); null for any other name.
    private static string? ReservedRefusal(string id, string path) =>
        Orkeon.Application.Interfaces.Ports.LlmProfiles.IsDefault(id)
            ? $"{path}: '{Orkeon.Application.Interfaces.Ports.LlmProfiles.Default}' is the reserved name of the default profile, "
              + $"which is the {LlmSection.SectionPath} section itself. Rename the profile."
            : Orkeon.Application.Interfaces.Ports.LlmProfiles.IsAuto(id)
                ? Orkeon.Application.Interfaces.Ports.LlmProfiles.AutoReservedMessage(path)
                : null;

    // The sentence of a name no module answers, which the host says after every section.
    private const string UnansweredFragment = " names no provider of this host: ";

    private static bool IsUnanswered(ValidationMessage message) => message.Text.Contains(UnansweredFragment, StringComparison.Ordinal);

    /// <summary>
    /// The <c>Llm</c> section of <paramref name="document"/>, however the file spells its key (<c>llm</c>,
    /// <c>LLM</c>): the configuration binds it whatever the case. Null when the file holds none.
    /// </summary>
    public static JsonObject? SectionOf(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.GetNode(document.PathAsWritten(LlmSection.SectionPath)) as JsonObject;
    }

    /// <summary>
    /// The refusals of <paramref name="section"/>'s <c>Provider</c>, at <paramref name="sectionPath"/>
    /// (<c>Llm</c>, or <c>Llm:Profiles:&lt;name&gt;</c>); none when it names no provider.
    /// </summary>
    public static IReadOnlyList<ValidationMessage> Check(JsonObject section, string sectionPath) =>
        Check(section, sectionPath, CliModuleKeys);

    /// <summary>
    /// <see cref="Check(JsonObject, string)"/> on a host that registers the provider modules of
    /// <paramref name="modules"/>, by key.
    /// </summary>
    public static IReadOnlyList<ValidationMessage> Check(JsonObject section, string sectionPath, IReadOnlyList<string> modules)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(modules);
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
                $"{sectionPath}:{ProviderKey} names a vendor of the catalogue, which Provider never names. "
                + "Leave Provider out and the provider follows from BaseUrl, the model's name and the key; "
                + "Provider takes 'mock' or the key of a provider module.")];
        }

        if (!string.IsNullOrWhiteSpace(responses))
        {
            return [Refused($"{sectionPath}:{ResponsesKey}",
                $"{sectionPath}:{ResponsesKey}: only the mock provider reads a responses file, and {sectionPath}:{ProviderKey} names another.")];
        }

        if (key == LlmProviderKeys.Mock || modules.Contains(key, StringComparer.OrdinalIgnoreCase))
            return [];

        var known = modules.Count == 0 ? "this host registers none" : "registered: " + string.Join(", ", modules.Order(StringComparer.Ordinal));
        return [Refused($"{sectionPath}:{ProviderKey}",
            $"{sectionPath}:{ProviderKey}{UnansweredFragment}"
            + $"write {LlmProviderKeys.Mock} or the key of a provider module ({known}); "
            + "leave it out and the provider follows from BaseUrl, the model's name and the key.")];
    }

    private static ValidationMessage Refused(string path, string text) =>
        ValidationMessage.Error(ValidationCodes.LlmProvider, text, path);

    /// <summary>
    /// The single value at <paramref name="key"/>, the key compared without case as the configuration
    /// binds it, in the text the JSON configuration gives the engine (a switch reads <c>True</c>).
    /// </summary>
    private static string? Value(JsonObject section, string key) =>
        Node(section, key) is JsonValue value ? EmailAccountRules.ConfigurationText(value) : null;

    private static JsonNode? Node(JsonObject section, string key) =>
        section.FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}
