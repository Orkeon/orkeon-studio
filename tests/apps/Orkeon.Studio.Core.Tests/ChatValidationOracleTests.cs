using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orkeon.Constants.Configuration;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Validation;
using Orkeon.Tools.Chat.Accounts;
using Orkeon.Tools.Chat.Configuration;
using Orkeon.Tools.Chat.Places;
using Rights = Orkeon.Studio.Core.Configuration.ChatRights;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// What Studio says of a chat account is what the run will say, on the model of
/// <see cref="DriveValidationOracleTests"/>: every row is handed to the engine's own binder, registry
/// and resolver, which must say the same sentences in the same order. A rule the engine gains turns
/// its row red, and the rule is then added to <see cref="ChatAccountRules"/>.
/// </summary>
public sealed class ChatValidationOracleTests
{
    private const string Name = "work";
    private const string TeamId = "00000000-0000-0000-0000-000000000001";

    /// <summary>An account a person signs in, which the engine resolves.</summary>
    private static readonly ChatAccountDefinition User = new()
    {
        Name = Name,
        Provider = "Teams",
        Rights = Rights.Read | Rights.Send,
        ClientId = "client-id",
        ReadPlaces = ["*"],
        SendPlaces = ["team:Support/channel:General"],
    };

    /// <summary>A webhook, which the engine resolves.</summary>
    private static readonly ChatAccountDefinition Webhook = new()
    {
        Name = Name,
        Provider = "TeamsWebhook",
        Rights = Rights.Send,
        WebhookEnvVar = "CHAT_WORK_WEBHOOK_URL",
    };

    /// <summary>An application, which the engine resolves.</summary>
    private static readonly ChatAccountDefinition App = new()
    {
        Name = Name,
        Provider = "Teams",
        Rights = Rights.Read | Rights.Meetings,
        AuthMethod = "ClientCredentials",
        ClientId = "client-id",
        Tenant = "contoso.com",
        ClientSecretEnvVar = "CHAT_WORK_CLIENT_SECRET",
        ReadPlaces = ["team:" + TeamId],
    };

    /// <summary>The accounts Studio writes, and whether the engine uses each of them.</summary>
    private static readonly Dictionary<string, (ChatAccountDefinition Account, bool Usable)> Definitions = new(StringComparer.Ordinal)
    {
        ["user"] = (User, true),
        ["webhook"] = (Webhook, true),
        ["webhook with a cap"] = (Webhook with { MaxPerHour = 5 }, true),
        ["application"] = (App, true),
        ["user with everything"] = (User with
        {
            Rights = Rights.Read | Rights.Send | Rights.Edit | Rights.Delete | Rights.React | Rights.Meetings,
            AuthMethod = "oauth2",
            Tenant = "organizations",
            AdminGranted = ["channelmessages", " Meetings ", "ChannelEdit", "ChannelMembers", ""],
            Cloud = "UsGov",
            MaxPerHour = 1,
        }, true),
        ["no places at all"] = (User with { ReadPlaces = [], SendPlaces = [] }, true),
        ["a name the engine refuses"] = (User with { Name = "-work" }, false),
        ["no provider"] = (User with { Provider = null }, false),
        ["a provider it does not know"] = (User with { Provider = "Slack" }, false),
        ["no rights"] = (User with { Rights = Rights.None }, false),
        ["unreadable rights"] = (User with { Rights = Rights.None, RightsRaw = "Read, Rite" }, false),
        ["an unknown method"] = (User with { AuthMethod = "Password" }, false),
        ["an unknown cloud"] = (User with { Cloud = "Mars" }, false),
        ["china"] = (User with { Cloud = "China" }, false),
        ["no client id"] = (User with { ClientId = null }, false),
        ["a client id as a reference"] = (User with { ClientId = "${CLIENT_ID}" }, false),
        ["a tenant as a reference"] = (User with { Tenant = "%TENANT%" }, false),
        ["a tenant that is none"] = (User with { Tenant = "my tenant" }, false),
        ["consumers"] = (User with { Tenant = "consumers" }, false),
        ["an unknown grant, twice"] = (User with { AdminGranted = ["Everything", "Meetings", "More"] }, false),
        ["a user with a certificate"] = (User with { CertificatePathEnvVar = "C", FederatedTokenFileEnvVar = "F", ClientSecretEnvVar = "S" }, false),
        ["a user with a webhook"] = (User with { WebhookEnvVar = "W" }, false),
        ["places that are none"] = (User with { ReadPlaces = ["general", "team:*"], SendPlaces = ["user:me", "chat:a b"] }, false),
        ["a cap of nothing"] = (User with { MaxPerHour = 0 }, false),
        ["webhook that reads"] = (Webhook with { Rights = Rights.Read | Rights.Send }, false),
        ["webhook without a variable"] = (Webhook with { WebhookEnvVar = null }, false),
        ["webhook with its address"] = (Webhook with { WebhookEnvVar = "https://example.com/hook?sig=secret" }, false),
        ["webhook with places"] = (Webhook with { ReadPlaces = ["*"], SendPlaces = ["*"], MaxPerHour = -1 }, false),
        ["application that posts"] = (App with { Rights = Rights.Read | Rights.Send, SendPlaces = ["*"], MaxPerHour = 3, AdminGranted = ["Meetings"] }, false),
        ["application with meetings alone"] = (App with { Rights = Rights.Meetings }, false),
        ["application without a credential"] = (App with { ClientSecretEnvVar = null }, false),
        ["application with two credentials"] = (App with { CertificatePathEnvVar = "C" }, false),
        ["application by certificate"] = (App with { ClientSecretEnvVar = null, CertificatePathEnvVar = "C" }, true),
        ["application by federated token"] = (App with { ClientSecretEnvVar = null, FederatedTokenFileEnvVar = "F" }, true),
        ["application with a secret as a reference"] = (App with { ClientSecretEnvVar = "${SECRET}" }, false),
        ["application without a tenant"] = (App with { Tenant = null }, false),
        ["application on an alias"] = (App with { Tenant = "common" }, false),
        ["application with open places"] = (App with { ReadPlaces = ["*", "user:me@example.com", "team:Support", "team:Support/channel:General", "chat:19:abc@thread.v2"] }, false),
        ["everything wrong at once"] = (new ChatAccountDefinition
        {
            Name = "_bad",
            Provider = "Teams",
            AuthMethod = "ClientCredentials",
            Cloud = "China",
            AdminGranted = ["x"],
            ReadPlaces = ["x", "*"],
            SendPlaces = ["y"],
            MaxPerHour = -1,
            WebhookEnvVar = "W",
        }, false),
    };

    /// <summary>The sections written by hand: what a file may carry that <see cref="ChatSection"/> never writes.</summary>
    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["an account that is no object"] = """{ "Accounts": { "work": null } }""",
        ["an account that is a text"] = """{ "Accounts": { "work": "Teams" } }""",
        ["values the binder cannot convert"] = """
            { "Accounts": { "work": { "Provider": "Slack", "Rights": "Read, Rite", "Cloud": "Mars",
              "Auth": { "Method": "x" }, "Send": { "MaxPerHour": "lots" } } } }
            """,
        ["unknown keys at every depth"] = """
            { "Accounts": { "work": { "Provider": "Teams", "Note": "n", "Auth": { "Secret": "s" }, "Read": { "Places": [] }, "Webhook": { "Url": "u" } } } }
            """,
        ["an unknown key of the section"] = """
            { "Teams": { "Tenant": "t" }, "Accounts": { "work": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" } } } }
            """,
        ["values where sections and lists are expected"] = """
            { "Screening": "yes", "Accounts": { "work": { "Auth": "c", "Read": { "AllowedPlaces": "*" }, "Send": [ "x" ] } } }
            """,
        ["every key in another case"] = """
            { "accounts": { "work": { "provider": "teams", "rights": "read,SEND", "auth": { "clientid": "c", "admingranted": [ "meetings" ] },
              "read": { "allowedplaces": [ "TEAM:Support" ] }, "send": { "maxperhour": "0x1E" } } } }
            """,
        ["numbers for names"] = """{ "Accounts": { "work": { "Provider": 0, "Rights": "3", "Cloud": 1, "Auth": { "Method": 0 } } } }""",
        ["empty values"] = """{ "Accounts": { "work": { "Provider": "", "Rights": "", "Cloud": "", "Auth": { "Method": "" }, "Send": { "MaxPerHour": "" } } } }""",
        ["an empty cap alone"] = """{ "Accounts": { "work": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" }, "Send": { "MaxPerHour": "" } } } }""",
        ["a screening switch that is neither"] = """
            { "Screening": { "WithholdRejected": "maybe" }, "Accounts": { "work": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" } } } }
            """,
        ["a screening switch and a key at once"] = """
            { "Screening": { "WithholdRejected": 1, "Other": 2 }, "Accounts": { "work": { "Provider": "Teams", "Extra": "x" } } }
            """,
    };

    public static TheoryData<string> DefinitionRows() => new(Definitions.Keys);

    public static TheoryData<string> FileRows() => new(Files.Keys);

    [Theory]
    [MemberData(nameof(DefinitionRows))]
    public void Studio_and_the_engine_say_the_same_of_an_account_studio_writes(string row)
    {
        var (definition, usable) = Definitions[row];
        var document = AppSettingsDocument.CreateEmpty();
        document.Chat.SetAccount(definition);

        var findings = ChatAccountRules.Check(definition.Name, definition);

        Assert.Equal(usable, findings.Count == 0);
        AssertSameAsTheEngine(document, definition.Name, findings);

        // The file Studio wrote reads back as the account it was given.
        Assert.Equal(findings, ChatAccountRules.Check(document, definition.Name));
    }

    [Theory]
    [MemberData(nameof(FileRows))]
    public void Studio_and_the_engine_say_the_same_of_an_account_written_by_hand(string row)
    {
        var document = AppSettingsDocument.Parse($$"""{ "Orkeon": { "Tools": { "Chat": {{Files[row]}} } } }""");

        AssertSameAsTheEngine(document, Name, ChatAccountRules.Check(document, Name));
    }

    /// <summary>A secret written where a variable is named, or in a typed key, is never said back (Orkeon #465).</summary>
    [Fact]
    public void A_secret_in_the_wrong_key_is_never_said_back()
    {
        const string Secret = "sk-ant-studio-oracle' at 'secret";
        var definitions = new[] { App with { ClientSecretEnvVar = Secret }, Webhook with { WebhookEnvVar = Secret }, User with { Provider = Secret } };

        foreach (var definition in definitions)
        {
            var findings = ChatAccountRules.Check(Name, definition);
            Assert.NotEmpty(findings);
            Assert.All(findings, finding => Assert.DoesNotContain("sk-ant", finding.Text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_finding_on_an_account_is_a_warning_at_a_path_under_the_account()
    {
        foreach (var (definition, _) in Definitions.Values)
        {
            foreach (var finding in ChatAccountRules.Check(definition.Name, definition))
            {
                Assert.Equal(ValidationSeverity.Warning, finding.Severity);
                Assert.StartsWith($"{ChatSection.AccountsPath}:{definition.Name}", finding.Path, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void An_account_the_file_does_not_hold_has_no_finding()
    {
        Assert.Empty(ChatAccountRules.Check(AppSettingsDocument.CreateEmpty(), Name));
    }

    [Fact]
    public void A_place_is_judged_as_the_engine_judges_it()
    {
        string[] places =
        [
            "*", " * ", "", "team:Support", "TEAM:Support", "team:", "team:*", "team:Support/channel:General", "team:Support/channel:",
            "team:Support/channel:*", "team:a/b/channel:c", "team:" + TeamId, "chat:19:abc@thread.v2", "chat:19:abc@thread.tacv2", "chat:", "chat:a b",
            "chat:" + new string('a', 400), "chat:" + new string('a', 401), "user:me@example.com", "user:me", "user:*@example.com", "user:*@com",
            "user:*@-a.com", "user:" + TeamId, "user:a@b@c", "user:m e@example.com", "channel:x", "general", "team:a\u0007b",
            "team:" + new string('a', 995), "team:" + new string('a', 996),
        ];
        foreach (var place in places)
            Assert.True(PlaceReference.TryParse(place, out _) == ChatAccountRules.IsPlace(place), place);
    }

    [Fact]
    public void An_account_name_is_judged_as_the_engine_judges_it()
    {
        foreach (var name in new[] { "work", "Work.2", "a_b-c", "-work", "_work", "", "w o", new string('a', 64), new string('a', 65), "\u00e9" })
            Assert.Equal(ChatAccountResolver.IsValidName(name), ChatAccountRules.IsValidName(name));
    }

    /// <summary>The same verdict, and the same sentences in the same order, as the engine's registry once its binder has read <paramref name="document"/>.</summary>
    private static void AssertSameAsTheEngine(AppSettingsDocument document, string name, IReadOnlyList<ValidationMessage> findings)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJson()));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = new ChatToolsOptions();
        ChatOptionsBinder.Bind(configuration.GetSection(ConfigurationKeys.ToolsChat), options);

        var resolution = new ChatAccountRegistry(Options.Create(options)).Inspect(name);

        Assert.Equal(resolution.Problems, findings.Select(finding => finding.Text));
        Assert.Equal(resolution.Account is null, findings.Count > 0);
        Assert.All(findings, finding => Assert.Equal(FamilyOf(finding.Text), finding.Code));
    }

    /// <summary>The code Studio files an engine sentence under: the chat families of <see cref="ValidationCodes"/>.</summary>
    private static string FamilyOf(string problem)
    {
        if (problem.Contains(" is not an account setting", StringComparison.Ordinal) || problem.Contains(" is not a chat setting", StringComparison.Ordinal))
            return ValidationCodes.ChatKey;
        if (problem.StartsWith(ConfigurationKeys.ToolsChat + ":Screening:", StringComparison.Ordinal))
            return ValidationCodes.ChatScreening;
        if (problem.Contains(" single value where ", StringComparison.Ordinal))
            return ValidationCodes.ChatKey;
        if (problem.StartsWith("Auth:AdminGranted", StringComparison.Ordinal))
            return ValidationCodes.ChatAuth;

        string[] values = [" is not one of ", " is not a list of ", " is a number where ", " is a list where ", " takes names: ", " is not a whole number"];
        if (values.Any(value => problem.Contains(value, StringComparison.Ordinal)))
            return ValidationCodes.ChatValue;

        (string Start, string Code)[] families =
        [
            ("the account name ", ValidationCodes.ChatName),
            ("Provider ", ValidationCodes.ChatProvider),
            ("Rights ", ValidationCodes.ChatRights),
            ("Webhook:", ValidationCodes.ChatWebhook),
            ("Cloud ", ValidationCodes.ChatCloud),
            ("Read:AllowedPlaces", ValidationCodes.ChatPlaces),
            ("Send:AllowedPlaces", ValidationCodes.ChatPlaces),
            ("AllowedPlaces ", ValidationCodes.ChatPlaces),
            ("Send:MaxPerHour", ValidationCodes.ChatLimits),
            ("Auth:", ValidationCodes.ChatAuth),
        ];
        return families.FirstOrDefault(family => problem.StartsWith(family.Start, StringComparison.Ordinal)).Code ?? "unclassified: " + problem;
    }
}
