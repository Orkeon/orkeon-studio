using System.Text.Json.Nodes;
using Orkeon.Studio.Core.Configuration;
using StudioChatRights = Orkeon.Studio.Core.Configuration.ChatRights;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// <see cref="ChatSection"/> edits <c>Orkeon:Tools:Chat</c> in place, on the model of
/// <see cref="EmailSectionTests"/>: an account reads back as written, a key Studio does not model
/// survives an edit, nothing is written as an empty string, object or list, keys are found
/// whatever their case and keep the spelling of the file, and the rest of the file is never touched.
/// </summary>
public sealed class ChatSectionTests
{
    private static readonly ChatAccountDefinition Full = new()
    {
        Name = "work",
        Provider = "Teams",
        Rights = StudioChatRights.Read | StudioChatRights.Send | StudioChatRights.React,
        AuthMethod = "OAuth2",
        ClientId = "client-id",
        Tenant = "contoso.com",
        AdminGranted = ["ChannelMessages", "Meetings"],
        CertificatePathEnvVar = "CHAT_CERTIFICATE",
        FederatedTokenFileEnvVar = "CHAT_TOKEN_FILE",
        ClientSecretEnvVar = "CHAT_WORK_CLIENT_SECRET",
        WebhookEnvVar = "CHAT_WORK_WEBHOOK_URL",
        Cloud = "UsGov",
        ReadPlaces = ["*"],
        SendPlaces = ["team:Support/channel:General", "user:me@example.com"],
        MaxPerHour = 20,
    };

    [Fact]
    public void An_account_reads_back_as_it_was_written_with_the_keys_the_engine_binds()
    {
        var document = AppSettingsDocument.CreateEmpty();

        document.Chat.SetAccount(Full);

        Assert.Equal(Full, document.Chat.GetAccount("work"), DefinitionComparer.Instance);
        var account = Assert.IsType<JsonObject>(document.GetNode("Orkeon:Tools:Chat:Accounts:work"));
        Assert.Equal("Teams", account["Provider"]!.GetValue<string>());
        Assert.Equal("Read, Send, React", account["Rights"]!.GetValue<string>());
        Assert.Equal("CHAT_WORK_CLIENT_SECRET", account["Auth"]!["ClientSecretEnvVar"]!.GetValue<string>());
        Assert.Equal(2, account["Auth"]!["AdminGranted"]!.AsArray().Count);
        Assert.Equal("CHAT_WORK_WEBHOOK_URL", account["Webhook"]!["UrlEnvVar"]!.GetValue<string>());
        Assert.Single(account["Read"]!["AllowedPlaces"]!.AsArray());
        Assert.Equal(2, account["Send"]!["AllowedPlaces"]!.AsArray().Count);
        Assert.Equal(20, account["Send"]!["MaxPerHour"]!.GetValue<int>());
    }

    [Fact]
    public void Clearing_every_field_leaves_the_account_object_and_no_empty_value()
    {
        var document = AppSettingsDocument.CreateEmpty();
        document.Chat.SetAccount(Full);

        document.Chat.SetAccount(new ChatAccountDefinition { Name = "work", Tenant = "  ", ClientId = "" });

        var account = Assert.IsType<JsonObject>(document.GetNode("Orkeon:Tools:Chat:Accounts:work"));
        Assert.Empty(account);
        Assert.Equal(["work"], document.Chat.AccountNames);
    }

    [Fact]
    public void A_key_studio_does_not_model_and_the_rest_of_the_file_survive_an_edit()
    {
        var document = AppSettingsDocument.Parse("""
            {
              "Llm": { "Provider": "Ollama" },
              "Orkeon": { "Tools": { "Chat": {
                "Accounts": { "work": { "Provider": "OneChat", "Rights": "Read", "Note": "mine",
                  "Auth": { "ClientId": "c", "Extra": 1 }, "Send": { "Later": true } } },
                "Future": { "Setting": 1 } } } }
            }
            """);

        var account = document.Chat.GetAccount("work")!;
        document.Chat.SetAccount(account with { Tenant = "contoso.com", Rights = StudioChatRights.Read | StudioChatRights.Edit });

        Assert.Equal("Ollama", document.GetString("Llm:Provider"));
        Assert.Equal("mine", document.GetString("Orkeon:Tools:Chat:Accounts:work:Note"));
        Assert.Equal(1, document.GetInt32("Orkeon:Tools:Chat:Accounts:work:Auth:Extra"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Chat:Accounts:work:Send:Later"));
        Assert.Equal(1, document.GetInt32("Orkeon:Tools:Chat:Future:Setting"));
        Assert.Equal("contoso.com", document.GetString("Orkeon:Tools:Chat:Accounts:work:Auth:Tenant"));
        Assert.Equal("Read, Edit", document.GetString("Orkeon:Tools:Chat:Accounts:work:Rights"));
    }

    [Fact]
    public void Saving_an_account_the_user_did_not_change_changes_nothing_whatever_the_spelling()
    {
        const string Json = """
            { "Orkeon": { "Tools": { "Chat": { "accounts": { "work": {
              "provider": "teams", "rights": "read,SEND", "cloud": "usgov",
              "auth": { "clientid": "c", "admingranted": ["meetings", 42] }, "read": { "allowedplaces": ["*", 42] },
              "send": { "maxperhour": "0x1E" } } } } } } }
            """;
        var document = AppSettingsDocument.Parse(Json);
        var before = document.ToJson();

        document.Chat.SetAccount(document.Chat.GetAccount("WORK")!);

        Assert.Equal(before, document.ToJson());
    }

    [Fact]
    public void Rights_the_engine_cannot_read_are_written_back_as_they_were_until_a_right_is_chosen()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Chat": { "Accounts": { "work": { "Rights": "Read, Rite" } } } } } }""");
        var account = document.Chat.GetAccount("work")!;

        Assert.Equal(StudioChatRights.None, account.Rights);
        Assert.Equal("Read, Rite", account.RightsRaw);

        document.Chat.SetAccount(account);
        Assert.Equal("Read, Rite", document.GetString("Orkeon:Tools:Chat:Accounts:work:Rights"));

        document.Chat.SetAccount(account with { Rights = StudioChatRights.Read, RightsRaw = null });
        Assert.Equal("Read", document.GetString("Orkeon:Tools:Chat:Accounts:work:Rights"));
    }

    [Fact]
    public void A_name_that_differs_only_by_case_is_the_same_account_and_cannot_be_set_as_another()
    {
        var document = AppSettingsDocument.CreateEmpty();
        document.Chat.SetAccount(Full);

        Assert.Throws<ArgumentException>(() => document.Chat.SetAccount(Full with { Name = "WORK" }));
        Assert.Equal("work", document.Chat.GetAccount("WORK")!.Name);
    }

    [Fact]
    public void Renaming_keeps_the_place_and_every_key_and_the_default_follows()
    {
        var document = AppSettingsDocument.CreateEmpty();
        document.Chat.SetAccount(Full with { Name = "a" });
        document.Chat.SetAccount(Full);
        document.Chat.SetAccount(Full with { Name = "z" });
        document.Chat.DefaultAccount = "WORK";

        document.Chat.RenameAccount("work", "office");

        Assert.Equal(["a", "office", "z"], document.Chat.AccountNames);
        Assert.Equal(Full with { Name = "office" }, document.Chat.GetAccount("office"), DefinitionComparer.Instance);
        Assert.Equal("office", document.Chat.DefaultAccount);
        Assert.Throws<ArgumentException>(() => document.Chat.RenameAccount("office", "A"));
    }

    [Fact]
    public void Removing_the_last_account_removes_the_dictionary_and_the_default_that_named_it()
    {
        var document = AppSettingsDocument.CreateEmpty();
        document.Chat.SetAccount(Full);
        document.Chat.DefaultAccount = "work";
        document.Chat.CredentialsDirectory = "/srv/tokens";

        document.Chat.RemoveAccount("WORK");

        Assert.Empty(document.Chat.AccountNames);
        Assert.Null(document.GetNode("Orkeon:Tools:Chat:Accounts"));
        Assert.Null(document.Chat.DefaultAccount);
        Assert.Equal("/srv/tokens", document.Chat.CredentialsDirectory);
    }

    [Fact]
    public void The_screening_switch_is_written_under_its_object_and_clearing_it_removes_the_object()
    {
        var document = AppSettingsDocument.CreateEmpty();

        document.Chat.WithholdRejected = true;
        Assert.True(document.GetBoolean("Orkeon:Tools:Chat:Screening:WithholdRejected"));

        document.Chat.WithholdRejected = null;
        Assert.Null(document.GetNode("Orkeon:Tools:Chat:Screening"));

        // Clearing what was never there creates nothing.
        var empty = AppSettingsDocument.CreateEmpty();
        empty.Chat.WithholdRejected = null;
        empty.Chat.DefaultAccount = null;
        Assert.Null(empty.GetNode("Orkeon:Tools:Chat"));
    }

    [Fact]
    public void A_value_that_is_no_object_is_an_account_with_no_field_until_one_is_set()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Chat": { "Accounts": { "work": null } } } } }""");

        var account = document.Chat.GetAccount("work")!;
        document.Chat.SetAccount(account);
        Assert.Null(document.GetNode("Orkeon:Tools:Chat:Accounts:work"));
        Assert.Equal(["work"], document.Chat.AccountNames);

        document.Chat.SetAccount(account with { Provider = "OneChat" });
        Assert.Equal("OneChat", document.GetString("Orkeon:Tools:Chat:Accounts:work:Provider"));
    }

    [Fact]
    public void The_email_section_is_left_alone_by_every_chat_edit()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Email": { "Accounts": { "work": { "Provider": "Gmail" } } } } } }""");
        var before = document.GetNode("Orkeon:Tools:Email")!.ToJsonString();

        document.Chat.SetAccount(Full);
        document.Chat.RenameAccount("work", "office");
        document.Chat.RemoveAccount("office");

        Assert.Equal(before, document.GetNode("Orkeon:Tools:Email")!.ToJsonString());
        Assert.Equal(["work"], document.Email.AccountNames);
    }

    /// <summary>Two definitions are equal when every field is, the lists compared item by item.</summary>
    private sealed class DefinitionComparer : IEqualityComparer<ChatAccountDefinition?>
    {
        public static readonly DefinitionComparer Instance = new();

        public bool Equals(ChatAccountDefinition? x, ChatAccountDefinition? y) =>
            x is not null && y is not null
            && x with { AdminGranted = [], ReadPlaces = [], SendPlaces = [] } == y with { AdminGranted = [], ReadPlaces = [], SendPlaces = [] }
            && x.AdminGranted.SequenceEqual(y.AdminGranted, StringComparer.Ordinal)
            && x.ReadPlaces.SequenceEqual(y.ReadPlaces, StringComparer.Ordinal)
            && x.SendPlaces.SequenceEqual(y.SendPlaces, StringComparer.Ordinal);

        public int GetHashCode(ChatAccountDefinition? obj) => obj?.Name.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
