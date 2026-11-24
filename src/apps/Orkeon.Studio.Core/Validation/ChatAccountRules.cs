using System.Globalization;
using System.Text.Json.Nodes;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.Configuration;
using static Orkeon.Studio.Core.Configuration.ChatSection.Values;
using Keys = Orkeon.Studio.Core.Configuration.ChatSection.Keys;

namespace Orkeon.Studio.Core.Validation;

/// <summary>
/// The rules the engine applies to a chat account, spelt again in Core on the model of
/// <see cref="DriveAccountRules"/>: what <c>orkeon run</c> will say of the account when a tool or
/// <c>orkeon chat</c> names it. A broken account stops no run — the engine sets it aside, with its
/// problems —, so every finding is a warning, in the engine's own sentence, at the key that fixes it.
/// </summary>
/// <remarks>
/// Core does not reference <c>Orkeon.Tools.Chat</c>: the sentences and the order of the checks
/// are copies of <c>ChatOptionsBinder</c> and <c>ChatAccountResolver</c>, and
/// <c>ChatValidationOracleTests</c> holds each of them to the real binder and the real resolver.
/// </remarks>
public static class ChatAccountRules
{
    /// <summary>The tenant of an account that names none: any work or school account.</summary>
    internal const string DefaultTenant = "organizations";

    /// <summary>The longest account name the engine accepts.</summary>
    private const int MaxNameLength = 64;

    private const int MaxPlaceLength = 1000;
    private const int MaxIdLength = 400;

    private const string MethodKey = Keys.Auth + ":" + Keys.Method;
    private const string ClientIdKey = Keys.Auth + ":" + Keys.ClientId;
    private const string TenantKey = Keys.Auth + ":" + Keys.Tenant;
    private const string AdminGrantedKey = Keys.Auth + ":" + Keys.AdminGranted;
    private const string CertificateKey = Keys.Auth + ":" + Keys.CertificatePathEnvVar;
    private const string FederatedTokenKey = Keys.Auth + ":" + Keys.FederatedTokenFileEnvVar;
    private const string ClientSecretKey = Keys.Auth + ":" + Keys.ClientSecretEnvVar;
    private const string UrlEnvVarKey = Keys.Webhook + ":" + Keys.UrlEnvVar;
    private const string ReadPlacesKey = Keys.Read + ":" + Keys.AllowedPlaces;
    private const string SendPlacesKey = Keys.Send + ":" + Keys.AllowedPlaces;
    private const string MaxPerHourKey = Keys.Send + ":" + Keys.MaxPerHour;
    private const string WithholdRejectedKey = Keys.Screening + ":" + Keys.WithholdRejected;

    private const string CapProblem = "Send:MaxPerHour must be positive";
    private const string PlaceSyntax = "write team:<team>, team:<team>/channel:<channel>, chat:<id>, user:<address>, user:*@<domain> or *";

    /// <summary>How the binder reads a setting that is not a string.</summary>
    private enum Kind
    {
        Provider,
        Rights,
        Method,
        Cloud,
        Integer,
    }

    /// <summary>What a place designates, as the engine's <c>PlaceForm</c> names it.</summary>
    private enum PlaceForm
    {
        Any,
        Team,
        Chat,
        Person,
    }

    /// <summary>
    /// Every account setting the binder converts, in the order it checks them. An enumeration may
    /// be empty, which reads as absent; <c>Rights</c> and the cap may not.
    /// </summary>
    private static readonly (string Key, Kind Kind, bool Optional)[] TypedSettings =
    [
        (Keys.Provider, Kind.Provider, true),
        (Keys.Rights, Kind.Rights, false),
        (MethodKey, Kind.Method, true),
        (Keys.Cloud, Kind.Cloud, true),
        (MaxPerHourKey, Kind.Integer, false),
    ];

    /// <summary>The keys an account carries, in the order the engine declares them.</summary>
    internal static readonly string[] AccountKeys =
        [Keys.Provider, Keys.Rights, Keys.Auth, Keys.Webhook, Keys.Cloud, Keys.Read, Keys.Send];

    /// <summary>The keys the section itself carries, in the order the engine declares them.</summary>
    internal static readonly string[] SectionKeys =
        [Keys.DefaultAccount, Keys.CredentialsDirectory, Keys.Accounts, Keys.Screening];

    /// <summary>The keys each object of the section or of an account carries, by the object's key.</summary>
    internal static readonly Dictionary<string, string[]> ObjectKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [Keys.Screening] = [Keys.WithholdRejected],
        [Keys.Auth] =
        [
            Keys.Method, Keys.ClientId, Keys.Tenant, Keys.AdminGranted,
            Keys.CertificatePathEnvVar, Keys.FederatedTokenFileEnvVar, Keys.ClientSecretEnvVar,
        ],
        [Keys.Webhook] = [Keys.UrlEnvVar],
        [Keys.Read] = [Keys.AllowedPlaces],
        [Keys.Send] = [Keys.AllowedPlaces, Keys.MaxPerHour],
    };

    private static readonly string[] ListKeys = [Keys.AllowedPlaces, Keys.AdminGranted];

    /// <summary>
    /// The findings of an account as <paramref name="definition"/> holds it, under
    /// <paramref name="name"/>: empty when the engine will use the account. A value the engine
    /// cannot read (a provider it does not know, rights that are no list of rights) is all it says
    /// of the account, as the engine then reads nothing else of it.
    /// </summary>
    public static IReadOnlyList<ValidationMessage> Check(string name, ChatAccountDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(definition);

        var findings = new Findings($"{ChatSection.AccountsPath}:{name}");
        foreach (var (key, kind, optional) in TypedSettings)
        {
            if (Unreadable(key, kind, optional, WrittenText(definition, key)) is { } problem)
                findings.Add(ValidationCodes.ChatValue, key, problem);
        }

        if (findings.Messages.Count == 0)
            Resolve(name, definition, findings);

        return findings.Messages;
    }

    /// <summary>
    /// The findings of the account <paramref name="name"/> (its exact spelling) as the file holds
    /// it, which is what the run reads: what the section itself holds that the engine does not read
    /// (<see cref="SectionProblems"/>), which sets every account aside, then a key no account
    /// carries and a value the binder cannot convert, and the engine then says nothing else of the
    /// account; otherwise the rules of <see cref="Check(string, ChatAccountDefinition)"/>. Keys
    /// equal but for the case (<see cref="ChatTwinKeys"/>) come before all of it and alone. Empty
    /// when the file holds no such account.
    /// </summary>
    public static IReadOnlyList<ValidationMessage> Check(AppSettingsDocument document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(name);

        if (document.Chat.AccountsNode is not { } accounts || !accounts.TryGetPropertyValue(name, out var node))
            return [];

        // What stands under the name may be no object at all: the engine then reads an account
        // that declares nothing.
        var account = node as JsonObject;
        if (account is not null && ChatTwinKeys.Under(account, $"{ChatSection.AccountsPath}:{name}").ToList() is { Count: > 0 } twins)
            return twins;

        var findings = new Findings($"{ChatSection.AccountsPath}:{name}");
        foreach (var problem in SectionProblems(document))
            findings.Add(problem.Code, string.Empty, problem.Text);

        if (account is not null)
        {
            CheckKeys(account, AccountKeys, prefix: string.Empty, AccountOwner, findings);
            foreach (var (key, kind, optional) in TypedSettings)
            {
                if (Unreadable(key, kind, optional, EmailAccountRules.ConfigurationText(EmailAccountRules.Child(account, key))) is { } problem)
                    findings.Add(ValidationCodes.ChatValue, key, problem);
            }
        }

        return findings.Messages.Count > 0 ? findings.Messages : Check(name, ChatSection.ReadAccount(name, account));
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a name the engine accepts for an account (and for its
    /// token file): what a form checks before it writes an account under it.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// Whether <paramref name="text"/> is a place <c>AllowedPlaces</c> understands, read as the
    /// engine's <c>PlaceReference</c> reads it: <c>team:T</c>, <c>team:T/channel:C</c>,
    /// <c>chat:ID</c>, <c>user:ADDRESS</c>, <c>user:*@DOMAIN</c> or <c>*</c>.
    /// </summary>
    public static bool IsPlace(string? text) => TryParsePlace(text, out _, out _);

    /// <summary>
    /// The member of <see cref="ChatSection.Providers"/> the engine binds <paramref name="text"/>
    /// to, a name without regard to case, or null when it binds none: a number binds none (Orkeon #464).
    /// </summary>
    public static string? ProviderOf(string? text) => EmailAccountRules.Member(ChatSection.Providers, text);

    /// <summary>
    /// The member of <see cref="ChatSection.AuthMethods"/> the engine binds <paramref name="text"/>
    /// to — <see cref="OAuth2"/> when absent or empty —, or null when it binds none.
    /// </summary>
    public static string? MethodOf(string? text) =>
        string.IsNullOrEmpty(text) ? OAuth2 : EmailAccountRules.Member(ChatSection.AuthMethods, text);

    /// <summary>
    /// What the section itself holds that the engine does not read — a key it knows no setting for,
    /// a value where a section is expected, a screening switch that is neither true nor false —,
    /// each in its sentence and at its key: every account is set aside on it.
    /// </summary>
    internal static IReadOnlyList<ValidationMessage> SectionProblems(AppSettingsDocument document)
    {
        var findings = new Findings(ChatSection.SectionPath);
        if (document.Chat.Section is { } section)
            CheckKeys(section, SectionKeys, prefix: string.Empty, SectionOwner, findings);

        var text = EmailAccountRules.ConfigurationText(document.Chat.WithholdRejectedNode);
        if (!string.IsNullOrEmpty(text) && !bool.TryParse(text, out _))
            findings.Add(ValidationCodes.ChatScreening, WithholdRejectedKey, $"{ChatSection.SectionPath}:{WithholdRejectedKey} is neither true nor false");

        return findings.Messages;
    }

    /// <summary>The engine's resolver, rule by rule and in its order.</summary>
    private static void Resolve(string name, ChatAccountDefinition definition, Findings findings)
    {
        if (!IsValidName(name))
        {
            findings.Add(ValidationCodes.ChatName, string.Empty,
                "the account name may only hold letters, digits, '.', '_' and '-', starts with a letter or a digit, and has 64 characters at most");
        }

        var provider = ProviderOf(definition.Provider);
        if (provider is null)
            findings.Add(ValidationCodes.ChatProvider, Keys.Provider, "Provider is required: Teams or TeamsWebhook");

        if (provider == TeamsWebhook)
        {
            ResolveWebhook(definition, findings);
            return;
        }

        if (!string.IsNullOrWhiteSpace(definition.WebhookEnvVar))
            findings.Add(ValidationCodes.ChatWebhook, UrlEnvVarKey, "Webhook:UrlEnvVar goes with Provider TeamsWebhook alone");

        if (Granted(definition) == ChatRights.None)
        {
            findings.Add(ValidationCodes.ChatRights, Keys.Rights,
                "Rights is required and says what an agent may do, e.g. \"Rights\": \"Read, Send\"");
        }

        var application = MethodOf(definition.AuthMethod) == ClientCredentials;
        ResolveCredential(definition, application, findings);
        ResolveGrants(definition, findings);
        var oauth = ResolveCloud(definition, findings) && ResolveOAuth(definition, findings);
        ResolvePlaces(definition.ReadPlaces, ReadPlacesKey, findings);
        ResolvePlaces(definition.SendPlaces, SendPlacesKey, findings);
        if (application)
        {
            if (oauth)
                ResolveApplicationTenant(definition, findings);
            ResolveApplicationPlaces(definition, findings);
        }

        ResolveCap(definition, findings);
    }

    /// <summary>A webhook: the address fixes the place, so nothing but its variable and the cap is read.</summary>
    private static void ResolveWebhook(ChatAccountDefinition definition, Findings findings)
    {
        if (Granted(definition) != ChatRights.Send)
            findings.Add(ValidationCodes.ChatRights, Keys.Rights, "Rights must be Send, the one right a TeamsWebhook account has");

        ResolveVariable(definition.WebhookEnvVar, UrlEnvVarKey, ValidationCodes.ChatWebhook, findings);
        if (string.IsNullOrWhiteSpace(definition.WebhookEnvVar))
        {
            findings.Add(ValidationCodes.ChatWebhook, UrlEnvVarKey,
                "Webhook:UrlEnvVar is required: the NAME of the environment variable holding the webhook's address, never the address itself");
        }

        if (Declares(definition.ReadPlaces) || Declares(definition.SendPlaces))
        {
            findings.Add(ValidationCodes.ChatPlaces, Declares(definition.ReadPlaces) ? ReadPlacesKey : SendPlacesKey,
                "AllowedPlaces is not read for a TeamsWebhook account, whose address fixes the one place: leave Read:AllowedPlaces and Send:AllowedPlaces out");
        }

        ResolveCap(definition, findings);
    }

    /// <summary>The credential of an application account, and what such an account may not declare; the keys of one refused for a user's.</summary>
    private static void ResolveCredential(ChatAccountDefinition definition, bool application, Findings findings)
    {
        (string Key, string? Declared)[] credentials =
        [
            (CertificateKey, definition.CertificatePathEnvVar),
            (FederatedTokenKey, definition.FederatedTokenFileEnvVar),
            (ClientSecretKey, definition.ClientSecretEnvVar),
        ];
        var declared = credentials.Where(credential => !string.IsNullOrWhiteSpace(credential.Declared)).ToList();
        if (!application)
        {
            foreach (var (key, _) in declared)
                findings.Add(ValidationCodes.ChatAuth, key, $"{key} goes with Auth:Method ClientCredentials alone");
            return;
        }

        var rights = Granted(definition);
        if ((rights & ~(ChatRights.Read | ChatRights.Meetings)) != ChatRights.None)
        {
            findings.Add(ValidationCodes.ChatRights, Keys.Rights,
                "Rights holds more than Read and Meetings, and Auth:Method ClientCredentials only reads: Microsoft Graph lets no application post as a user");
        }

        if (rights.HasFlag(ChatRights.Meetings) && !rights.HasFlag(ChatRights.Read))
        {
            findings.Add(ValidationCodes.ChatRights, Keys.Rights,
                "Rights holds Meetings without Read, and an application reads a meeting through a chat it may read: add Read");
        }

        if (Declares(definition.SendPlaces))
            findings.Add(ValidationCodes.ChatPlaces, SendPlacesKey, "Send:AllowedPlaces goes with Auth:Method OAuth2 alone: an application posts nothing");

        if (definition.MaxPerHour is not null)
            findings.Add(ValidationCodes.ChatLimits, MaxPerHourKey, "Send:MaxPerHour goes with Auth:Method OAuth2 alone: an application posts nothing");

        if (Declares(definition.AdminGranted))
        {
            findings.Add(ValidationCodes.ChatAuth, AdminGrantedKey,
                "Auth:AdminGranted goes with Auth:Method OAuth2 alone: an administrator grants every permission of an application");
        }

        if (declared.Count != 1)
        {
            findings.Add(ValidationCodes.ChatAuth, MethodKey,
                "Auth:Method ClientCredentials takes exactly one of Auth:CertificatePathEnvVar, Auth:FederatedTokenFileEnvVar and Auth:ClientSecretEnvVar");
            return;
        }

        ResolveVariable(declared[0].Declared, declared[0].Key, ValidationCodes.ChatAuth, findings);
    }

    private static void ResolveGrants(ChatAccountDefinition definition, Findings findings)
    {
        var unknown = definition.AdminGranted
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Count(entry => !ChatSection.Grants.Contains(entry.Trim(), StringComparer.OrdinalIgnoreCase));
        for (var i = 0; i < unknown; i++)
            findings.Add(ValidationCodes.ChatAuth, AdminGrantedKey, $"Auth:AdminGranted holds an entry that is not one of {string.Join(", ", ChatSection.Grants)}");
    }

    private static bool ResolveCloud(ChatAccountDefinition definition, Findings findings)
    {
        if (EmailAccountRules.Member(ChatSection.Clouds, definition.Cloud) != China)
            return true;

        findings.Add(ValidationCodes.ChatCloud, Keys.Cloud, "Cloud China is not supported: Microsoft's pages give two different sign-in hosts for it");
        return false;
    }

    private static bool ResolveOAuth(ChatAccountDefinition definition, Findings findings)
    {
        var inClientId = SettingPlaceholder.TryMatch(definition.ClientId, out _);
        if (inClientId || SettingPlaceholder.TryMatch(definition.Tenant, out _))
        {
            findings.Add(ValidationCodes.ChatAuth, inClientId ? ClientIdKey : TenantKey,
                "Auth:ClientId or Auth:Tenant holds a variable reference, and Orkeon expands no variable here: write the id itself, it is not a secret");
            return false;
        }

        if (string.IsNullOrWhiteSpace(definition.ClientId))
        {
            findings.Add(ValidationCodes.ChatAuth, ClientIdKey, "Auth:ClientId is required (the id of your Microsoft Entra application)");
            return false;
        }

        var tenant = TenantOf(definition);
        if (!char.IsAsciiLetterOrDigit(tenant[0]) || !tenant.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            findings.Add(ValidationCodes.ChatAuth, TenantKey, "Auth:Tenant is not a tenant id, domain or alias");
            return false;
        }

        if (string.Equals(tenant, "consumers", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(ValidationCodes.ChatAuth, TenantKey,
                "Auth:Tenant consumers is for personal accounts, which Microsoft Graph serves no Teams chat: write your organization's tenant id");
            return false;
        }

        return true;
    }

    // An application has no home tenant to be found in: the aliases designate none.
    private static void ResolveApplicationTenant(ChatAccountDefinition definition, Findings findings)
    {
        var tenant = TenantOf(definition);
        if (string.Equals(tenant, "common", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tenant, DefaultTenant, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(ValidationCodes.ChatAuth, TenantKey,
                "Auth:Tenant is required for Auth:Method ClientCredentials: your organization's tenant id or domain, not an alias");
        }
    }

    private static void ResolvePlaces(IEnumerable<string> declared, string key, Findings findings)
    {
        foreach (var entry in declared.Where(entry => !string.IsNullOrWhiteSpace(entry) && !IsPlace(entry)))
            findings.Add(ValidationCodes.ChatPlaces, key, $"{key} entry '{entry.Trim()}' is not a place: {PlaceSyntax}");
    }

    private static void ResolveApplicationPlaces(ChatAccountDefinition definition, Findings findings)
    {
        foreach (var problem in definition.ReadPlaces.Select(ApplicationPlaceProblem).OfType<string>())
            findings.Add(ValidationCodes.ChatPlaces, ReadPlacesKey, problem);
    }

    /// <summary>Why an application may not read a place; null when it may, or when the entry is no place.</summary>
    private static string? ApplicationPlaceProblem(string entry) =>
        !TryParsePlace(entry, out var form, out var value)
            ? null
            : form switch
            {
                PlaceForm.Any => "Read:AllowedPlaces holds '*', which for Auth:Method ClientCredentials would be every conversation of the tenant: name the places",
                PlaceForm.Person => $"Read:AllowedPlaces entry '{entry.Trim()}' names a person, and an application has no one-to-one chat of its own: name the chat by its id",
                PlaceForm.Team when !IsDirectoryId(value) =>
                    $"Read:AllowedPlaces entry '{entry.Trim()}' names a team by its name, which any team of the tenant can take, and an application reaches them all: write the team's id",
                _ => null,
            };

    private static void ResolveCap(ChatAccountDefinition definition, Findings findings)
    {
        if (definition.MaxPerHour is <= 0)
            findings.Add(ValidationCodes.ChatLimits, MaxPerHourKey, CapProblem);
    }

    /// <summary>What is written there by mistake may be the secret: it is not repeated.</summary>
    private static void ResolveVariable(string? declared, string key, string code, Findings findings)
    {
        var name = declared?.Trim();
        if (string.IsNullOrEmpty(name) || IsVariableName(name))
            return;

        findings.Add(code, key, $"{key} is the NAME of an environment variable (letters, digits and '_'), never its value nor a reference to it");
    }

    /// <summary>Whether the engine takes <paramref name="name"/> for the name of an environment variable.</summary>
    internal static bool IsVariableName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>The tenant an account declares, trimmed, else the default one.</summary>
    internal static string TenantOf(ChatAccountDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.Tenant) ? DefaultTenant : definition.Tenant.Trim();

    private static ChatRights Granted(ChatAccountDefinition definition) => definition.Rights & ChatSection.AllRights;

    private static bool Declares(IEnumerable<string> entries) => entries.Any(entry => !string.IsNullOrWhiteSpace(entry));

    /// <summary>The engine's <c>PlaceReference.TryParse</c>; <paramref name="value"/> is the team of a team or of a channel.</summary>
    private static bool TryParsePlace(string? text, out PlaceForm form, out string value)
    {
        form = PlaceForm.Any;
        value = string.Empty;
        var written = text?.Trim();
        if (string.IsNullOrEmpty(written) || written.Length > MaxPlaceLength || written.Any(char.IsControl))
            return false;

        if (written == "*")
            return true;

        if (After(written, "team:") is { } team)
        {
            form = PlaceForm.Team;
            return IsTeam(team, out value);
        }

        if (After(written, "chat:") is { } chat)
        {
            form = PlaceForm.Chat;
            value = chat.Trim();
            return IsChatId(value);
        }

        if (After(written, "user:") is not { } user)
            return false;

        form = PlaceForm.Person;
        value = user.Trim();
        return value.StartsWith("*@", StringComparison.Ordinal) ? IsDomain(value[2..]) : IsDirectoryId(value) || IsAddress(value);
    }

    private static string? After(string written, string prefix) =>
        written.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? written[prefix.Length..] : null;

    private static bool IsTeam(string rest, out string team)
    {
        const string ChannelSeparator = "/channel:";
        var cut = rest.LastIndexOf(ChannelSeparator, StringComparison.OrdinalIgnoreCase);
        team = (cut < 0 ? rest : rest[..cut]).Trim();
        if (team.Length == 0 || team == "*")
            return false;

        return cut < 0 || rest[(cut + ChannelSeparator.Length)..].Trim() is not ("" or "*");
    }

    private static bool IsChatId(string value) =>
        value.Length is > 0 and <= MaxIdLength
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '@' or '.' or '_' or '-')
        && !value.Contains("@thread.tacv2", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectoryId(string value) => Guid.TryParseExact(value, "D", out _);

    private static bool IsAddress(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1
            && !value.Any(c => char.IsWhiteSpace(c) || c is '*' or '/' or '\\' or '?' or '<' or '>');
    }

    private static bool IsDomain(string value) =>
        value.Length > 2 && char.IsLetterOrDigit(value[0]) && char.IsLetterOrDigit(value[^1])
        && value.Contains('.', StringComparison.Ordinal)
        && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-');

    /// <summary>How the refusal of a key names what it belongs to.</summary>
    private sealed record KeyOwner(string Prefix, string Setting, string Name);

    private static readonly KeyOwner AccountOwner = new(string.Empty, "an account setting", "an account");

    private static readonly KeyOwner SectionOwner = new(ChatSection.SectionPath + ":", "a chat setting", ChatSection.SectionPath);

    /// <summary>
    /// The keys of <paramref name="section"/> the engine knows no setting for, at any depth, and
    /// those that hold one value where several are expected, in the order the configuration lists
    /// them (by name). Nothing is said under <c>Accounts</c>: each account is judged on its own.
    /// </summary>
    private static void CheckKeys(JsonNode section, string[] known, string prefix, KeyOwner root, Findings findings)
    {
        foreach (var (key, value) in Children(section).OrderBy(child => child.Key, StringComparer.OrdinalIgnoreCase))
        {
            var path = prefix + key;
            if (!known.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                var owner = prefix.Length == 0 ? root.Name : prefix.TrimEnd(':');
                findings.Add(ValidationCodes.ChatKey, path, $"{root.Prefix}{path} is not {root.Setting}: {owner} carries {string.Join(", ", known)}");
                continue;
            }

            var isList = ListKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
            ObjectKeys.TryGetValue(key, out var childKeys);
            if (!isList && childKeys is null && !string.Equals(key, Keys.Accounts, StringComparison.OrdinalIgnoreCase))
                continue;

            if (EmailAccountRules.ConfigurationText(value) is { Length: > 0 })
                findings.Add(ValidationCodes.ChatKey, path, $"{root.Prefix}{path} is a single value where a {(isList ? "list" : "section")} is expected");
            else if (childKeys is not null && value is not null)
                CheckKeys(value, childKeys, path + ":", root, findings);
        }
    }

    /// <summary>The children the configuration reads under <paramref name="node"/>: an array's are keyed by index.</summary>
    private static IEnumerable<KeyValuePair<string, JsonNode?>> Children(JsonNode node) => node switch
    {
        JsonObject owner => owner,
        JsonArray array => array.Select((item, index) => KeyValuePair.Create(index.ToString(CultureInfo.InvariantCulture), item)),
        _ => [],
    };

    /// <summary>
    /// The binder's sentence on a typed setting it cannot convert, or null when it reads
    /// <paramref name="text"/> — or when the key is absent, or optional and empty.
    /// </summary>
    private static string? Unreadable(string key, Kind kind, bool optional, string? text)
    {
        if (text is null || (optional && text.Length == 0))
            return null;

        return kind switch
        {
            Kind.Integer => EmailAccountRules.BindsAsInteger(text) ? null : $"{key} is not a whole number",
            Kind.Rights => EmailAccountRules.EnumerationValue(ChatSection.RightNames, text, flags: true) is { } value && (value & ~(long)ChatSection.AllRights) == 0
                ? null
                : EmailAccountRules.EnumerationProblem(key, nameof(ChatRights), ChatSection.RightNames, flags: true, text),
            _ => EmailAccountRules.Member(NamesOf(kind), text) is not null
                ? null
                : EmailAccountRules.EnumerationProblem(key, typeName: null, NamesOf(kind), flags: false, text),
        };
    }

    private static IReadOnlyList<string> NamesOf(Kind kind) => kind switch
    {
        Kind.Provider => ChatSection.Providers,
        Kind.Method => ChatSection.AuthMethods,
        _ => ChatSection.Clouds,
    };

    /// <summary>The text a typed setting holds in <paramref name="definition"/>; a number always reads.</summary>
    private static string? WrittenText(ChatAccountDefinition definition, string key) => key switch
    {
        Keys.Provider => definition.Provider,
        Keys.Rights => Granted(definition) == ChatRights.None ? definition.RightsRaw : null,
        MethodKey => definition.AuthMethod,
        Keys.Cloud => definition.Cloud,
        _ => null,
    };

    /// <summary>The findings under one path, each a warning at a key under it.</summary>
    private sealed class Findings(string path)
    {
        public List<ValidationMessage> Messages { get; } = [];

        public void Add(string code, string key, string text) =>
            Messages.Add(ValidationMessage.Warning(code, text, key.Length == 0 ? path : $"{path}:{key}"));
    }
}

/// <summary>Keys equal but for the case in one object of <c>Orkeon:Tools:Chat</c>; see <see cref="TwinKeys"/>.</summary>
internal static class ChatTwinKeys
{
    /// <summary>The keys Studio knows in the section, whatever the object that carries them, as it spells them.</summary>
    private static readonly string[] Spellings =
    [
        .. ChatAccountRules.SectionKeys, .. ChatAccountRules.AccountKeys,
        .. ChatAccountRules.ObjectKeys.Values.SelectMany(keys => keys),
    ];

    private static readonly TwinKeys Check = new(Spellings, ValidationCodes.ChatTwin);

    /// <summary>
    /// The twin keys of <paramref name="owner"/> (at <paramref name="path"/>) and of every object
    /// under it; see <see cref="TwinKeys.Under"/>.
    /// </summary>
    public static IEnumerable<ValidationMessage> Under(JsonObject owner, string path, JsonObject? accounts = null) =>
        Check.Under(owner, path, accounts);
}
