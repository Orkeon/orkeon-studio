using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// How the chat section plugs into <see cref="AppSettingsValidator"/>, on the model of
/// <see cref="EmailValidationTests"/>: every finding of an account is a warning — the run sets it
/// aside, the file saves —, the default account must name a declared one, and the one error is a
/// pair of names or keys equal but for the case that set one same key. <see cref="ChatSecretNames"/>
/// follows the e-mail convention for the variable of a secret.
/// </summary>
public sealed class ChatValidationTests
{
    private static IReadOnlyList<ValidationMessage> Validate(string chatSection) =>
        new AppSettingsValidator().ValidateJson($$"""{ "Llm": { "Provider": "Ollama" }, "Orkeon": { "Tools": { "Chat": {{chatSection}} } } }""")
            .Where(message => message.Code.StartsWith("STUDIO-CHAT-", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// The configuration binds the section whatever the case of its keys: one written in lowercase is
    /// judged all the same, and a value set through Studio goes into it, not into a twin beside it.
    /// </summary>
    [Fact]
    public void A_section_in_another_letter_case_is_judged_and_written_where_it_stands()
    {
        var json = """{ "orkeon": { "tools": { "chat": { "Accounts": { "work": { "Provider": "Teams", "Rights": "Read" } } } } } }""";

        var finding = Assert.Single(new AppSettingsValidator().ValidateJson(json), message => message.Code.StartsWith("STUDIO-CHAT-", StringComparison.Ordinal));
        Assert.Equal(ValidationCodes.ChatAuth, finding.Code);

        var document = AppSettingsDocument.Parse(json);
        Assert.Equal(["work"], document.Chat.AccountNames);
        document.Chat.DefaultAccount = "work";
        Assert.Equal("work", document.GetString("orkeon:tools:chat:DefaultAccount"));
        Assert.False(document.ContainsPath("Orkeon"));
    }

    [Fact]
    public void A_usable_account_raises_nothing()
    {
        Assert.Empty(Validate("""{ "Accounts": { "work": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" } } } }"""));
    }

    [Fact]
    public void An_incomplete_account_is_a_warning_and_the_file_still_saves()
    {
        var findings = Validate("""{ "Accounts": { "work": { "Provider": "Teams", "Rights": "Read" } } }""");

        var finding = Assert.Single(findings);
        Assert.Equal(ValidationCodes.ChatAuth, finding.Code);
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        Assert.Equal("Orkeon:Tools:Chat:Accounts:work:Auth:ClientId", finding.Path);
    }

    [Fact]
    public void A_default_account_that_names_none_of_them_says_the_engines_sentence()
    {
        var finding = Assert.Single(Validate("""
            { "DefaultAccount": "office", "Accounts": { "work": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" } },
              "Archive": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" } } } }
            """));

        Assert.Equal(ValidationCodes.ChatDefault, finding.Code);
        Assert.Equal("Unknown chat account 'office'. Configured: Archive, work.", finding.Text);
    }

    [Fact]
    public void Two_names_equal_but_for_the_case_are_an_error_when_they_set_one_same_key_and_a_warning_otherwise()
    {
        var clash = Validate("""{ "Accounts": { "work": { "Provider": "Teams" }, "WORK": { "Provider": "Teams" } } }""");
        var merge = Validate("""{ "Accounts": { "work": { "Provider": "Teams" }, "WORK": { "Rights": "Read" } } }""");

        var error = Assert.Single(clash, message => message.Code == ValidationCodes.ChatDuplicate);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.StartsWith("Chat accounts 'work' and 'WORK' differ only by case and both set Provider", error.Text, StringComparison.Ordinal);

        var warning = Assert.Single(merge, message => message.Code == ValidationCodes.ChatDuplicate);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
    }

    [Fact]
    public void Two_keys_equal_but_for_the_case_in_one_account_are_said_alone()
    {
        var findings = Validate("""{ "Accounts": { "work": { "Provider": "Teams", "provider": "TeamsWebhook", "Rights": "Nothing" } } }""");

        var twin = Assert.Single(findings);
        Assert.Equal(ValidationCodes.ChatTwin, twin.Code);
        Assert.Equal(ValidationSeverity.Error, twin.Severity);
        Assert.Equal("Orkeon:Tools:Chat:Accounts:work:Provider", twin.Path);
    }

    [Fact]
    public void An_unreadable_screening_switch_is_said_for_the_section_and_for_each_account()
    {
        var findings = Validate("""
            { "Screening": { "WithholdRejected": "maybe" },
              "Accounts": { "work": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" } } } }
            """);

        Assert.Equal([ValidationCodes.ChatScreening, ValidationCodes.ChatScreening], findings.Select(finding => finding.Code));
        Assert.Equal("Orkeon:Tools:Chat:Screening:WithholdRejected", findings[0].Path);
        Assert.Equal("Orkeon:Tools:Chat:Accounts:work", findings[1].Path);
    }

    [Fact]
    public void The_email_findings_do_not_leak_into_the_chat_ones_nor_the_reverse()
    {
        var all = new AppSettingsValidator().ValidateJson("""
            { "Llm": { "Provider": "Ollama" }, "Orkeon": { "Tools": {
              "Email": { "Accounts": { "work": { "Provider": "Gmail" } } },
              "Chat": { "Accounts": { "work": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" } } } } } } }
            """);

        Assert.DoesNotContain(all, message => message.Code.StartsWith("STUDIO-CHAT-", StringComparison.Ordinal));
        Assert.Contains(all, message => message.Code.StartsWith("STUDIO-MAIL-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("perso", "CHAT_PERSO_CLIENT_SECRET")]
    [InlineData("my.team-2", "CHAT_MY_TEAM_2_CLIENT_SECRET")]
    public void The_variable_of_a_client_secret_is_derived_from_the_account_name(string account, string expected)
    {
        Assert.Equal(expected, ChatSecretNames.ClientSecretFor(account, []));
    }

    [Fact]
    public void A_derived_variable_stays_clear_of_the_names_already_taken_whatever_their_case()
    {
        Assert.Equal("CHAT_A_B_CLIENT_SECRET_2", ChatSecretNames.ClientSecretFor("a-b", ["chat_a_b_client_secret"]));
        Assert.Equal("CHAT_A_B_CLIENT_SECRET_3", ChatSecretNames.ClientSecretFor("a.b", ["CHAT_A_B_CLIENT_SECRET", "CHAT_A_B_CLIENT_SECRET_2", " "]));
        // Never ORKEON_: a run would read such a variable as configuration, its prefix removed.
        Assert.StartsWith("CHAT_", ChatSecretNames.ClientSecretFor("orkeon", []), StringComparison.Ordinal);
    }

    [Fact]
    public void The_variable_of_a_webhook_is_derived_from_the_account_name_too()
    {
        Assert.Equal("CHAT_ALERTS_WEBHOOK_URL", ChatSecretNames.WebhookVariableFor("alerts", []));
        Assert.Equal("CHAT_ALERTS_WEBHOOK_URL_2", ChatSecretNames.WebhookVariableFor("alerts", ["CHAT_ALERTS_WEBHOOK_URL"]));
    }

    [Fact]
    public void An_unknown_key_of_the_section_is_said_for_the_section_and_for_each_account()
    {
        var findings = Validate("""{ "Teams": {}, "Extra": 1, "Accounts": { "work": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "c" } } } }""");

        Assert.All(findings, finding => Assert.Equal(ValidationCodes.ChatKey, finding.Code));
        Assert.Contains(findings, finding => finding.Path == "Orkeon:Tools:Chat:Extra");
        Assert.Contains(findings, finding => finding.Path == "Orkeon:Tools:Chat:Accounts:work");
    }
}
