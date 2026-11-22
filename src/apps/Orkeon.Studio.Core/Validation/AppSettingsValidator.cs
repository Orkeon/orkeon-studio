using Orkeon.Constants.Configuration;
using Orkeon.Constants.Llm;
using System.Globalization;
using System.Text.Json.Nodes;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.FileSystem;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Presets;

namespace Orkeon.Studio.Core.Validation;

/// <summary>Why a document is being validated — it decides how hard some findings are.</summary>
public enum ValidationScope
{
    /// <summary>
    /// While the user is still editing: an empty mount list is a warning, because a launcher
    /// can still supply mounts on the command line.
    /// </summary>
    Editing,

    /// <summary>
    /// About to write the file: an empty mount list is an error. A settings file is meant to
    /// stand on its own (spec §4.5, "at least one mount"), and saving one that cannot start a
    /// run without extra command-line arguments is the mistake worth blocking.
    /// </summary>
    Saving,
}

/// <summary>
/// Pre-save validation of an <see cref="AppSettingsDocument"/>: well-formed JSON,
/// known keys holding values of a usable type, mounts the runtime will accept, and
/// the WIN-01 warning — the onboarding trap where a missing <c>Llm</c> section makes
/// the runtime degrade silently to the echo provider.
/// </summary>
public sealed class AppSettingsValidator
{
    /// <summary>
    /// The WIN-01 message, verbatim as <c>RunnerHost.LlmNotConfiguredMessage</c> words it so
    /// the UI warning and the runtime warning read the same.
    /// </summary>
    public const string LlmNotConfiguredMessage = OperatorMessages.LlmNotConfigured;

    /// <summary>
    /// Studio-only addendum to WIN-01. The runtime reads <c>ORKEON_Llm__*</c> environment
    /// variables as the <c>Llm</c> section and then warns about nothing; Studio only sees the
    /// file, so it must say that this finding can be a false alarm rather than let a user go
    /// hunting for a problem their environment already solved.
    /// </summary>
    public const string LlmNotConfiguredEnvironmentNote =
        "Studio inspects this file alone: if `ORKEON_Llm__BaseUrl` / `ORKEON_Llm__Model` are set " +
        "in the environment the run is launched with, the runtime reads them as the `Llm` " +
        "section and emits no warning.";

    /// <summary>Full text of the WIN-01 finding as reported by this validator.</summary>
    public const string LlmNotConfiguredWarning =
        LlmNotConfiguredMessage + " " + LlmNotConfiguredEnvironmentNote;

    private static readonly string[] StringFields =
    [
        "Orkeon:Rag:Profile",
        "Orkeon:Rag:Provider",
        RagSection.LlmProfilePath,
    ];

    // Every field the run reads as an integer (int.TryParse, or the binder): 600.0, 0.5 or "1e3" is
    // no integer to it — ignored, or a refused start (STUDIO-55). The Llm shape's own are below.
    private static readonly string[] IntegerFields =
    [
        "RateLimiting:MaxConcurrentRequests",
        "RateLimiting:GlobalRequestsPerMinute",
        "RateLimiting:ProviderRequestsPerMinute",
        "RateLimiting:AgentRequestsPerMinute",
        "RateLimiting:QueueLimit",
        "LlmLogging:MaxBodyLengthChars",
        "Orkeon:Rag:Corrective:MaxIterations",
    ];

    private static readonly string[] BooleanFields =
    [
        "LlmLogging:FullEmbeddingLog",
        "LlmLogging:LogStreamingExchanges",
        "Orkeon:Rag:Retrieval:Hybrid:Enabled",
        "Orkeon:Rag:Corrective:WebFallback:Enabled",
        "Orkeon:Rag:WebFallback:Enabled",
        McpSection.SectionPath + ":Enabled",
        ShellToolsSection.SectionPath + ":AllowInterpreters",
    ];

    // The keys of a section of the Llm shape — Llm itself and each entry of Llm:Profiles —, by the
    // kind the run reads them as (LlmSettings): relative to the section.
    private static readonly string[] LlmStringKeys =
        ["Model", "BaseUrl", "ApiKey", ConfigurationKeys.LlmApiKeyEnvVar, "Thinking:Effort", LlmProviderRules.ProviderKey, LlmProviderRules.ResponsesKey,
         LlmProfilesSection.WorkspaceIdKey];
    private static readonly string[] LlmIntegerKeys =
        ["MaxTokens", "TimeoutSeconds", "StreamIdleSeconds", "MaxRetries", LlmProfilesSection.ContextWindowTokensKey];
    private static readonly string[] LlmBooleanKeys = ["Thinking:Enabled", ConfigurationKeys.LlmGrammar];

    // The words that make an environment value read as a secret: written in clear in the
    // settings file, it is the very thing the API-key rows keep out of every file.
    private static readonly string[] SecretWords = ["TOKEN", "SECRET", "KEY", "PASSWORD", "PASSWD"];

    private readonly MountValidator _mounts;

    /// <summary>Creates a validator over the given directory access (defaults to the real disk).</summary>
    public AppSettingsValidator(IDirectoryProbe? directories = null) =>
        _mounts = new MountValidator(directories);

    /// <summary>Parses then validates JSON text; a malformed document yields a single error.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="scope">Why the document is being validated; see <see cref="ValidationScope"/>.</param>
    public IReadOnlyList<ValidationMessage> ValidateJson(
        string? json,
        ValidationScope scope = ValidationScope.Editing)
    {
        if (AppSettingsDocument.TryParse(json, out var document, out var error))
            return Validate(document, scope);

        return
        [
            ValidationMessage.Error(
                ValidationCodes.MalformedJson,
                string.Create(CultureInfo.InvariantCulture, $"The file is not valid JSON: {error}")),
        ];
    }

    /// <summary>Validates a document, most severe findings first within each family.</summary>
    /// <param name="document">The edited document.</param>
    /// <param name="scope">Why the document is being validated; see <see cref="ValidationScope"/>.</param>
    public IReadOnlyList<ValidationMessage> Validate(
        AppSettingsDocument document,
        ValidationScope scope = ValidationScope.Editing)
    {
        ArgumentNullException.ThrowIfNull(document);

        var messages = new List<ValidationMessage>();

        ValidateLlm(document, messages);
        ValidateTypes(document, messages);
        ValidateRagProfile(document, messages);
        ValidateRagLlmProfile(document, messages);
        ValidateMounts(document, messages);
        ValidateMcp(document, messages);
        ValidateEmail(document, messages);
        ValidateDrive(document, messages);
        ValidateEngineSections(document, messages);
        ValidateSettingPlaceholders(document, messages);

        return messages;
    }

    /// <summary>
    /// The sections the engine validates at start with rules of its own, judged by those very rules: the
    /// compaction and the pruning of a context, the skills, both sides of the tool bridge (which every
    /// host validates, the serving command or not), the guardian and the decision profiles. One error
    /// per refused value, at its key.
    /// </summary>
    private static void ValidateEngineSections(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        // What every check of a section says of a key the configuration refuses as written twice.
        const string KeyWrittenTwice = "the configuration refuses a key written twice";

        // A file the configuration does not load is said once, whichever section holds the key it names,
        // and not again where the check of a section already said it of its own keys, in its own words.
        var load = document.LoadRefusal();
        if (load is { } refused
            && !messages.Any(said => said.Severity == ValidationSeverity.Error && said.Text.Contains(KeyWrittenTwice, StringComparison.Ordinal)))
        {
            messages.Add(ValidationMessage.Error(ValidationCodes.EngineSettingRefused, refused.Problem, refused.Path));
        }

        foreach (var (path, problem) in document.Compaction.Problems()
            .Concat(document.Skills.Problems())
            .Concat(document.ToolBridge.Problems())
            .Concat(document.BridgeServer.Problems())
            .Concat(document.Guardian.Problems())
            .Concat(document.Decision.ProfileProblems())
            .Concat(document.Decision.UnknownKeys())
            .Where(found => load is null || found != load.Value))
            messages.Add(ValidationMessage.Error(ValidationCodes.EngineSettingRefused, problem, path));

        // Like Orkeon:Rag:LlmProfile: the profile may come from the environment a run is launched with.
        if (document.Compaction.UnknownProfile() is { } unknown)
            messages.Add(ValidationMessage.Warning(ValidationCodes.EngineSettingRefused, unknown.Problem, unknown.Path));
    }

    /// <summary>
    /// The e-mail accounts (STUDIO-66), judged as the run will judge them: a broken account stops
    /// no run — the engine sets it aside, and says why when a tool names it —, so each finding is
    /// a warning in the engine's own sentence (<see cref="EmailAccountRules"/>) and the file saves.
    /// The one error is a pair of account names, or of keys anywhere in the section
    /// (<see cref="EmailTwinKeys"/>), equal but for the case and that set one same key: the JSON
    /// configuration refuses a key written twice, and no run reads the file at all.
    /// </summary>
    private static void ValidateEmail(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        var email = document.Email;
        if (document.GetNode(document.PathAsWritten(EmailSection.SectionPath)) is JsonObject section)
            messages.AddRange(EmailTwinKeys.Under(section, EmailSection.SectionPath, email.AccountsNode));

        if (EmailAccountRules.ScreeningProblem(document) is { } screening)
        {
            messages.Add(ValidationMessage.Warning(
                ValidationCodes.EmailScreening,
                screening,
                $"{EmailSection.SectionPath}:{EmailSection.Keys.Screening}:{EmailSection.Keys.WithholdRejected}"));
        }

        var names = email.AccountNames;
        if (email.DefaultAccount?.Trim() is { Length: > 0 } named && !names.Contains(named, StringComparer.OrdinalIgnoreCase))
        {
            // The sentence a call that names no account gets from the engine's registry.
            var configured = names.Count == 0 ? "none" : string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase));
            messages.Add(ValidationMessage.Warning(
                ValidationCodes.EmailDefault,
                $"Unknown e-mail account '{named}'. Configured: {configured}.",
                $"{EmailSection.SectionPath}:{EmailSection.Keys.DefaultAccount}"));
        }

        // Names equal but for the case are judged together and on that alone: the run reads none
        // of them on its own. Each is compared with every one before it.
        var accounts = email.AccountsNode;
        var twins = names
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var earlier = new Dictionary<string, (string First, TwinKeys.Reading Read)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!twins.Contains(name))
            {
                messages.AddRange(EmailAccountRules.Check(document, name));
                continue;
            }

            var account = accounts?[name];
            if (earlier.TryGetValue(name, out var twin))
            {
                messages.Add(DuplicateAccount(EmailFamily, twin.First, name, twin.Read.Next(account)));
            }
            else
            {
                var read = new TwinKeys.Reading();
                read.Next(account);
                earlier[name] = (name, read);
            }

            // The keys inside are judged all the same: a pair of them alone makes the file unreadable.
            if (account is JsonObject keys)
                messages.AddRange(EmailTwinKeys.Under(keys, $"{EmailSection.AccountsPath}:{name}"));
        }
    }

    /// <summary>
    /// The drive accounts, judged as the run will judge them, on the model of the e-mail ones: each
    /// finding a warning in the engine's own sentence (<see cref="DriveAccountRules"/>), the one
    /// error a pair of names or of keys equal but for the case that set one same key.
    /// </summary>
    private static void ValidateDrive(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        var drive = document.Drive;
        if (document.GetNode(document.PathAsWritten(DriveSection.SectionPath)) is JsonObject section)
            messages.AddRange(DriveTwinKeys.Under(section, DriveSection.SectionPath, drive.AccountsNode));

        if (DriveAccountRules.ScreeningProblem(document) is { } screening)
        {
            messages.Add(ValidationMessage.Warning(
                ValidationCodes.DriveScreening,
                screening,
                $"{DriveSection.SectionPath}:{DriveSection.Keys.Screening}:{DriveSection.Keys.WithholdRejected}"));
        }

        var names = drive.AccountNames;
        if (drive.DefaultAccount?.Trim() is { Length: > 0 } named && !names.Contains(named, StringComparer.OrdinalIgnoreCase))
        {
            // The sentence a call that names no account gets from the engine's registry.
            var configured = names.Count == 0 ? "none" : string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase));
            messages.Add(ValidationMessage.Warning(
                ValidationCodes.DriveDefault,
                $"Unknown drive account '{named}'. Configured: {configured}.",
                $"{DriveSection.SectionPath}:{DriveSection.Keys.DefaultAccount}"));
        }

        var accounts = drive.AccountsNode;
        var twins = names
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var earlier = new Dictionary<string, (string First, TwinKeys.Reading Read)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!twins.Contains(name))
            {
                messages.AddRange(DriveAccountRules.Check(document, name));
                continue;
            }

            var account = accounts?[name];
            if (earlier.TryGetValue(name, out var twin))
            {
                messages.Add(DuplicateAccount(DriveFamily, twin.First, name, twin.Read.Next(account)));
            }
            else
            {
                var read = new TwinKeys.Reading();
                read.Next(account);
                earlier[name] = (name, read);
            }

            if (account is JsonObject keys)
                messages.AddRange(DriveTwinKeys.Under(keys, $"{DriveSection.AccountsPath}:{name}"));
        }
    }

    /// <summary>What names an account family in the sentences of <see cref="DuplicateAccount"/>.</summary>
    private sealed record AccountFamily(string Label, string Code, string AccountsPath);

    private static readonly AccountFamily EmailFamily = new("E-mail", ValidationCodes.EmailDuplicate, EmailSection.AccountsPath);

    private static readonly AccountFamily DriveFamily = new("Drive", ValidationCodes.DriveDuplicate, DriveSection.AccountsPath);

    /// <summary>
    /// What the configuration does of two account names equal but for the case: it refuses the
    /// file when both objects set one same key (<paramref name="shared"/> — an error: every run
    /// fails to start), and reads one account made of both otherwise (a warning).
    /// </summary>
    private static ValidationMessage DuplicateAccount(AccountFamily family, string first, string name, string? shared)
    {
        var path = $"{family.AccountsPath}:{name}";
        if (shared is null)
        {
            return ValidationMessage.Warning(
                family.Code,
                $"{family.Label} accounts '{first}' and '{name}' differ only by case: the run reads them as one account, " +
                "the keys of both together. Rename one, or merge them.",
                path);
        }

        var what = shared.Length == 0 ? "a value" : shared;
        return ValidationMessage.Error(
            family.Code,
            $"{family.Label} accounts '{first}' and '{name}' differ only by case and both set " +
            $"{what}: the configuration refuses a key written twice, " +
            "so no run can read this file. Rename one, or merge them.",
            path);
    }

    /// <summary>
    /// The MCP servers (STUDIO-21): the rules the runtime applies when it connects, said
    /// before the file is written rather than at the next run. An identifier is a
    /// configuration key, so it must survive the binder; a stdio server without a command
    /// and an HTTP server without a URL are the two refusals <c>McpToolProvider</c> raises;
    /// a secret written in an environment block is the same clear-text trap as an inline
    /// API key, reported at the same level.
    /// </summary>
    private static void ValidateMcp(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        var serversNode = document.GetNode(McpSection.ServersPath);
        if (serversNode is not null and not JsonObject)
        {
            messages.Add(WrongType(McpSection.ServersPath, "an object of servers keyed by identifier"));
            return;
        }

        foreach (var id in document.Mcp.ServerIds)
            ValidateMcpServer(document, id, messages);
    }

    private static void ValidateMcpServer(AppSettingsDocument document, string id, List<ValidationMessage> messages)
    {
        var path = $"{McpSection.ServersPath}:{id}";
        if (!McpSection.IsValidServerId(id))
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.McpServerIdInvalid,
                string.Create(CultureInfo.InvariantCulture,
                    $"'{id}' is not a usable MCP server identifier: letters, digits, '.', '_' and '-' only."),
                path));
        }

        if (document.Mcp.GetServer(id) is not { } server)
            return;

        if (TransportError(server, id, path) is { } transportError)
            messages.Add(transportError);

        foreach (var (name, value) in server.Env.Where(entry => LooksLikeSecret(entry.Key, entry.Value)))
        {
            messages.Add(ValidationMessage.Information(
                ValidationCodes.McpEnvLooksSecret,
                string.Create(CultureInfo.InvariantCulture,
                    $"'{name}' of MCP server '{id}' is written in clear text in this file. The server process inherits your user environment: set the variable there and leave it out of the file."),
                $"{path}:Env:{name}"));
        }
    }

    /// <summary>
    /// The one refusal a server's transport earns: unknown, stdio without a command, or HTTP
    /// (sse) without an absolute http(s) URL. Null when the transport is complete.
    /// </summary>
    private static ValidationMessage? TransportError(McpServerDefinition server, string id, string path)
    {
        if (!server.IsStdio && !server.IsSse)
        {
            return ValidationMessage.Error(
                ValidationCodes.McpTransportUnknown,
                string.Create(CultureInfo.InvariantCulture,
                    $"Unknown MCP transport '{server.Transport}' for server '{id}'. Known transports: {string.Join(", ", McpSection.Transports)}."),
                $"{path}:Transport");
        }

        if (server.IsStdio && string.IsNullOrWhiteSpace(server.Command))
        {
            return ValidationMessage.Error(
                ValidationCodes.McpCommandMissing,
                string.Create(CultureInfo.InvariantCulture,
                    $"MCP server '{id}' uses the stdio transport and names no command to launch."),
                $"{path}:Command");
        }

        if (server.IsSse && !LlmSection.IsAbsoluteHttpUrl(server.Url))
        {
            return ValidationMessage.Error(
                ValidationCodes.McpUrlInvalid,
                string.Create(CultureInfo.InvariantCulture,
                    $"MCP server '{id}' uses the HTTP (sse) transport and '{server.Url}' is not an absolute http(s) URL."),
                $"{path}:Url");
        }

        return null;
    }

    private static bool LooksLikeSecret(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (SettingPlaceholder.TryMatch(value, out _))
            return false;

        return SecretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateLlm(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        if (!document.Llm.Exists)
        {
            messages.Add(ValidationMessage.Warning(
                ValidationCodes.LlmSectionMissing, LlmNotConfiguredWarning, LlmSection.SectionPath));
        }

        // The section as the configuration binds it, whatever the case of its key ("llm"): the run
        // reads it all the same, and judges every key of it.
        if (LlmProviderRules.SectionOf(document) is not { } section)
            return;

        // STUDIO-55: the default is read as strictly as a profile at start (GAP-40) — its shape is
        // judged the same way, then each entry's.
        ValidateLlmShape(section, LlmSection.SectionPath, messages);
        messages.AddRange(LlmProviderRules.Check(section, LlmSection.SectionPath));
        ValidateInlineKey(document, section, messages);
        ValidateProfiles(section, messages);
        ValidateModelCapabilities(section, messages);
    }

    // The switches of an Llm:ModelCapabilities entry, read as true or false at start.
    private static readonly string[] ModelCapabilityBooleanKeys = ["AudioInput", "AudioOutput", "Vision"];

    private const string ModelCapabilityKeyList = "AudioInput, AudioOutput, Vision, ReasoningEfforts and Thinking";

    private const string SingleValue = "a single value, not a list or an object";

    /// <summary>
    /// <c>Llm:ModelCapabilities:&lt;model&gt;</c> (or <c>&lt;provider&gt;/&lt;model&gt;</c>), judged as the run
    /// reads it at start (STUDIO-55), on the tree its configuration sees (<see cref="SettingNode"/>: a key
    /// split at <c>:</c>, an array keyed by index). The values, as <c>LlmSettings.ReadModelCapabilities</c>
    /// reads them: a switch that is not true or false refuses the start, and so does a <c>Thinking</c>
    /// that names no thinking support (<c>None</c>, <c>EffortOnly</c>, <c>Toggle</c>, <c>Budget</c>,
    /// without case; a comma or a number is refused, #356). The keys, as the host's settings check walks
    /// them (GAP-40): an entry carries its five keys and no other, so a typo refuses the start, and so does
    /// a model id holding a <c>:</c> (<c>llama3.2:latest</c> is read as the key <c>latest</c> of the entry
    /// <c>llama3.2</c>); a switch, <c>Thinking</c> and each item of <c>ReasoningEfforts</c> are single
    /// values, so a list or an object that holds anything there refuses the start; an entry is a section of
    /// keys, so a single value written for it refuses the start too (Orkeon #454). An empty value, a JSON
    /// null, <c>[]</c> and <c>{}</c> read as left out; a switch written as blanks alone is refused. The text of a <c>ReasoningEfforts</c> item is not
    /// judged: the run takes a vendor value (<c>none</c>, <c>minimal</c>).
    /// An entry under a blank id - <c>""</c>, <c>" "</c>, or what a key that starts with <c>:</c> is
    /// split into - refuses the start whatever it holds: the run's model table takes no blank id.
    /// <para>
    /// Before any of it, the file must load: two keys that come to one same path - equal but for the
    /// case, or once split at <c>:</c> - and both hold a value make the JSON configuration throw on a
    /// duplicate key (<see cref="SettingNode.WrittenTwice"/>), an error at that path. Two objects that
    /// set no same key are read as one, without a word.
    /// </para>
    /// </summary>
    private static void ValidateModelCapabilities(JsonObject section, List<ValidationMessage> messages)
    {
        if (LlmModelCapabilityOverrides.Read(section) is not { } models)
            return;

        // The ids written with a ':': the unknown key such an id is split into gets a remedy.
        var split = Child(section, LlmModelCapabilityOverrides.Key) is JsonObject written
            ? written.Select(property => property.Key).Where(id => id.Contains(':', StringComparison.Ordinal)).ToList()
            : [];

        foreach (var entry in models.Children)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                messages.Add(ValidationMessage.Error(
                    ValidationCodes.InvalidFieldType,
                    $"'{entry.Path}' is an entry under a blank model id, which the run refuses to start on, whatever the entry holds. " +
                    "Key the entry by a model id (or <provider>/<model>), or remove it.",
                    entry.Path));
            }

            else if (!entry.HasChildren && entry.Value is { Length: > 0 })
            {
                // An entry is a section of keys: a single value there binds nothing, and the host's
                // start refuses it (Orkeon #454). The value is not echoed, as the engine does not.
                messages.Add(ValidationMessage.Error(
                    ValidationCodes.InvalidFieldType,
                    $"'{entry.Path}' is a single value where a section of keys is expected: write its keys, or leave the key out.",
                    entry.Path));
            }

            foreach (var field in entry.Children)
            {
                if (ModelCapabilityBooleanKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase))
                {
                    // Blanks alone are no boolean to the binder: only an empty text reads as left out (Orkeon #454).
                    if (field.HasChildren)
                        messages.Add(WrongType(field.Path, SingleValue));
                    else if (field.Value is { Length: > 0 } raw && !bool.TryParse(raw, out _))
                        messages.Add(WrongType(field.Path, "true or false"));
                }
                else if (string.Equals(field.Key, "Thinking", StringComparison.OrdinalIgnoreCase))
                {
                    if (field.HasChildren)
                    {
                        messages.Add(WrongType(field.Path, SingleValue));
                    }
                    else if (field.Text is { } thinking && !LlmModelCapabilityOverrides.TryParseThinking(thinking, out _))
                    {
                        // The engine's sentence (Orkeon #465): the entry's path and the key, never the value.
                        messages.Add(ValidationMessage.Error(
                            ValidationCodes.InvalidFieldType,
                            $"{entry.Path}:Thinking is not a thinking support: write one of {string.Join(", ", Enum.GetNames<ThinkingSupport>())}.",
                            field.Path));
                    }
                }
                else if (string.Equals(field.Key, "ReasoningEfforts", StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Children.FirstOrDefault(item => item.HasChildren) is { } nested)
                    {
                        messages.Add(ValidationMessage.Error(
                            ValidationCodes.InvalidFieldType,
                            $"'{field.Path}' must be a list of single values: '{nested.Path}' is a list or an object.",
                            field.Path));
                    }
                }
                else
                {
                    // Only the key that comes from the split: a typo beside it is no colon's doing.
                    var prefix = $"{entry.Key}:{field.Key}";
                    messages.Add(UnknownModelCapabilityKey(entry, field, split.Find(id =>
                        id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && (id.Length == prefix.Length || id[prefix.Length] == ':'))));
                }
            }
        }

        // Whatever the key: the configuration throws on the second value, and reads nothing of the file.
        foreach (var twice in models.WrittenTwice)
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.InvalidFieldType,
                $"'{twice.Path}' is written twice, under keys the run reads as one (equal but for the case, or once split at ':'): " +
                "the configuration refuses a key written twice, so no run can read this file. Keep one.",
                twice.Path));
        }
    }

    private static ValidationMessage UnknownModelCapabilityKey(SettingNode entry, SettingNode field, string? idWithColon)
    {
        var message = $"'{field.Path}' is not a setting: '{entry.Path}' carries {ModelCapabilityKeyList}.";
        if (idWithColon is not null)
        {
            message += $" The run splits the model id '{idWithColon}' at its ':' and reads '{field.Key}' as a key of '{entry.Key}': " +
                       $"key the entry '{entry.Key}' instead, which covers '{idWithColon}'.";
        }

        return ValidationMessage.Error(ValidationCodes.InvalidFieldType, message, field.Path);
    }

    /// <summary>
    /// A <c>WorkspaceId</c> (Orkeon #468) on a section whose provider is not Anthropic: the run does not
    /// send it and warns, in the remedy said here. The provider is read as the run builds it: the mock a
    /// section names, else the one its address reads as, else - no address - its model's name, then the
    /// prefix of the key written in the file, else OpenAI (<see cref="LlmEndpointReading.Of"/>). A
    /// section that names another provider (a module, or a name the start refuses) is not judged, nor
    /// one whose address the start refuses: no provider is built.
    /// </summary>
    private static void ValidateWorkspace(JsonObject section, string sectionPath, List<ValidationMessage> messages)
    {
        if (Child(section, LlmProfilesSection.WorkspaceIdKey) is not JsonValue workspaceNode
            || string.IsNullOrWhiteSpace(AppSettingsDocument.ReadString(workspaceNode)))
        {
            return;
        }

        var address = ConfigurationText(section, "BaseUrl");
        if (!string.IsNullOrWhiteSpace(address) && LlmEndpointReading.Of(address, null, null).Notice == LlmEndpointNotice.Refused)
            return;

        string provider;
        if (ConfigurationText(section, LlmProviderRules.ProviderKey) is { } named && !string.IsNullOrWhiteSpace(named))
        {
            if (!string.Equals(named.Trim(), LlmProviderKeys.Mock, StringComparison.OrdinalIgnoreCase))
                return;
            provider = LlmProviderKeys.Mock;
        }
        else
        {
            var model = ConfigurationText(section, "Model");
            provider = LlmEndpointReading.Of(
                address, string.IsNullOrWhiteSpace(model) ? null : model, ConfigurationText(section, "ApiKey")).ProviderKey;
        }

        if (provider == LlmProviderKeys.Anthropic)
            return;

        var path = $"{sectionPath}:{LlmProfilesSection.WorkspaceIdKey}";
        messages.Add(ValidationMessage.Warning(ValidationCodes.LlmWorkspace, WorkspaceNotSent(path, provider), path));
    }

    /// <summary>The single value at <paramref name="relativePath"/>, in the text the JSON configuration gives the run; null for none.</summary>
    private static string? ConfigurationText(JsonObject section, string relativePath) =>
        Child(section, relativePath) is JsonValue value ? EmailAccountRules.ConfigurationText(value) : null;

    /// <summary>The warning of a workspace the provider does not send, ending on the run's own remedy.</summary>
    public static string WorkspaceNotSent(string path, string provider) =>
        $"{path} is not sent: the provider of this section is '{provider}'. "
        + "Only Anthropic scopes a key to a workspace; remove Llm:WorkspaceId (or the profile's) for this provider.";

    /// <summary>
    /// What the run will make of an absolute <c>BaseUrl</c> of a section of the <c>Llm</c> shape: an
    /// error for an address it refuses, a notice for one it reads without a word - unless the section
    /// names its <c>Provider</c>, which the run then builds without reading the address.
    /// </summary>
    private static void ValidateEndpointReading(JsonObject section, string sectionPath, List<ValidationMessage> messages)
    {
        if (Child(section, "BaseUrl") is not JsonValue addressNode
            || AppSettingsDocument.ReadString(addressNode) is not { } address
            || !LlmSection.IsAbsoluteHttpUrl(address)
            || StudioEndpointReading.Of(address) is not { } reading)
        {
            return;
        }

        var path = $"{sectionPath}:BaseUrl";
        if (reading.Notice == LlmEndpointNotice.Refused)
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.LlmEndpoint,
                OperatorMessages.LlmVendorAnthropicPathRefused(path, address, reading.ProviderKey),
                path));
        }
        else if (reading.Notice == LlmEndpointNotice.Silent
                 && string.IsNullOrWhiteSpace(ConfigurationText(section, LlmProviderRules.ProviderKey)))
        {
            // A section that names its provider is not read by its address: the run says nothing.
            messages.Add(ValidationMessage.Information(
                ValidationCodes.LlmEndpoint, OperatorMessages.LlmBaseUrlSilent(path, address), path));
        }
    }

    /// <summary>
    /// The keys of a section of the <c>Llm</c> shape, judged as the run reads them at start
    /// (<c>LlmSettings</c>, STUDIO-55): strings, a finite temperature, three integers, two switches, an
    /// absolute http(s) address and a variable's name. A key written as <c>[]</c> or <c>{}</c> is
    /// read as left out, as the run reads it; a list or an object that holds anything, even a null,
    /// is refused under each of them. A variable reference is judged apart, by
    /// <see cref="ValidateSettingPlaceholders"/>.
    /// One error per field, at its path; never the value of <c>ApiKeyEnvVar</c> or <c>ApiKey</c>,
    /// which may be a key pasted in the wrong field.
    /// </summary>
    private static void ValidateLlmShape(JsonObject section, string sectionPath, List<ValidationMessage> messages)
    {
        // A list or an object that holds anything puts keys under a single value, which the host's
        // settings check refuses (GAP-40). An empty one is read as left out (Written), and the run starts.
        foreach (var key in LlmStringKeys.Where(key => Written(section, key) is JsonObject or JsonArray))
            messages.Add(WrongType($"{sectionPath}:{key}", "a string"));

        if (Written(section, "Temperature") is { } temperature
            && !(AppSettingsDocument.ReadDouble(temperature) is { } value && double.IsFinite(value)))
        {
            messages.Add(WrongType($"{sectionPath}:Temperature", "a finite number"));
        }

        foreach (var key in LlmIntegerKeys.Where(key => Written(section, key) is { } node && AppSettingsDocument.ReadInt32(node) is null))
            messages.Add(WrongType($"{sectionPath}:{key}", "a whole number"));

        foreach (var key in LlmBooleanKeys.Where(key => Written(section, key) is { } node && AppSettingsDocument.ReadBoolean(node) is null))
            messages.Add(WrongType($"{sectionPath}:{key}", "true or false"));

        // A window of zero or less would leave the agent's loop no room: the host refuses to start.
        if (Written(section, LlmProfilesSection.ContextWindowTokensKey) is { } windowNode
            && LlmSection.ContextWindowProblem(sectionPath, AppSettingsDocument.ReadInt32(windowNode)) is { } window)
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.InvalidFieldType, window, $"{sectionPath}:{LlmProfilesSection.ContextWindowTokensKey}"));
        }

        if (Child(section, "BaseUrl") is JsonValue baseUrlNode
            && AppSettingsDocument.ReadString(baseUrlNode) is { } baseUrl
            && !string.IsNullOrWhiteSpace(baseUrl)
            && !LlmSection.IsAbsoluteHttpUrl(baseUrl))
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.InvalidFieldType,
                string.Create(CultureInfo.InvariantCulture, $"'{baseUrl}' is not an absolute http(s) URL."),
                $"{sectionPath}:BaseUrl"));
        }

        ValidateEndpointReading(section, sectionPath, messages);
        ValidateWorkspace(section, sectionPath, messages);
        ValidateCache(section, sectionPath, messages);
        ValidateThinkingEffort(section, sectionPath, messages);

        if (Child(section, ConfigurationKeys.LlmApiKeyEnvVar) is JsonValue referenceNode
            && !LlmSection.IsVariableName(AppSettingsDocument.ReadString(referenceNode)))
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.InvalidFieldType,
                $"'{sectionPath}:{ConfigurationKeys.LlmApiKeyEnvVar}' must be the name of an environment variable " +
                "(no '=', space or line break) — never the key itself.",
                $"{sectionPath}:{ConfigurationKeys.LlmApiKeyEnvVar}"));
        }

    }

    /// <summary>
    /// <c>Cache:Ttl</c> of a section of the <c>Llm</c> shape, judged by the Domain's
    /// <see cref="LlmCacheConfig.Request"/> as the run reads it: a lifetime other than <c>5m</c> or
    /// <c>1h</c>, or one set with no breakpoint marked, is an error at <c>Cache:Ttl</c>.
    /// </summary>
    private static void ValidateCache(JsonObject section, string sectionPath, List<ValidationMessage> messages)
    {
        if (Child(section, "Cache:Ttl") is not JsonValue ttlNode
            || AppSettingsDocument.ReadString(ttlNode)?.Trim() is not { Length: > 0 } ttl)
        {
            return;
        }

        var systemPrompt = Child(section, "Cache:SystemPrompt") is { } s && AppSettingsDocument.ReadBoolean(s) == true;
        var tools = Child(section, "Cache:Tools") is { } t && AppSettingsDocument.ReadBoolean(t) == true;
        try
        {
            LlmCacheConfig.Request(systemPrompt, tools, ttl);
        }
        catch (ArgumentException ex)
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.InvalidFieldType, ex.Message, $"{sectionPath}:Cache:Ttl"));
        }
    }

    /// <summary>
    /// <c>Thinking:Effort</c> of a section of the <c>Llm</c> shape, judged by the Domain's
    /// <see cref="LlmThinkingConfig.ValidateEffort"/>: an unknown, mis-cased or padded level is an
    /// error. A value made of whitespace alone is unset, as the run reads it.
    /// </summary>
    private static void ValidateThinkingEffort(JsonObject section, string sectionPath, List<ValidationMessage> messages)
    {
        if (Child(section, "Thinking:Effort") is not JsonValue effortNode
            || AppSettingsDocument.ReadString(effortNode) is not { } effort
            || string.IsNullOrWhiteSpace(effort))
        {
            return;
        }

        try
        {
            LlmThinkingConfig.ValidateEffort(effort);
        }
        catch (ArgumentException ex)
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.InvalidFieldType, ex.Message, $"{sectionPath}:Thinking:Effort"));
        }
    }

    /// <summary>
    /// CH-15: every string of the document at a path the run refuses a variable reference at
    /// (<see cref="SettingPlaceholders"/>, keys compared without case), judged by the Domain's
    /// recognition. One blocking error per value at its path, which replaces any other finding on
    /// that value (a name-shape or URL error says less than the reference does).
    /// </summary>
    private static void ValidateSettingPlaceholders(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        var refused = new List<ValidationMessage>();
        CollectPlaceholders(document.Root, string.Empty, refused);
        foreach (var message in refused)
        {
            messages.RemoveAll(other => string.Equals(other.Path, message.Path, StringComparison.OrdinalIgnoreCase));
            messages.Add(message);
        }
    }

    private static void CollectPlaceholders(JsonNode? node, string path, List<ValidationMessage> refused)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                    CollectPlaceholders(child, path.Length == 0 ? key : $"{path}:{key}", refused);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    CollectPlaceholders(array[i], string.Create(CultureInfo.InvariantCulture, $"{path}:{i}"), refused);
                break;
            case JsonValue leaf when SettingPlaceholders.CorrectionFor(path) is { } correction
                && AppSettingsDocument.ReadString(leaf) is { } text
                && SettingPlaceholder.TryMatch(text, out _):
                refused.Add(PlaceholderRefusal(path, correction, text));
                break;
            default:
                break;
        }
    }

    private static ValidationMessage PlaceholderRefusal(string path, string correction, string text)
    {
        var segments = path.Split(':');
        if (segments.Length > 1
            && string.Equals(segments[0], LlmSection.SectionPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^1], "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationMessage.Error(
                ValidationCodes.LlmApiKeyPlaceholder,
                $"'{path}' is a ${{…}} placeholder, which the engine never expands — the text itself " +
                "would be sent as the key, and the run refuses to start. Name the variable that holds the key " +
                $"instead, in place of ApiKey: \"{ConfigurationKeys.LlmApiKeyEnvVar}\": \"{LlmSection.PlaceholderVariable(text)}\".",
                path);
        }

        return ValidationMessage.Error(
            ValidationCodes.SettingPlaceholder, OperatorMessages.SettingPlaceholderRefused(path, correction), path);
    }

    /// <summary>The information on a key written in clear in <c>Llm</c> — a placeholder is an error instead.</summary>
    private static void ValidateInlineKey(AppSettingsDocument document, JsonObject section, List<ValidationMessage> messages)
    {
        if (Child(section, "ApiKey") is JsonObject or JsonArray)
            return;

        if (document.Llm.ApiKey is { Length: > 0 } apiKey
            && !LlmSection.IsKeyPlaceholder(apiKey)
            && !string.Equals(apiKey, LlmPresets.DockerModelRunnerApiKeyPlaceholder, StringComparison.Ordinal))
        {
            // STUDIO-49: a key the configuration resolves wins, so this one also masks the
            // variable Llm:ApiKeyEnvVar names — and no election removes it: it is not Studio's.
            messages.Add(ValidationMessage.Information(
                ValidationCodes.InlineApiKey,
                "The API key is stored in clear text in this file, and it masks Llm:ApiKeyEnvVar: a key " +
                "the configuration holds wins over the variable the reference names. Prefer removing it — " +
                $"name the variable that holds the key in ApiKeyEnvVar, or set {LlmPresets.DefaultApiKeyEnv}.",
                "Llm:ApiKey"));
        }

        if (string.Equals(document.Llm.ApiKey, LlmPresets.DockerModelRunnerApiKeyPlaceholder, StringComparison.Ordinal)
            && document.Llm.ApiKeyEnvVar is { Length: > 0 })
        {
            // STUDIO-54: alone, Docker Model Runner's placeholder is no key — its server checks none.
            // Beside a reference — a file edited by hand: an election takes it out —, it masks the
            // variable the reference names, and the run sends it in place of the key.
            messages.Add(ValidationMessage.Information(
                ValidationCodes.InlineApiKey,
                $"The placeholder key '{LlmPresets.DockerModelRunnerApiKeyPlaceholder}' masks Llm:ApiKeyEnvVar: a key " +
                "the configuration holds wins over the variable the reference names, so a run sends the " +
                "placeholder in place of the key. Remove ApiKey — only a Docker Model Runner endpoint takes it, " +
                "and that endpoint needs no variable.",
                "Llm:ApiKey"));
        }
    }

    /// <summary>
    /// <c>Llm:Profiles</c> (STUDIO-55): an object of entries, each an object of the <c>Llm</c> shape,
    /// none named <c>default</c> — the run refuses to start on any of them. Read from the nodes, not by
    /// path: an entry's name is spelt as the file holds it.
    /// </summary>
    private static void ValidateProfiles(JsonObject section, List<ValidationMessage> messages)
    {
        switch (Child(section, ConfigurationKeys.LlmProfiles))
        {
            case null:
                return;
            case not JsonObject:
                messages.Add(WrongType(LlmProfilesSection.SectionPath, "an object of profiles keyed by name"));
                return;
            case JsonObject profiles:
                foreach (var (id, entry) in profiles)
                    ValidateProfile(id, entry, messages);
                return;
        }
    }

    private static void ValidateProfile(string id, JsonNode? entry, List<ValidationMessage> messages)
    {
        var path = $"{LlmProfilesSection.SectionPath}:{id}";
        if (LlmProfilesSection.IsDefault(id))
        {
            messages.Add(ValidationMessage.Error(
                ValidationCodes.LlmProfileReservedName,
                $"The model profile '{id}' carries the reserved name of the default profile, which is the " +
                $"{LlmSection.SectionPath} section itself: the run refuses to start. Rename the profile.",
                path));
        }

        if (Orkeon.Application.Interfaces.Ports.LlmProfiles.IsAuto(id))
        {
            // The name of the pick itself (llm.profile: auto): the run refuses to start.
            messages.Add(ValidationMessage.Error(
                ValidationCodes.LlmProfileReservedName,
                Orkeon.Application.Interfaces.Ports.LlmProfiles.AutoReservedMessage(path),
                path));
        }

        if (entry is JsonObject shape)
        {
            ValidateLlmShape(shape, path, messages);
            messages.AddRange(LlmProviderRules.Check(shape, path));
        }
        else
            messages.Add(WrongType(path, "an object of the Llm section's keys"));
    }

    /// <summary>
    /// The node at <paramref name="relativePath"/> under <paramref name="section"/>, keys compared
    /// without case as the configuration binds them; null when absent.
    /// </summary>
    private static JsonNode? Child(JsonObject section, string relativePath)
    {
        JsonNode? current = section;
        foreach (var segment in relativePath.Split(':'))
        {
            if (current is not JsonObject container)
                return null;
            current = container.FirstOrDefault(property => string.Equals(property.Key, segment, StringComparison.OrdinalIgnoreCase)).Value;
            if (current is null)
                return null;
        }

        return current;
    }

    /// <summary>
    /// <see cref="Child"/>, an empty object or list read as absent: the run's configuration holds no
    /// value under such a key, and reads the setting as left out.
    /// </summary>
    private static JsonNode? Written(JsonObject section, string relativePath) =>
        Child(section, relativePath) is { } node and not (JsonObject { Count: 0 } or JsonArray { Count: 0 }) ? node : null;

    private static void ValidateTypes(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        // A node that parses as the declared kind is silent; an absent one is silent too --
        // only a value of the wrong shape is reported, one message per field.
        messages.AddRange(StringFields
            .Where(path => document.GetNode(path) is JsonObject or JsonArray)
            .Select(path => WrongType(path, "a string")));

        messages.AddRange(IntegerFields
            .Where(path => document.GetNode(path) is not null && document.GetInt32(path) is null)
            .Select(path => WrongType(path, "a whole number")));

        messages.AddRange(BooleanFields
            .Where(path => document.GetNode(path) is not null && document.GetBoolean(path) is null)
            .Select(path => WrongType(path, "true or false")));

        var mountsNode = document.GetNode(MountsSection.SectionPath);
        if (mountsNode is not null and not JsonArray)
            messages.Add(WrongType(MountsSection.SectionPath, "an array of mount strings"));

        var logLevelsNode = document.GetNode(LoggingSection.SectionPath);
        if (logLevelsNode is not null and not JsonObject)
            messages.Add(WrongType(LoggingSection.SectionPath, "an object mapping categories to levels"));
    }

    private static void ValidateRagProfile(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        if (document.Rag.HasValidProfile)
            return;

        messages.Add(ValidationMessage.Error(
            ValidationCodes.UnknownRagProfile,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Unknown RAG profile '{document.Rag.Profile}'. Known profiles: " +
                $"{string.Join(", ", RagSection.KnownProfiles)}."),
            $"{RagSection.SectionPath}:Profile"));
    }

    /// <summary>
    /// <c>Orkeon:Rag:LlmProfile</c> (GAP-19) must name a profile the host defines: an unknown one
    /// refuses the start of every run. A warning, not an error — like WIN-01, the profile may come
    /// from the environment the run is launched with (<c>ORKEON_Llm__Profiles__&lt;id&gt;__*</c>,
    /// which is how Studio passes its own model settings), and Studio sees the file alone.
    /// </summary>
    private static void ValidateRagLlmProfile(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        if (document.GetNode(RagSection.LlmProfilePath) is not JsonValue
            || document.Rag.LlmProfile is not { } profile
            || LlmProfilesSection.IsDefault(profile)
            || document.Llm.Profiles.Find(profile) is not null)
        {
            return;
        }

        var known = new[] { LlmProfilesSection.DefaultProfile }.Concat(document.Llm.Profiles.Ids);
        messages.Add(ValidationMessage.Warning(
            ValidationCodes.UnknownRagLlmProfile,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The RAG subsystem names the LLM profile '{profile.Trim()}', which this file does not define: " +
                $"the host refuses to start unless the environment the run is launched with defines it. " +
                $"Profiles in this file: {string.Join(", ", known)}."),
            RagSection.LlmProfilePath));
    }

    private void ValidateMounts(AppSettingsDocument document, List<ValidationMessage> messages)
    {
        var entries = document.Mounts.RawEntries;

        if (entries.Count == 0)
        {
            // A warning, while editing and when saving alike (STUDIO-57). The runner builds no
            // file system when nothing is mounted and runs the crew all the same: a team that
            // reads a mail and answers a mail needs no folder, and a team's own folders travel
            // in its sidecar and its launcher, never in this file. Saving used to block here,
            // on the claim that the runtime refuses to start — it does not.
            messages.Add(ValidationMessage.Warning(
                ValidationCodes.MountsEmpty,
                "No file system mount is declared: the agents see no folder of yours. A team that " +
                "reads or writes no file needs none, and a team's own folders travel with it.",
                MountsSection.SectionPath));
            return;
        }

        messages.AddRange(_mounts.Validate(entries, requireAtLeastOne: false));
    }

    private static ValidationMessage WrongType(string path, string expected) =>
        ValidationMessage.Error(
            ValidationCodes.InvalidFieldType,
            string.Create(CultureInfo.InvariantCulture, $"'{path}' must be {expected}."),
            path);
}
