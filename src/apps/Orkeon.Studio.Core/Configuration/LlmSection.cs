using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Orkeon.Constants.Configuration;
using Orkeon.Domain.SharedKernel.ValueObjects;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Typed view over the <c>Llm</c> section of an <see cref="AppSettingsDocument"/>.
/// There is deliberately no <c>Provider</c> key: the runtime infers the dialect from
/// the endpoint, so Studio shows how the endpoint is read (<see cref="Orkeon.Studio.Core.Llm.StudioEndpointReading"/>) and writes no provider.
/// </summary>
public sealed partial class LlmSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = "Llm";

    private readonly AppSettingsDocument _document;

    // The section, and each of its keys, where the file spells them, in whatever letter case: what the
    // run binds, and never a twin the configuration would refuse as a duplicate.
    private string Section => _document.PathAsWritten(SectionPath);

    private string Key(string key) => _document.PathAsWritten($"{SectionPath}:{key}");

    internal LlmSection(AppSettingsDocument document)
    {
        _document = document;
        Profiles = new LlmProfilesSection(document);
    }

    /// <summary>
    /// The named profiles under <c>Llm:Profiles</c> (GAP-17, STUDIO-48) — part of this section in
    /// the file, but not part of the default provider it describes.
    /// </summary>
    public LlmProfilesSection Profiles { get; }

    /// <summary>
    /// True when the section describes the default provider: at least one key that holds a value
    /// besides those that describe none (<see cref="KeysBesideTheDefault"/>), the runtime's own
    /// reading (<c>LlmSettings.HasDefault</c>, where a blank value reads as absent — STUDIO-49).
    /// False is the WIN-01 condition: the runtime silently falls back to the
    /// <c>&lt;undefined-llm&gt;</c> echo provider — a section holding profiles alone included.
    /// </summary>
    public bool Exists =>
        _document.GetNode(Section) is JsonObject section
        && section.Any(property =>
            !KeysBesideTheDefault.Contains(property.Key, StringComparer.OrdinalIgnoreCase)
            && HoldsValue(property.Value));

    /// <summary>
    /// The keys of <c>Llm</c> that say nothing of the default provider, as the runtime lists them
    /// (<c>LlmSettings.HasDefault</c>): the named profiles, the capabilities declared per model, the
    /// replay and the section of the agent-cli provider module (<c>Llm:AgentCli</c>, Orkeon #470),
    /// which every profile on that provider shares.
    /// </summary>
    // Also what "no model" leaves in the section (Remove): each of them serves the profiles too.
    internal static readonly string[] KeysBesideTheDefault = [ConfigurationKeys.LlmProfiles, "ModelCapabilities", "Replay", "AgentCli"];

    /// <summary>
    /// Model identifier (<c>Llm:Model</c>). Setting another model than the one the section holds takes
    /// <see cref="ContextWindowTokens"/> out with it (<see cref="LlmProfilesSection.SetModel"/>).
    /// </summary>
    public string? Model
    {
        get => _document.GetString(Key("Model"));
        set => LlmProfilesSection.SetModel(_document, SectionPath, value);
    }

    /// <summary>Endpoint base URL (<c>Llm:BaseUrl</c>).</summary>
    [SuppressMessage("Design", "CA1056",
        Justification = "This is the JSON field as typed by the user, not a resolved endpoint: an " +
                        "in-progress or malformed URL must survive load/edit/save, which System.Uri " +
                        "cannot represent. Validation reports it instead of rejecting the text.")]
    public string? BaseUrl
    {
        get => _document.GetString(Key("BaseUrl"));
        set => _document.SetString(Key("BaseUrl"), value);
    }

    /// <summary>
    /// Inline API key (<c>Llm:ApiKey</c>) — discouraged: prefer <see cref="ApiKeyEnvVar"/>, which
    /// names the variable holding the key. A key here masks that reference: the runtime uses a key
    /// the configuration resolves first. Docker Model Runner's placeholder <c>not-needed</c> is no
    /// key: its server checks none, and the run's OpenAI dialect wants one.
    /// </summary>
    public string? ApiKey
    {
        get => _document.GetString(Key("ApiKey"));
        set => _document.SetString(Key("ApiKey"), value);
    }

    /// <summary>
    /// The environment variable holding the default's key (<c>Llm:ApiKeyEnvVar</c>, STUDIO-49) —
    /// its name, never the key —, which every run reads, Studio's or not. Written by the election
    /// of a model setting, by <c>orkeon init --api-key-env</c> and by <c>orkeon-studio-config</c>.
    /// </summary>
    public string? ApiKeyEnvVar
    {
        get => _document.GetString(Key($"{ConfigurationKeys.LlmApiKeyEnvVar}"));
        set => _document.SetString(Key($"{ConfigurationKeys.LlmApiKeyEnvVar}"), value);
    }

    /// <summary>
    /// The Anthropic workspace the default's key acts in (<c>Llm:WorkspaceId</c>, Orkeon #468), sent as
    /// <c>anthropic-workspace-id</c>: an identity-linked key refuses every call without it. Not a secret.
    /// Another provider does not send it, and the run warns.
    /// </summary>
    public string? WorkspaceId
    {
        get => _document.GetString(Key(LlmProfilesSection.WorkspaceIdKey));
        set => _document.SetString(Key(LlmProfilesSection.WorkspaceIdKey), value);
    }

    /// <summary>Sampling temperature (<c>Llm:Temperature</c>).</summary>
    public double? Temperature
    {
        get => _document.GetDouble(Key("Temperature"));
        set => _document.SetDouble(Key("Temperature"), value);
    }

    /// <summary>
    /// The model's context window, in tokens (<c>Llm:ContextWindowTokens</c>), above zero: what the
    /// compaction of a context measures against. Null lets the engine take the window it documents for
    /// the model. It describes the model the section holds, and goes when <see cref="Model"/> changes.
    /// </summary>
    public int? ContextWindowTokens
    {
        get => _document.GetInt32(Key($"{LlmProfilesSection.ContextWindowTokensKey}"));
        set => _document.SetInt32(Key($"{LlmProfilesSection.ContextWindowTokensKey}"), value);
    }

    /// <summary>Maximum response tokens (<c>Llm:MaxTokens</c>).</summary>
    public int? MaxTokens
    {
        get => _document.GetInt32(Key("MaxTokens"));
        set => _document.SetInt32(Key("MaxTokens"), value);
    }

    /// <summary>Request timeout in seconds (<c>Llm:TimeoutSeconds</c>).</summary>
    public int? TimeoutSeconds
    {
        get => _document.GetInt32(Key("TimeoutSeconds"));
        set => _document.SetInt32(Key("TimeoutSeconds"), value);
    }

    /// <summary>
    /// The longest silence allowed between two lines of a streamed answer, in seconds
    /// (<c>Llm:StreamIdleSeconds</c>); null leaves the stream bounded by the timeout alone (LLM-12).
    /// </summary>
    public int? StreamIdleSeconds
    {
        get => _document.GetInt32(Key("StreamIdleSeconds"));
        set => _document.SetInt32(Key("StreamIdleSeconds"), value);
    }

    /// <summary>The call mode (<c>Llm:CallMode</c>).</summary>
    public string? CallMode
    {
        get => _document.GetString(Key("CallMode"));
        set => _document.SetString(Key("CallMode"), value);
    }

    /// <summary>
    /// Thinking switch (<c>Llm:Thinking:Enabled</c>): null leaves the provider's default —
    /// on for Kimi K2.6, DeepSeek V4 and GLM. The runner reads the same key (LLM-11).
    /// </summary>
    public bool? ThinkingEnabled
    {
        get => _document.GetBoolean(Key("Thinking:Enabled"));
        set => _document.SetBoolean(Key("Thinking:Enabled"), value);
    }

    /// <summary>Reasoning-effort hint (<c>Llm:Thinking:Effort</c>), for the models that take one.</summary>
    public string? ThinkingEffort
    {
        get => _document.GetString(Key("Thinking:Effort"));
        set => _document.SetString(Key("Thinking:Effort"), value);
    }

    /// <summary>
    /// The one sentence that refuses a context window of zero or less, which would leave the agent's
    /// loop no room and the host refuses to start on; null when <paramref name="window"/> is above zero
    /// or left out.
    /// </summary>
    /// <param name="sectionPath">The section of the <c>Llm</c> shape that states the window: <c>Llm</c>, or a profile's.</param>
    /// <param name="window">The window as the section reads it.</param>
    public static string? ContextWindowProblem(string sectionPath, int? window) => window is <= 0
        ? string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{sectionPath}:{LlmProfilesSection.ContextWindowTokensKey}: {window} is not a number of tokens above zero. Leave the key out for the model's documented window.")
        : null;

    /// <summary>
    /// Whether <paramref name="apiKey"/> holds a variable reference — the Domain's recognition
    /// (<see cref="SettingPlaceholder"/>), which the run refuses the start on: Orkeon never
    /// expands it, and the text itself would be sent as the key (STUDIO-55, CH-15). <c>sk-${X}</c>
    /// is one too.
    /// </summary>
    /// <param name="apiKey">The <c>ApiKey</c> as the file holds it.</param>
    public static bool IsKeyPlaceholder(string? apiKey) => SettingPlaceholder.TryMatch(apiKey, out _);

    /// <summary>
    /// What the run's refusal of a placeholder key says to write instead: the name inside the braces
    /// when it can be a variable's name, else <c>&lt;the variable's name&gt;</c>.
    /// </summary>
    /// <param name="apiKey">A key <see cref="IsKeyPlaceholder"/> recognises.</param>
    public static string PlaceholderVariable(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return SettingPlaceholder.TryMatch(apiKey, out var name) && !string.IsNullOrWhiteSpace(name) && IsVariableName(name)
            ? name.Trim()
            : "<the variable's name>";
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be an environment variable's name as the run reads
    /// <c>ApiKeyEnvVar</c>: no <c>=</c>, no space, no line break — a key or a sentence pasted in the
    /// wrong field otherwise (STUDIO-49). Blank is no reference, and reads as true.
    /// </summary>
    /// <param name="name">The reference as written.</param>
    public static bool IsVariableName(string? name) =>
        string.IsNullOrWhiteSpace(name) || !name.Any(c => c == '=' || char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// Whether <paramref name="url"/> is an absolute http(s) URL — the rule of <c>Llm:BaseUrl</c> and of
    /// each profile's. <c>localhost:11434</c> parses as an absolute URI whose scheme is
    /// <c>localhost</c>, so the scheme check is what catches a URL missing its <c>http://</c>.
    /// </summary>
    /// <param name="url">The address as written.</param>
    [SuppressMessage("Design", "CA1054",
        Justification = "The address as the user typed it is what is judged, malformed text included.")]
    public static bool IsAbsoluteHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Writes what a model setting pins into the default section, field by field (STUDIO-49,
    /// the election): every field <see cref="LlmProfileEntry"/> models, a null one removing its
    /// key, the reference to the key's variable included — so a run outside Studio follows the
    /// election, its timeout and its key included. The keys Studio does not model stay where they
    /// are: <c>MaxRetries</c>, <c>Grammar</c>, <c>AvailableModels</c>, <c>Profiles</c>, and an
    /// <c>ApiKey</c> — but Docker Model Runner's placeholder, which an elected Docker Model Runner
    /// setting writes where no key is set and any other takes out (<see cref="KeyAgrees"/>,
    /// STUDIO-54): left under another card, it would pass before the variable the elected setting
    /// names. The entry's <see cref="LlmProfileEntry.Id"/> is not read.
    /// </summary>
    /// <param name="entry">What the elected setting pins.</param>
    /// <returns>True when the document changed.</returns>
    public bool Set(LlmProfileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var before = _document.GetNode(Section)?.ToJsonString();
        LlmProfilesSection.WriteEntry(_document, SectionPath, entry);
        if (_document.GetNode(Section) is JsonObject { Count: 0 })
            _document.Remove(Section);
        return !string.Equals(before, _document.GetNode(Section)?.ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the section's <c>ApiKey</c> agrees with the elected <paramref name="entry"/>
    /// (<see cref="LlmProfilesSection.KeyAgrees(AppSettingsDocument, string, LlmProfileEntry)"/>, STUDIO-54).
    /// </summary>
    /// <param name="entry">What the elected setting writes.</param>
    public bool KeyAgrees(LlmProfileEntry entry) => LlmProfilesSection.KeyAgrees(_document, SectionPath, entry);

    /// <summary>
    /// Whether a JSON node holds a value as the configuration reads it: a scalar that is not blank,
    /// or a container holding one. <c>null</c>, <c>""</c> and <c>{ }</c> read as absent.
    /// </summary>
    private static bool HoldsValue(JsonNode? node) => node switch
    {
        JsonObject container => container.Any(property => HoldsValue(property.Value)),
        JsonArray items => items.Any(HoldsValue),
        JsonValue value when value.TryGetValue<string>(out var text) => !string.IsNullOrWhiteSpace(text),
        JsonValue => true,
        _ => false,
    };

    /// <summary>
    /// Removes the default provider — every key of the section but those that describe none
    /// (<see cref="KeysBesideTheDefault"/>) — which is the WIN-01 state. The named profiles stay:
    /// choosing « no model » for the default says nothing about them, and an entry written by hand is
    /// never Studio's to delete (STUDIO-48); so do the capabilities declared per model, the replay and
    /// the agent-cli module's section, host-wide settings the profiles read too. The section goes
    /// entirely only when nothing is left in it.
    /// </summary>
    public void Remove()
    {
        if (_document.GetNode(Section) is not JsonObject section)
        {
            _document.Remove(Section);
            return;
        }

        foreach (var key in section
                     .Select(property => property.Key)
                     .Where(key => !KeysBesideTheDefault.Contains(key, StringComparer.OrdinalIgnoreCase))
                     .ToList())
        {
            section.Remove(key);
        }

        if (section.Count == 0)
            _document.Remove(Section);
    }
}
