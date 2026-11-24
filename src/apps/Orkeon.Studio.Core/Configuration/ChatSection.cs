using System.Text.Json.Nodes;
using Orkeon.Constants.Configuration;
using static Orkeon.Studio.Core.Configuration.SectionNodes;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Typed view over <c>Orkeon:Tools:Chat</c>, on the model of <see cref="DriveSection"/>: the
/// accounts, keyed by name under <c>Accounts</c>, and the three settings that apply to all of them.
/// Every write lands in place, key by key, so a key Studio does not model survives an edit of the
/// account that carries it, and the engine reads the result exactly as written: nothing here
/// writes an empty string, an empty object or an empty list, because the engine reads each of them
/// as a value (an empty <c>Rights</c> sets the account aside). A field cleared removes its key, and
/// an object emptied removes itself.
/// </summary>
/// <remarks>
/// Every key inside the section is looked up without regard to case, as the configuration reads
/// it, and the spelling found in the file is kept: only a key the file lacks is created, in the
/// spelling of <see cref="Keys"/>. Account names are compared the same way, as the engine's
/// dictionary does. The names of the keys and of the enumeration values are copies of what
/// <c>Orkeon.Tools.Chat</c> binds — Core does not reference it, so as not to carry the Graph
/// client into the forms — and a test holds each of them to the engine's binder.
/// </remarks>
public sealed class ChatSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = ConfigurationKeys.ToolsChat;

    /// <summary>Configuration path of the accounts dictionary.</summary>
    public const string AccountsPath = SectionPath + ":" + Keys.Accounts;

    /// <summary>Every right the engine declares, together: a bit outside them is no right.</summary>
    internal static readonly ChatRights AllRights = Enum.GetValues<ChatRights>().Aggregate(ChatRights.None, (all, right) => all | right);

    private readonly AppSettingsDocument _document;

    internal ChatSection(AppSettingsDocument document) => _document = document;

    /// <summary>The <c>Provider</c> values the engine knows, in the spelling it binds.</summary>
    public static IReadOnlyList<string> Providers { get; } = [Values.Teams, Values.TeamsWebhook];

    /// <summary>The <c>Auth:Method</c> values the engine knows, in the spelling it binds.</summary>
    public static IReadOnlyList<string> AuthMethods { get; } = [Values.OAuth2, Values.ClientCredentials];

    /// <summary>The <c>Cloud</c> values the engine binds, <see cref="Values.China"/> included, which it then refuses.</summary>
    public static IReadOnlyList<string> Clouds { get; } = [Values.Global, Values.UsGov, Values.UsGovDoD, Values.China];

    /// <summary>The <c>Auth:AdminGranted</c> entries the engine knows, in the order it lists them.</summary>
    public static IReadOnlyList<string> Grants { get; } =
        [Values.ChannelMessages, Values.ChannelEdit, Values.MeetingsGrant, Values.ChannelMembers];

    /// <summary>The names of the rights, in the order the engine lists them and Studio writes them.</summary>
    public static IReadOnlyList<string> RightNames { get; } = ["Read", "Send", "Edit", "Delete", "React", "Meetings"];

    /// <summary>The account names, as the file spells them, in document order.</summary>
    public IReadOnlyList<string> AccountNames => AccountsNode is { } accounts ? [.. accounts.Select(property => property.Key)] : [];

    /// <summary>The account a call uses when it names none; absent means the only account.</summary>
    public string? DefaultAccount
    {
        get => ReadText(Section, Keys.DefaultAccount);
        set => WriteSectionText(Keys.DefaultAccount, value);
    }

    /// <summary>
    /// Whether a message the prompt-injection detector rejects has its text withheld
    /// (<c>Screening:WithholdRejected</c>); absent means <see langword="false"/> to the engine.
    /// Clearing it removes <c>Screening</c> too, unless a key Studio does not model lives there.
    /// </summary>
    public bool? WithholdRejected
    {
        get => AppSettingsDocument.ReadBoolean(WithholdRejectedNode);
        set
        {
            // Clearing never creates the section, and leaves alone what is no object under Screening.
            if (value is null && Node(Section, Keys.Screening) is not JsonObject)
                return;

            WriteObject(EnsureSection(), Keys.Screening, screening =>
            {
                if (value is null)
                    Remove(screening, Keys.WithholdRejected);
                else
                    Write(screening, Keys.WithholdRejected, JsonValue.Create(value.Value));
            });
        }
    }

    /// <summary>
    /// The physical directory that holds the OAuth tokens, for a host whose per-user settings
    /// directory is not the right one. A physical path, not a virtual one: no mount applies.
    /// </summary>
    public string? CredentialsDirectory
    {
        get => ReadText(Section, Keys.CredentialsDirectory);
        set => WriteSectionText(Keys.CredentialsDirectory, value);
    }

    /// <summary>
    /// The account under <paramref name="name"/> (the exact spelling first, then without regard
    /// to case), or null when the file holds no such name. A value that is no object
    /// (<c>"work": null</c>) is an account all the same, listed by <see cref="AccountNames"/> and by
    /// the engine: it reads as one with no field.
    /// </summary>
    public ChatAccountDefinition? GetAccount(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (AccountsNode is not { } accounts || FindKey(accounts, name) is not { } key)
            return null;

        return ReadAccount(key, accounts[key] as JsonObject);
    }

    /// <summary>
    /// The account <paramref name="account"/> holds under <paramref name="key"/>, whatever the key.
    /// A node that is no object reads as an account with no field.
    /// </summary>
    internal static ChatAccountDefinition ReadAccount(string key, JsonObject? account)
    {
        var auth = Node(account, Keys.Auth) as JsonObject;
        var webhook = Node(account, Keys.Webhook) as JsonObject;
        var read = Node(account, Keys.Read) as JsonObject;
        var send = Node(account, Keys.Send) as JsonObject;
        var rights = ReadText(account, Keys.Rights);
        var parsed = TryParseRights(rights, out var granted) ? granted : ChatRights.None;

        return new ChatAccountDefinition
        {
            Name = key,
            Provider = ReadText(account, Keys.Provider),
            Rights = parsed,
            RightsRaw = parsed == ChatRights.None && !string.IsNullOrWhiteSpace(rights) ? rights : null,
            AuthMethod = ReadText(auth, Keys.Method),
            ClientId = ReadText(auth, Keys.ClientId),
            Tenant = ReadText(auth, Keys.Tenant),
            AdminGranted = [.. ReadStrings(Node(auth, Keys.AdminGranted))],
            CertificatePathEnvVar = ReadText(auth, Keys.CertificatePathEnvVar),
            FederatedTokenFileEnvVar = ReadText(auth, Keys.FederatedTokenFileEnvVar),
            ClientSecretEnvVar = ReadText(auth, Keys.ClientSecretEnvVar),
            WebhookEnvVar = ReadText(webhook, Keys.UrlEnvVar),
            Cloud = ReadText(account, Keys.Cloud),
            ReadPlaces = [.. ReadStrings(Node(read, Keys.AllowedPlaces))],
            SendPlaces = [.. ReadStrings(Node(send, Keys.AllowedPlaces))],
            MaxPerHour = ReadInt(send, Keys.MaxPerHour),
        };
    }

    /// <summary>
    /// Writes the account in place under its name — a new one goes last — and elects no default:
    /// a field that is null or blank, an empty list and an object left empty remove
    /// their keys. A value the file already holds in another spelling that reads the same
    /// (<c>"read,SEND"</c>, <c>"20"</c>) is left as it is, so saving an account the user did not
    /// change changes nothing. The account object itself stays, however empty, so that it is still
    /// listed; a value that is no object stays as written while the account brings no field.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is blank, or differs from an existing account's only by case: the engine could not
    /// tell the two apart, so this is the same account and a rename is the way to respell it.
    /// </exception>
    public void SetAccount(ChatAccountDefinition account)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Name);

        var accounts = EnsureAccounts();
        var key = FindKey(accounts, account.Name);
        if (key is not null && !string.Equals(key, account.Name, StringComparison.Ordinal))
            throw new ArgumentException($"'{account.Name}' is the account '{key}' with another case: the engine reads them as one.", nameof(account));

        key ??= account.Name;
        var declared = accounts.ContainsKey(key);
        var existing = accounts[key] as JsonObject;
        var target = existing ?? [];

        WriteText(target, Keys.Provider, account.Provider);
        WriteRights(target, account);
        WriteObject(target, Keys.Auth, auth =>
        {
            WriteText(auth, Keys.Method, account.AuthMethod);
            WriteText(auth, Keys.ClientId, account.ClientId);
            WriteText(auth, Keys.Tenant, account.Tenant);
            WriteStrings(auth, Keys.AdminGranted, account.AdminGranted);
            WriteText(auth, Keys.CertificatePathEnvVar, account.CertificatePathEnvVar);
            WriteText(auth, Keys.FederatedTokenFileEnvVar, account.FederatedTokenFileEnvVar);
            WriteText(auth, Keys.ClientSecretEnvVar, account.ClientSecretEnvVar);
        });
        WriteObject(target, Keys.Webhook, webhook => WriteText(webhook, Keys.UrlEnvVar, account.WebhookEnvVar));
        WriteText(target, Keys.Cloud, account.Cloud);
        WriteObject(target, Keys.Read, read => WriteStrings(read, Keys.AllowedPlaces, account.ReadPlaces));
        WriteObject(target, Keys.Send, send =>
        {
            WriteStrings(send, Keys.AllowedPlaces, account.SendPlaces);
            WriteInt(send, Keys.MaxPerHour, account.MaxPerHour);
        });

        // What is no object under the name read as an account with no field: written back with
        // none, it is that same value.
        if (existing is null && (target.Count > 0 || !declared))
            accounts[key] = target;
    }

    /// <summary>
    /// Removes the account and everything under it; the dictionary goes when it empties, and so
    /// does <see cref="DefaultAccount"/> when it named the account and no other account answers
    /// to that name.
    /// </summary>
    public void RemoveAccount(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (AccountsNode is not { } accounts || FindKey(accounts, name) is not { } key)
            return;

        accounts.Remove(key);
        if (DefaultAccount is { } named
            && string.Equals(named, key, StringComparison.OrdinalIgnoreCase)
            && FindKey(accounts, named) is null)
        {
            DefaultAccount = null;
        }

        if (accounts.Count == 0)
            Remove(Section, Keys.Accounts);
    }

    /// <summary>
    /// Moves an account under a new name, every key it carries included, and keeps it where it
    /// stood in the document. <see cref="DefaultAccount"/> follows when it named the account.
    /// Respelling a name in another case is a rename of the same account, not a second one.
    /// </summary>
    /// <exception cref="ArgumentException">The new name is blank, or another account already answers to it.</exception>
    public void RenameAccount(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        if (AccountsNode is not { } accounts || FindKey(accounts, oldName) is not { } key)
            return;
        if (string.Equals(key, newName, StringComparison.Ordinal))
            return;
        if (accounts.Any(property => !string.Equals(property.Key, key, StringComparison.Ordinal)
                                     && string.Equals(property.Key, newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"An account already answers to '{newName}' (names are compared without regard to case).", nameof(newName));
        }

        // JsonObject has no in-place rename: lift every property out and put them back in order.
        var entries = accounts.Select(property => (property.Key, Value: property.Value?.DeepClone())).ToList();
        accounts.Clear();
        foreach (var (entryKey, value) in entries)
            accounts[string.Equals(entryKey, key, StringComparison.Ordinal) ? newName : entryKey] = value;

        if (DefaultAccount is { } named && string.Equals(named, key, StringComparison.OrdinalIgnoreCase))
            DefaultAccount = newName;
    }

    /// <summary>The section's object, or null when the file holds none.</summary>
    /// <remarks>The key is found whatever its letter case (<c>orkeon:tools:chat</c>), as the configuration binds it.</remarks>
    internal JsonObject? Section => _document.GetNode(_document.PathAsWritten(SectionPath)) as JsonObject;

    /// <summary>The accounts dictionary as the file holds it, whatever the case of its key; null when there is none.</summary>
    internal JsonObject? AccountsNode => Node(Section, Keys.Accounts) as JsonObject;

    /// <summary>What stands under <c>Screening:WithholdRejected</c>, whatever the case of the two keys.</summary>
    internal JsonNode? WithholdRejectedNode => Node(Node(Section, Keys.Screening) as JsonObject, Keys.WithholdRejected);

    private JsonObject EnsureSection()
    {
        if (Section is { } section)
            return section;

        section = [];
        _document.SetNode(_document.PathAsWritten(SectionPath), section);
        return section;
    }

    private JsonObject EnsureAccounts()
    {
        if (AccountsNode is { } accounts)
            return accounts;

        var section = EnsureSection();
        accounts = [];
        section[FindKey(section, Keys.Accounts) ?? Keys.Accounts] = accounts;
        return accounts;
    }

    /// <summary>Writes a text of the section itself; clearing it never creates the section.</summary>
    private void WriteSectionText(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            Remove(Section, key);
        else
            Write(EnsureSection(), key, JsonValue.Create(value));
    }

    /// <summary>
    /// Reads the rights the way the engine's binder does, names without regard to case, and refuses
    /// what it refuses: a number, even one whose bits are rights (Orkeon #464), and nothing granted.
    /// A file that holds a number is then read as granting nothing and kept as written.
    /// </summary>
    private static bool TryParseRights(string? text, out ChatRights rights)
    {
        rights = ChatRights.None;
        if (text is null
            || Orkeon.Studio.Core.Validation.EmailAccountRules.HoldsNumber(text)
            || !Enum.TryParse(text, ignoreCase: true, out ChatRights parsed)
            || parsed == ChatRights.None
            || (parsed & ~AllRights) != ChatRights.None)
        {
            return false;
        }

        rights = parsed;
        return true;
    }

    /// <summary>
    /// The rights as the engine reads them, <c>"Read, Send"</c>; when none is granted
    /// the text the file held (<see cref="ChatAccountDefinition.RightsRaw"/>) is written back as it was.
    /// </summary>
    private static void WriteRights(JsonObject owner, ChatAccountDefinition account)
    {
        var granted = account.Rights & AllRights;
        if (granted == ChatRights.None)
        {
            WriteText(owner, Keys.Rights, account.RightsRaw);
            return;
        }

        if (TryParseRights(ReadText(owner, Keys.Rights), out var written) && written == granted)
            return;

        var names = Enum.GetValues<ChatRights>().Where(right => right != ChatRights.None && granted.HasFlag(right));
        Write(owner, Keys.Rights, JsonValue.Create(string.Join(", ", names)));
    }

    /// <summary>
    /// The values the engine binds, one constant each, for the code that branches on one;
    /// <see cref="Providers"/> and the other lists are made of them, and a test holds each to the
    /// engine's enumeration.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1034",
        Justification = "The values read beside the keys they are written under (ChatSection.Values.TeamsWebhook); " +
                        "a top-level class would detach them from the lists a test holds to the engine.")]
    public static class Values
    {
        /// <summary>Provider: Microsoft Teams, through Microsoft Graph.</summary>
        public const string Teams = "Teams";

        /// <summary>Provider: a Teams Workflows webhook, which posts into one place.</summary>
        public const string TeamsWebhook = "TeamsWebhook";

        /// <summary>Method: a user signs in (the default).</summary>
        public const string OAuth2 = "OAuth2";

        /// <summary>Method: an application signs in as itself, to read alone.</summary>
        public const string ClientCredentials = "ClientCredentials";

        /// <summary>Cloud: the worldwide one (the default).</summary>
        public const string Global = "Global";

        /// <summary>Cloud: Microsoft 365 US Government L4.</summary>
        public const string UsGov = "UsGov";

        /// <summary>Cloud: Microsoft 365 US Government L5.</summary>
        public const string UsGovDoD = "UsGovDoD";

        /// <summary>Cloud: the one operated by 21Vianet, which the engine binds and refuses.</summary>
        public const string China = "China";

        /// <summary>Grant: reading a channel's messages.</summary>
        public const string ChannelMessages = "ChannelMessages";

        /// <summary>Grant: editing and deleting in a channel.</summary>
        public const string ChannelEdit = "ChannelEdit";

        /// <summary>Grant: reading meeting transcripts.</summary>
        public const string MeetingsGrant = "Meetings";

        /// <summary>Grant: listing the members of a channel.</summary>
        public const string ChannelMembers = "ChannelMembers";
    }

    /// <summary>
    /// The names of the keys <c>Orkeon:Tools:Chat</c> binds, one constant each. Core does not
    /// reference the engine, so a test holds this list to the options classes the binder fills.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1034",
        Justification = "The key names read as the path they spell (ChatSection.Keys.Webhook); a top-level " +
                        "class would detach them from the section they belong to.")]
    public static class Keys
    {
        /// <summary>Section: the account a call uses when it names none.</summary>
        public const string DefaultAccount = "DefaultAccount";

        /// <summary>Section: the physical directory of the OAuth tokens.</summary>
        public const string CredentialsDirectory = "CredentialsDirectory";

        /// <summary>Section: the accounts dictionary.</summary>
        public const string Accounts = "Accounts";

        /// <summary>Section: how the content an agent reads is screened.</summary>
        public const string Screening = "Screening";

        /// <summary>Screening: whether a rejected message has its text withheld.</summary>
        public const string WithholdRejected = "WithholdRejected";

        /// <summary>Account: the service it opens.</summary>
        public const string Provider = "Provider";

        /// <summary>Account: what an agent may do.</summary>
        public const string Rights = "Rights";

        /// <summary>Account: how it authenticates.</summary>
        public const string Auth = "Auth";

        /// <summary>Account: the webhook of a <see cref="Values.TeamsWebhook"/> account.</summary>
        public const string Webhook = "Webhook";

        /// <summary>Account: the Microsoft cloud it lives in.</summary>
        public const string Cloud = "Cloud";

        /// <summary>Account: what an agent may read.</summary>
        public const string Read = "Read";

        /// <summary>Account: the guard rails on posting.</summary>
        public const string Send = "Send";

        /// <summary>Auth: who signs in, a user or an application.</summary>
        public const string Method = "Method";

        /// <summary>Auth: the id of the Microsoft Entra application.</summary>
        public const string ClientId = "ClientId";

        /// <summary>Auth: the Microsoft tenant.</summary>
        public const string Tenant = "Tenant";

        /// <summary>Auth: what an administrator of the tenant consented to.</summary>
        public const string AdminGranted = "AdminGranted";

        /// <summary>Auth: the name of the variable that holds the path of the certificate.</summary>
        public const string CertificatePathEnvVar = "CertificatePathEnvVar";

        /// <summary>Auth: the name of the variable that holds the path of the federated token file.</summary>
        public const string FederatedTokenFileEnvVar = "FederatedTokenFileEnvVar";

        /// <summary>Auth: the name of the variable that holds the client secret.</summary>
        public const string ClientSecretEnvVar = "ClientSecretEnvVar";

        /// <summary>Webhook: the name of the variable that holds its address.</summary>
        public const string UrlEnvVar = "UrlEnvVar";

        /// <summary>Read and Send: the places an agent may read, or post in.</summary>
        public const string AllowedPlaces = "AllowedPlaces";

        /// <summary>Send: the most writes in an hour.</summary>
        public const string MaxPerHour = "MaxPerHour";
    }
}
