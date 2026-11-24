using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orkeon.Constants.Configuration;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Tools.Chat.Configuration;
using EngineChatRights = Orkeon.Tools.Chat.Configuration.ChatRights;
using StudioChatRights = Orkeon.Studio.Core.Configuration.ChatRights;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// Studio Core re-spells the keys and the enumeration values of <c>Orkeon:Tools:Chat</c> instead
/// of referencing <c>Orkeon.Tools.Chat</c>, so the copies are held to the engine here, as
/// <see cref="EmailSectionDriftTests"/> holds the e-mail ones. The engine types are <c>internal</c>;
/// the engine opens them to this project only.
/// </summary>
public sealed class ChatSectionDriftTests
{
    private const StudioChatRights AllRights =
        StudioChatRights.Read | StudioChatRights.Send | StudioChatRights.Edit | StudioChatRights.Delete | StudioChatRights.React | StudioChatRights.Meetings;

    /// <summary>The options classes the binder fills, with the keys of <see cref="ChatSection.Keys"/> each one carries.</summary>
    private static readonly (Type Options, string[] Keys)[] Groups =
    [
        (typeof(ChatToolsOptions), [ChatSection.Keys.DefaultAccount, ChatSection.Keys.CredentialsDirectory, ChatSection.Keys.Accounts, ChatSection.Keys.Screening]),
        (typeof(ChatScreeningOptions), [ChatSection.Keys.WithholdRejected]),
        (typeof(ChatAccountOptions),
        [
            ChatSection.Keys.Provider, ChatSection.Keys.Rights, ChatSection.Keys.Auth, ChatSection.Keys.Webhook,
            ChatSection.Keys.Cloud, ChatSection.Keys.Read, ChatSection.Keys.Send,
        ]),
        (typeof(ChatAuthOptions),
        [
            ChatSection.Keys.Method, ChatSection.Keys.ClientId, ChatSection.Keys.Tenant, ChatSection.Keys.AdminGranted,
            ChatSection.Keys.CertificatePathEnvVar, ChatSection.Keys.FederatedTokenFileEnvVar, ChatSection.Keys.ClientSecretEnvVar,
        ]),
        (typeof(ChatWebhookOptions), [ChatSection.Keys.UrlEnvVar]),
        (typeof(ChatReadOptions), [ChatSection.Keys.AllowedPlaces]),
        (typeof(ChatSendOptions), [ChatSection.Keys.AllowedPlaces, ChatSection.Keys.MaxPerHour]),
    ];

    [Fact]
    public void The_section_path_is_the_one_the_runner_binds()
    {
        Assert.Equal(ConfigurationKeys.ToolsChat, ChatSection.SectionPath);
        Assert.Equal(ConfigurationKeys.ToolsChat + ":" + ChatSection.Keys.Accounts, ChatSection.AccountsPath);
    }

    [Fact]
    public void Every_public_property_of_an_options_class_has_its_key_and_every_key_is_a_property()
    {
        foreach (var (options, keys) in Groups)
        {
            var properties = options
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal);

            Assert.True(
                properties.SequenceEqual(keys.Order(StringComparer.Ordinal), StringComparer.Ordinal),
                $"{options.Name} binds [{string.Join(", ", properties)}] but ChatSection.Keys models [{string.Join(", ", keys.Order(StringComparer.Ordinal))}] for it.");
        }
    }

    [Fact]
    public void Every_key_constant_belongs_to_an_options_class()
    {
        var constants = typeof(ChatSection.Keys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

        Assert.Equal(
            Groups.SelectMany(group => group.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            constants.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_provider_studio_offers_is_a_member_of_the_engine_enumeration_and_the_reverse()
    {
        Assert.Equal(Enum.GetNames<ChatProvider>().Order(StringComparer.Ordinal), ChatSection.Providers.Order(StringComparer.Ordinal));
        foreach (var provider in ChatSection.Providers)
            Assert.Equal(provider, BindOne(new ChatAccountDefinition { Name = "a", Provider = provider }).Provider.ToString());
    }

    [Fact]
    public void The_methods_the_clouds_and_the_grants_are_the_engines()
    {
        Assert.Equal(Enum.GetNames<ChatAuthMethod>(), ChatSection.AuthMethods);
        Assert.Equal(Enum.GetNames<ChatCloud>(), ChatSection.Clouds);
        Assert.Equal(Enum.GetNames<ChatAdminGrants>().Where(name => name != nameof(ChatAdminGrants.None)), ChatSection.Grants);
    }

    [Fact]
    public void The_engine_lists_its_rights_in_the_order_studio_writes_them_and_with_the_same_values()
    {
        Assert.Equal(
            Enum.GetValues<EngineChatRights>().Where(right => right != EngineChatRights.None).Select(right => right.ToString()),
            ChatSection.RightNames);

        foreach (var right in Enum.GetValues<EngineChatRights>())
            Assert.Equal((int)right, (int)Enum.Parse<StudioChatRights>(right.ToString()));
        Assert.Equal(Enum.GetNames<EngineChatRights>().Order(StringComparer.Ordinal), Enum.GetNames<StudioChatRights>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Each_right_studio_writes_alone_is_bound_by_the_engine_as_that_right()
    {
        foreach (var right in Enum.GetValues<StudioChatRights>().Where(right => right != StudioChatRights.None))
            Assert.Equal(right.ToString(), BindOne(new ChatAccountDefinition { Name = "a", Rights = right }).Rights.ToString());
    }

    [Fact]
    public void An_account_filled_field_by_field_is_bound_by_the_engine_with_no_problem_and_the_values_written()
    {
        var document = AppSettingsDocument.CreateEmpty();
        document.Chat.DefaultAccount = "work";
        document.Chat.WithholdRejected = true;
        document.Chat.CredentialsDirectory = "/srv/orkeon/credentials";
        document.Chat.SetAccount(new ChatAccountDefinition
        {
            Name = "work",
            Provider = "Teams",
            Rights = AllRights,
            AuthMethod = "ClientCredentials",
            ClientId = "client-id",
            Tenant = "contoso.com",
            AdminGranted = ["ChannelMessages"],
            CertificatePathEnvVar = "CERTIFICATE",
            FederatedTokenFileEnvVar = "TOKEN_FILE",
            ClientSecretEnvVar = "CLIENT_SECRET",
            WebhookEnvVar = "WEBHOOK",
            Cloud = "UsGovDoD",
            ReadPlaces = ["*"],
            SendPlaces = ["team:Support", "user:you@example.com"],
            MaxPerHour = 30,
        });

        var options = Bind(document);

        Assert.Empty(options.SectionProblems);
        Assert.Empty(options.AccountProblems);
        Assert.Equal("work", options.DefaultAccount);
        Assert.True(options.Screening.WithholdRejected);
        Assert.Equal("/srv/orkeon/credentials", options.CredentialsDirectory);

        var account = Assert.Single(options.Accounts).Value;
        Assert.Equal(ChatProvider.Teams, account.Provider);
        Assert.Equal((int)AllRights, (int)account.Rights);
        Assert.Equal(ChatAuthMethod.ClientCredentials, account.Auth.Method);
        Assert.Equal("client-id", account.Auth.ClientId);
        Assert.Equal("contoso.com", account.Auth.Tenant);
        Assert.Equal(["ChannelMessages"], account.Auth.AdminGranted);
        Assert.Equal("CERTIFICATE", account.Auth.CertificatePathEnvVar);
        Assert.Equal("TOKEN_FILE", account.Auth.FederatedTokenFileEnvVar);
        Assert.Equal("CLIENT_SECRET", account.Auth.ClientSecretEnvVar);
        Assert.Equal("WEBHOOK", account.Webhook.UrlEnvVar);
        Assert.Equal(ChatCloud.UsGovDoD, account.Cloud);
        Assert.Equal(["*"], account.Read.AllowedPlaces);
        Assert.Equal(["team:Support", "user:you@example.com"], account.Send.AllowedPlaces);
        Assert.Equal(30, account.Send.MaxPerHour);
    }

    private static ChatAccountOptions BindOne(ChatAccountDefinition account)
    {
        var document = AppSettingsDocument.CreateEmpty();
        document.Chat.SetAccount(account);

        var options = Bind(document);

        Assert.Empty(options.AccountProblems);
        return options.Accounts[account.Name];
    }

    private static ChatToolsOptions Bind(AppSettingsDocument document)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJson()));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = new ChatToolsOptions();

        ChatOptionsBinder.Bind(configuration.GetSection(ConfigurationKeys.ToolsChat), options);
        return options;
    }
}
