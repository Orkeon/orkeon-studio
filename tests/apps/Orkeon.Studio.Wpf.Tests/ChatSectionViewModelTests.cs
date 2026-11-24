using Orkeon.Studio.Core.Chat;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Email;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Core.Validation;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Capture.Worlds;
using Orkeon.Studio.Wpf.ViewModels.Config;
using Orkeon.Studio.Wpf.ViewModels.Services;
using Orkeon.Studio.Wpf.ViewModels.Shell;
using Orkeon.Tests.Shared.Timing;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The Microsoft Teams tab, on the model of the Drive one: the rows write <c>Orkeon:Tools:Chat</c> in
/// place, what each kind of account has no use for does not show, the secret of a webhook or of an
/// application goes to the key store and only its variable to the file, each row says what
/// <c>orkeon chat accounts</c> makes of its account, tests it with <c>orkeon chat check</c>, and signs
/// a person in with <c>orkeon chat login</c> in the panel the account tabs share. Every process is
/// <see cref="ScriptedOrkeonCli"/>: nothing is spawned.
/// </summary>
public sealed class ChatSectionViewModelTests
{
    private const string SettingsFile = "/home/me/.config/Orkeon/appsettings.json";

    private const string UserCode = "FAKE-C0DE";

    private const string DevicePage = "https://microsoft.com/devicelogin";

    private const string AuthorizationPage = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id=client-456&state=state-1";

    private const string Redirect = "http://127.0.0.1:53124/?code=auth-code-1&state=state-1";

    private const string NoCode = "The pasted address carries no authorization code: it may be cut short.";

    private const string Two = """
        { "Llm": { "Provider": "Ollama" }, "Orkeon": { "Tools": { "Chat": { "Accounts": {
          "perso": { "Provider": "Teams", "Rights": "Read, Send", "Note": "mine", "Read": { "AllowedPlaces": ["*"] },
                     "Auth": { "ClientId": "client-456", "AdminGranted": ["Later"] } },
          "office": { "Provider": "Teams", "Rights": "Read", "Auth": { "ClientId": "client-123" } } } } } } }
        """;

    private const string DeviceCodeLine =
        $$"""{"v":2,"seq":1,"ts":"2026-10-17T09:00:00Z","kind":"chat.login.device_code","verification_uri":"{{DevicePage}}","user_code":"{{UserCode}}","expires_in":900}""";

    private const string AuthorizationLine =
        $$"""{"v":2,"seq":1,"ts":"2026-10-17T09:00:00Z","kind":"chat.login.authorization_url","authorization_uri":"{{AuthorizationPage}}"}""";

    private static string CompletedLine(string account) =>
        $$"""{"v":2,"seq":3,"ts":"2026-10-17T09:00:05Z","kind":"chat.login.completed","account":"{{account}}"}""";

    private static string RejectedLine(string message) =>
        $$"""{"v":2,"seq":2,"ts":"2026-10-17T09:00:03Z","kind":"chat.login.redirect_rejected","message":"{{message}}"}""";

    private static string ErrorLine(string code, string message) =>
        $$"""{"v":2,"seq":2,"ts":"2026-10-17T09:00:05Z","kind":"error","code":"{{code}}","message":"{{message}}","recoverable":false}""";

    /// <summary>What <c>chat accounts</c> answers: the engine's order, sorted by name.</summary>
    private static string Accounts(bool officeReady = false, bool persoReady = true) => $$"""
        [{"name":"office","provider":"Teams","rights":"Read","application":false,"default":false,"ready":{{(officeReady ? "true" : "false")}},"problem":{{(officeReady ? "null" : "\"Chat account 'office' is not signed in: run `orkeon chat login office`.\"")}}},
         {"name":"perso","provider":"Teams","rights":"Read, Send","application":false,"default":false,"ready":{{(persoReady ? "true" : "false")}},"problem":null}]
        """.ReplaceLineEndings("");

    private sealed class Fixture
    {
        public ChatSectionViewModel Section { get; set; } = null!;
        public AppSettingsDocument Document { get; set; } = null!;
        public ScriptedOrkeonCli Cli { get; init; } = new();
        public FakeApiKeyStore Keys { get; } = new();
        public StubTimeProvider Clock { get; } = new();
        public ManualUiTicker Ticker { get; } = new();
        public RecordingBrowserOpener Browser { get; } = new();
        public InMemoryClipboardService Clipboard { get; } = new();
        public int Changes { get; set; }

        /// <summary>Plays the document's unsaved edits.</summary>
        public bool Dirty { get; set; }

        public ChatAccountRowViewModel Row(string name) => Section.Accounts.Single(row => row.Name == name);

        public int Runs(string subVerb) => Cli.Requests.Count(request => request.Arguments is ["chat", var verb, ..] && verb == subVerb);
    }

    private static Fixture Build(string json = Two, ScriptedOrkeonCli? cli = null, bool withCli = true, bool binaryPresent = true)
    {
        var fixture = new Fixture { Cli = cli ?? new ScriptedOrkeonCli().Answer("chat accounts", 0, Accounts()) };
        fixture.Document = AppSettingsDocument.Parse(json);
        fixture.Section = new ChatSectionViewModel(
            () => fixture.Document,
            () => fixture.Changes++,
            settingsPath: () => SettingsFile,
            keyStore: fixture.Keys,
            engine: withCli
                ? new ChatCliSeams
                {
                    Cli = new ChatCliClient(new OrkeonProcessRunner(
                        fixture.Cli,
                        new OrkeonBinaryLocator(binaryPresent ? FakeExecutableProbe.WithOrkeonInstalled() : new FakeExecutableProbe()))),
                    IsDirty = () => fixture.Dirty,
                    SignIn = new EmailSignInServices
                    {
                        Browser = fixture.Browser,
                        Clipboard = fixture.Clipboard,
                        Ticker = fixture.Ticker,
                        Clock = fixture.Clock,
                    },
                }
                : null);
        return fixture;
    }

    private static Task Guarded(Task task) => task.WaitAsync(Polling.DefaultTimeout, TestContext.Current.CancellationToken);

    // ── the rows and the file ──

    [Fact]
    public void Each_account_of_the_file_is_a_row_in_document_order_and_the_first_one_shows()
    {
        var fixture = Build();

        Assert.Equal(["perso", "office"], fixture.Section.Accounts.Select(row => row.Name));
        Assert.Same(fixture.Row("perso"), fixture.Section.SelectedAccount);
        Assert.True(fixture.Section.ShowsAccountTab);
        Assert.Equal(ChatSection.Values.Teams, fixture.Row("perso").Provider);
        Assert.Equal("Read, Send", fixture.Row("perso").RightsLine);
        Assert.True(fixture.Row("perso").CanRead);
        Assert.True(fixture.Row("perso").CanSend);
        Assert.False(fixture.Row("perso").CanReact);
        Assert.Equal($"Accounts are written to {SettingsFile}. A team launched on another settings file does not see them.", fixture.Section.SettingsFileLine);
    }

    [Fact]
    public void An_edit_writes_the_account_in_place_and_keeps_what_studio_does_not_model()
    {
        var fixture = Build();
        var perso = fixture.Row("perso");

        perso.CanReact = true;
        perso.Tenant = "  contoso.com ";
        perso.AddSendPlaceCommand.Execute(null);
        perso.SendPlaces[0].Place = "team:Support";
        perso.GrantMeetings = true;
        perso.MaxPerHour = "12";

        var written = fixture.Document.Chat.GetAccount("perso")!;
        Assert.Equal(ChatRights.Read | ChatRights.Send | ChatRights.React, written.Rights);
        Assert.Equal("contoso.com", written.Tenant);
        Assert.Equal(["*"], written.ReadPlaces);
        Assert.Equal(["team:Support"], written.SendPlaces);
        Assert.Equal(["Later", "Meetings"], written.AdminGranted);
        Assert.Equal(12, written.MaxPerHour);
        Assert.Equal("mine", fixture.Document.GetString("Orkeon:Tools:Chat:Accounts:perso:Note"));
        Assert.True(fixture.Changes > 0);

        // A grant turned off leaves the entry Studio does not know.
        perso.GrantMeetings = false;
        Assert.Equal(["Later"], fixture.Document.Chat.GetAccount("perso")!.AdminGranted);
    }

    [Fact]
    public void Choosing_the_engines_default_removes_the_key()
    {
        var fixture = Build();
        var office = fixture.Row("office");
        Assert.Equal(ChatSection.Values.OAuth2, office.AuthMethod);
        Assert.Equal(ChatSection.Values.Global, office.Cloud);

        office.AuthMethod = ChatSection.Values.ClientCredentials;
        office.Cloud = ChatSection.Values.UsGov;
        Assert.Equal("ClientCredentials", fixture.Document.Chat.GetAccount("office")!.AuthMethod);
        Assert.Equal("UsGov", fixture.Document.Chat.GetAccount("office")!.Cloud);

        office.AuthMethod = ChatSection.Values.OAuth2;
        office.Cloud = ChatSection.Values.Global;
        Assert.Null(fixture.Document.GetNode("Orkeon:Tools:Chat:Accounts:office:Auth:Method"));
        Assert.Null(fixture.Document.GetNode("Orkeon:Tools:Chat:Accounts:office:Cloud"));
        Assert.DoesNotContain(fixture.Section.CloudChoices, choice => choice.Value == ChatSection.Values.China);
    }

    [Fact]
    public void A_number_field_that_holds_no_whole_number_says_so_and_is_not_written()
    {
        var fixture = Build();
        var perso = fixture.Row("perso");
        perso.MaxPerHour = "50";

        perso.MaxPerHour = "lots";

        Assert.True(perso.HasUnreadNumber);
        Assert.Equal(50, fixture.Document.Chat.GetAccount("perso")!.MaxPerHour);

        perso.MaxPerHour = "";
        Assert.False(perso.HasUnreadNumber);
        Assert.Null(fixture.Document.GetNode("Orkeon:Tools:Chat:Accounts:perso:Send"));
    }

    [Fact]
    public void An_account_with_no_place_says_it_reads_or_writes_nowhere_and_a_line_that_is_no_place_says_it_on_itself()
    {
        var fixture = Build();
        var office = fixture.Row("office");
        Assert.True(office.ShowReadClosed);
        Assert.False(office.ShowSendPlaces);

        office.CanEdit = true;

        Assert.True(office.ShowSendPlaces);
        Assert.True(office.ShowSendClosed);

        office.AddReadPlaceCommand.Execute(null);
        Assert.True(office.ShowReadClosed);
        office.ReadPlaces[0].Place = "general";
        Assert.True(office.ReadPlaces[0].IsInvalid);
        Assert.False(office.ShowReadClosed);
        Assert.Contains(office.Problems, problem => problem.Code == ValidationCodes.ChatPlaces);

        office.RemovePlaceCommand.Execute(office.ReadPlaces[0]);
        Assert.Empty(fixture.Document.Chat.GetAccount("office")!.ReadPlaces);
    }

    [Fact]
    public void Each_kind_of_account_shows_only_what_it_has_a_use_for()
    {
        var fixture = Build();
        var office = fixture.Row("office");
        Assert.True(office.ShowRead && office.ShowSend && office.ShowEdit && office.ShowReact && office.ShowMeetings);
        Assert.True(office.ShowSignIn && office.ShowGrants && office.ShowMaxPerHour);
        Assert.False(office.ShowSecret);

        office.AuthMethod = ChatSection.Values.ClientCredentials;
        Assert.True(office.ShowRead && office.ShowMeetings && office.ShowSecret && office.ShowAuthMethod);
        Assert.False(office.ShowSend || office.ShowEdit || office.ShowSignIn || office.ShowGrants || office.ShowMaxPerHour);

        // A right the file grants all the same stays reachable.
        office.CanSend = true;
        Assert.True(office.ShowSend);
        office.CanSend = false;

        office.AuthMethod = ChatSection.Values.OAuth2;
        office.CanRead = false;
        office.Provider = ChatSection.Values.TeamsWebhook;
        Assert.True(office.IsWebhook && office.ShowSecret && office.ShowMaxPerHour);
        Assert.False(office.ShowRead || office.ShowSend || office.ShowMeetings || office.ShowSignIn || office.ShowReadPlaces || office.ShowSendPlaces || office.ShowGrants);
    }

    [Fact]
    public void What_the_run_will_say_follows_every_edit()
    {
        var fixture = Build();
        var office = fixture.Row("office");
        Assert.False(office.HasProblems);

        office.CanRead = false;

        Assert.Equal([ValidationCodes.ChatRights], office.Problems.Select(problem => problem.Code));

        office.CanRead = true;
        office.Tenant = "my tenant";
        Assert.Equal([ValidationCodes.ChatAuth], office.Problems.Select(problem => problem.Code));
    }

    [Fact]
    public void The_expert_sees_the_method_the_cloud_and_the_variables_and_the_novice_none_of_them()
    {
        var fixture = Build();
        var office = fixture.Row("office");
        Assert.False(office.ShowAuthMethod || office.ShowCloud || office.ShowClientSecretEnvVar);

        fixture.Section.IsExpert = true;

        Assert.True(office.ShowAuthMethod && office.ShowCloud && office.ShowSendPlaces);
        Assert.False(office.ShowClientSecretEnvVar);

        office.AuthMethod = ChatSection.Values.ClientCredentials;
        Assert.True(office.ShowClientSecretEnvVar && office.ShowCertificateEnvVar && office.ShowFederatedTokenFileEnvVar);
        Assert.False(office.ShowWebhookEnvVar);
    }

    // ── the box that adds an account ──

    [Fact]
    public void Adding_writes_nothing_until_the_name_is_usable_then_the_account_with_its_service()
    {
        var fixture = Build();
        fixture.Section.BeginAddCommand.Execute(null);
        Assert.True(fixture.Section.IsAdding);
        Assert.Equal(ChatSection.Values.Teams, fixture.Section.NewAccountProvider);
        Assert.False(fixture.Section.ConfirmAddCommand.CanExecute(null));

        fixture.Section.NewAccountName = "PERSO";
        Assert.True(fixture.Section.HasAddRefusal);
        Assert.False(fixture.Section.ConfirmAddCommand.CanExecute(null));

        fixture.Section.NewAccountName = "-bad";
        Assert.True(fixture.Section.HasAddRefusal);

        fixture.Section.NewAccountName = " team ";
        fixture.Section.NewAccountProvider = ChatSection.Values.TeamsWebhook;
        Assert.False(fixture.Section.HasAddRefusal);
        fixture.Section.ConfirmAddCommand.Execute(null);

        Assert.False(fixture.Section.IsAdding);
        Assert.Equal(["perso", "office", "team"], fixture.Document.Chat.AccountNames);
        Assert.Equal(ChatSection.Values.TeamsWebhook, fixture.Document.Chat.GetAccount("team")!.Provider);
        Assert.Same(fixture.Row("team"), fixture.Section.SelectedAccount);
        // A webhook has one right, which is written; it is not usable yet, and said: it names no variable.
        Assert.Equal(ChatRights.Send, fixture.Document.Chat.GetAccount("team")!.Rights);
        Assert.Equal([ValidationCodes.ChatWebhook], fixture.Row("team").Problems.Select(problem => problem.Code));
    }

    [Fact]
    public void Renaming_moves_the_account_and_the_default_that_named_it()
    {
        var fixture = Build();
        fixture.Section.DefaultAccount = "office";
        var office = fixture.Row("office");

        fixture.Section.RenameAccountCommand.Execute(office);
        Assert.True(office.IsRenaming);
        office.RenameText = "Perso";
        Assert.True(office.HasRenameRefusal);
        office.RenameText = "work";
        office.ConfirmRenameCommand.Execute(null);

        Assert.Equal(["perso", "work"], fixture.Document.Chat.AccountNames);
        Assert.Equal("work", fixture.Section.DefaultAccount);
        Assert.Equal("work", office.Name);
        Assert.False(office.IsRenaming);
    }

    [Fact]
    public void Removing_asks_first_then_takes_the_account_and_shows_a_neighbour()
    {
        var fixture = Build();
        var perso = fixture.Row("perso");

        fixture.Section.RemoveAccountCommand.Execute(perso);
        Assert.True(perso.IsConfirmingRemove);
        Assert.Equal(2, fixture.Document.Chat.AccountNames.Count);

        perso.ConfirmRemoveCommand.Execute(null);

        Assert.Equal(["office"], fixture.Document.Chat.AccountNames);
        Assert.Same(fixture.Row("office"), fixture.Section.SelectedAccount);

        fixture.Section.RemoveAccountCommand.Execute(fixture.Row("office"));
        fixture.Row("office").ConfirmRemoveCommand.Execute(null);
        Assert.Null(fixture.Document.GetNode("Orkeon:Tools:Chat"));
        Assert.False(fixture.Section.HasSelectedAccount);
    }

    [Fact]
    public void The_settings_of_the_section_write_their_keys_and_remove_them_when_cleared()
    {
        var fixture = Build();

        fixture.Section.WithholdRejected = true;
        fixture.Section.CredentialsDirectory = "/srv/tokens";
        Assert.True(fixture.Document.Chat.WithholdRejected);
        Assert.Equal("/srv/tokens", fixture.Document.Chat.CredentialsDirectory);

        fixture.Section.WithholdRejected = false;
        fixture.Section.CredentialsDirectory = "";
        Assert.Null(fixture.Document.GetNode("Orkeon:Tools:Chat:Screening"));
        Assert.Null(fixture.Document.GetNode("Orkeon:Tools:Chat:CredentialsDirectory"));
    }

    // ── the client secret ──

    [Fact]
    public void The_address_of_a_webhook_and_the_secret_of_an_application_are_kept_and_the_file_holds_only_their_variables()
    {
        var fixture = Build("""
            { "Orkeon": { "Tools": { "Chat": { "Accounts": {
              "alerts": { "Provider": "TeamsWebhook", "Rights": "Send" },
              "robot": { "Provider": "Teams", "Rights": "Read", "Read": { "AllowedPlaces": ["chat:19:a@thread.v2"] },
                         "Auth": { "Method": "ClientCredentials", "ClientId": "c", "Tenant": "contoso.com" } } } } } } }
            """);
        var alerts = fixture.Row("alerts");
        var robot = fixture.Row("robot");
        Assert.Equal("CHAT_ALERTS_WEBHOOK_URL", alerts.Secret!.EnvName);
        Assert.Equal("CHAT_ROBOT_CLIENT_SECRET", robot.Secret!.EnvName);
        Assert.NotEqual(alerts.SecretLabel, robot.SecretLabel);
        Assert.True(alerts.HasProblems && robot.HasProblems);

        alerts.Secret.KeyInput = "https://example.com/hook?sig=abc";
        alerts.Secret.StoreCommand.Execute(null);
        robot.Secret.KeyInput = "s3cret";
        robot.Secret.StoreCommand.Execute(null);

        Assert.Equal("https://example.com/hook?sig=abc", fixture.Keys.Saved["CHAT_ALERTS_WEBHOOK_URL"]);
        Assert.Equal("s3cret", fixture.Keys.Saved["CHAT_ROBOT_CLIENT_SECRET"]);
        Assert.Equal("CHAT_ALERTS_WEBHOOK_URL", fixture.Document.Chat.GetAccount("alerts")!.WebhookEnvVar);
        Assert.Equal("CHAT_ROBOT_CLIENT_SECRET", fixture.Document.Chat.GetAccount("robot")!.ClientSecretEnvVar);
        Assert.DoesNotContain("sig=abc", fixture.Document.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", fixture.Document.ToJson(), StringComparison.Ordinal);
        Assert.True(alerts.SecretStored && robot.SecretStored);
        Assert.False(alerts.HasProblems || robot.HasProblems);
    }

    [Fact]
    public void An_application_that_names_another_credential_has_no_secret_to_type_and_a_typed_one_never_changes_kind()
    {
        var fixture = Build();
        var office = fixture.Row("office");
        office.AuthMethod = ChatSection.Values.ClientCredentials;
        office.Secret!.KeyInput = "typed";
        office.Provider = ChatSection.Values.TeamsWebhook;
        Assert.Equal("", office.Secret!.KeyInput);

        office.Provider = ChatSection.Values.Teams;
        office.CertificatePathEnvVar = "CERTIFICATE";

        Assert.False(office.ShowSecret);
    }

    // ── what the engine makes of each account ──

    [Fact]
    public async Task Arriving_reads_the_states_of_the_saved_file_and_pairs_them_by_name()
    {
        var fixture = Build();

        await Guarded(fixture.Section.RefreshStatesAsync());

        Assert.Equal(["chat", "accounts", "--json", "--settings", SettingsFile], Assert.Single(fixture.Cli.Requests).Arguments);
        Assert.True(fixture.Row("perso").IsReady);
        Assert.Equal("Ready", fixture.Row("perso").State);
        Assert.Equal(EmailAccountReadiness.NotReady, fixture.Row("office").StateKind);
        Assert.Equal("Chat account 'office' is not signed in: run `orkeon chat login office`.", fixture.Row("office").StateDetail);
    }

    [Fact]
    public async Task An_account_the_engine_set_aside_says_so_and_a_missing_cli_makes_every_state_unknown()
    {
        var setAside = Build(cli: new ScriptedOrkeonCli().Answer("chat accounts", 0,
            """[{"name":"perso","provider":"?","default":false,"ready":false,"problem":"Provider is required"}]"""));
        var missing = Build(binaryPresent: false);

        await Guarded(setAside.Section.RefreshStatesAsync());
        await Guarded(missing.Section.RefreshStatesAsync());

        Assert.Equal(EmailAccountReadiness.SetAside, setAside.Row("perso").StateKind);
        Assert.Null(setAside.Row("perso").StateDetail);
        Assert.Equal(EmailAccountReadiness.None, setAside.Row("office").StateKind);
        Assert.Equal(EmailAccountReadiness.Unknown, missing.Row("perso").StateKind);
        Assert.StartsWith("Unknown — ", missing.Row("perso").State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_section_built_without_a_client_reads_nothing_tests_nothing_and_signs_nothing_in()
    {
        var fixture = Build(withCli: false);

        await Guarded(fixture.Section.RefreshStatesAsync());

        Assert.Empty(fixture.Cli.Requests);
        Assert.False(fixture.Row("perso").TestCommand.CanExecute(null));
        Assert.False(fixture.Row("perso").SignInCommand.CanExecute(null));
    }

    // ── the connection test ──

    [Fact]
    public async Task Testing_runs_the_check_on_the_saved_file_and_shows_the_engines_sentence()
    {
        const string Summary = "Chat account 'perso' is reachable: Read, Send ready.";
        var cli = new ScriptedOrkeonCli()
            .Answer("chat accounts", 0, Accounts())
            .Answer("chat check", 0, $$"""{"v":1,"seq":1,"kind":"chat.check.completed","account":"perso","ready":["Read"],"summary":"{{Summary}}"}""");
        var fixture = Build(cli: cli);
        var perso = fixture.Row("perso");

        await Guarded(perso.TestCommand.ExecuteAsync());

        Assert.Equal(["chat", "check", "perso", "--events", "jsonl", "--settings", SettingsFile], fixture.Cli.Requests[^1].Arguments);
        Assert.True(perso.LastCheckReachable);
        Assert.Equal("Reachable", perso.LastCheckHeadline);
        Assert.Equal(Summary, perso.LastCheckDetail);

        // The verdict was of the account as it stood: an edit clears it.
        perso.Tenant = "contoso.com";
        Assert.False(perso.HasLastCheck);
    }

    [Fact]
    public async Task The_test_of_a_webhook_never_says_it_was_reached()
    {
        var cli = new ScriptedOrkeonCli()
            .Answer("chat accounts", 0, Accounts())
            .Answer("chat check", 0, """{"v":1,"seq":1,"kind":"chat.check.completed","account":"alerts","ready":true,"summary":"a webhook, which was not called"}""");
        var fixture = Build("""
            { "Orkeon": { "Tools": { "Chat": { "Accounts": {
              "alerts": { "Provider": "TeamsWebhook", "Rights": "Send", "Webhook": { "UrlEnvVar": "HOOK" } } } } } } }
            """, cli);
        var alerts = fixture.Row("alerts");

        await Guarded(alerts.TestCommand.ExecuteAsync());

        Assert.Equal(EnglishStudioStrings.Instance[StudioStringKeys.ChatTestWebhook], alerts.LastCheckHeadline);
    }

    [Fact]
    public void A_secret_typed_for_one_kind_is_not_kept_for_the_other_when_both_name_one_variable()
    {
        var row = Build("""
            { "Orkeon": { "Tools": { "Chat": { "Accounts": {
              "alerts": { "Provider": "TeamsWebhook", "Rights": "Send", "Webhook": { "UrlEnvVar": "SHARED" },
                          "Auth": { "Method": "ClientCredentials", "ClientId": "c", "ClientSecretEnvVar": "SHARED" } } } } } } }
            """).Row("alerts");
        row.Secret!.KeyInput = "https://example.com/hook";
        var before = row.SecretLabel;
        Assert.True(row.ShowApplication);

        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        row.Provider = ChatSection.Values.Teams;

        Assert.Equal("SHARED", row.Secret!.EnvName);
        Assert.Equal("", row.Secret.KeyInput);
        Assert.NotEqual(before, row.SecretLabel);
        Assert.Contains(nameof(row.SecretLabel), raised);
    }

    [Fact]
    public async Task A_test_refused_for_want_of_a_sign_in_says_to_sign_in()
    {
        var cli = new ScriptedOrkeonCli()
            .Answer("chat accounts", 0, Accounts())
            .Answer("chat check", 1, ErrorLine(EmailCheckCodes.SignInRequired, "Run `orkeon chat login perso`."));
        var fixture = Build(cli: cli);
        var perso = fixture.Row("perso");

        await Guarded(perso.TestCommand.ExecuteAsync());

        Assert.Equal(EnglishStudioStrings.Instance[StudioStringKeys.MailTestSignInFirst], perso.LastCheckHeadline);
        Assert.True(perso.HasLastCheck);
        Assert.NotNull(perso.LastCheckHint);
    }

    [Fact]
    public void While_the_document_has_unsaved_edits_nothing_that_reads_the_file_can_start()
    {
        var fixture = Build();
        var office = fixture.Row("office");

        fixture.Dirty = true;
        fixture.Section.RefreshSavedFile();

        Assert.True(fixture.Section.IsStateOfSavedFile);
        Assert.False(office.TestCommand.CanExecute(null));
        Assert.False(office.SignInCommand.CanExecute(null));
        Assert.False(office.SignOutCommand.CanExecute(null));
        office.SignInCommand.Execute(null);
        Assert.Null(office.SignIn);
    }

    // ── signing in and out ──

    [Fact]
    public void An_account_a_person_signs_in_is_offered_the_sign_in_and_one_without_a_service_is_not()
    {
        var fixture = Build();
        Assert.True(fixture.Row("perso").ShowSignIn);
        Assert.True(fixture.Row("office").ShowSignIn);

        fixture.Row("office").Provider = null;

        Assert.False(fixture.Row("office").ShowSignIn);
        Assert.False(fixture.Row("office").SignInCommand.CanExecute(null));
    }

    [Fact]
    public void A_device_sign_in_shows_the_page_and_the_code_and_counts_the_code_down()
    {
        var cli = new ScriptedOrkeonCli().Answer("chat accounts", 0, Accounts()).Converse("chat login", [DeviceCodeLine], _ => []);
        var fixture = Build(cli: cli);

        fixture.Row("office").SignInCommand.Execute(null);
        var signIn = fixture.Row("office").SignIn!;

        Assert.Equal(["chat", "login", "office", "--events", "jsonl", "--settings", SettingsFile], Assert.Single(fixture.Cli.Requests).Arguments);
        Assert.True(fixture.Section.IsSigningIn);
        Assert.Equal(EmailSignInPhase.DeviceCode, signIn.Phase);
        Assert.Equal(UserCode, signIn.UserCode);
        Assert.Equal(DevicePage, signIn.Address);
        Assert.Equal("The code expires in 15 min", signIn.ExpiresInText);
        Assert.True(fixture.Ticker.IsRunning);
        Assert.Empty(fixture.Browser.Opened);

        fixture.Clock.Now += TimeSpan.FromMinutes(15);
        fixture.Ticker.Tick();

        Assert.True(signIn.IsExpired);
        Assert.True(signIn.CanRestart);
        Assert.Equal(1, fixture.Cli.StoppedConversations);
    }

    [Fact]
    public async Task A_browser_sign_in_shows_the_address_takes_the_pasted_one_says_why_one_is_rejected_and_reads_the_states_once_signed_in()
    {
        var cli = new ScriptedOrkeonCli().Answer("chat accounts", 0, Accounts(persoReady: false));
        cli.Converse("chat login", [AuthorizationLine], line =>
        {
            if (line != Redirect)
                return [RejectedLine(NoCode)];

            cli.Answer("chat accounts", 0, Accounts(persoReady: true));
            return [CompletedLine("perso")];
        });
        var fixture = Build(cli: cli);
        await Guarded(fixture.Section.RefreshStatesAsync());
        Assert.False(fixture.Row("perso").IsReady);

        fixture.Row("perso").SignInCommand.Execute(null);
        var signIn = fixture.Row("perso").SignIn!;

        Assert.True(signIn.IsAuthorizationUrl);
        Assert.Equal(AuthorizationPage, signIn.Address);
        Assert.True(signIn.CanOpen);
        signIn.CopyLinkCommand.Execute(null);
        Assert.Equal(AuthorizationPage, fixture.Clipboard.LastText);
        signIn.OpenCommand.Execute(null);
        Assert.Equal([AuthorizationPage], fixture.Browser.Opened.Select(uri => uri.AbsoluteUri));

        signIn.PastedRedirect = "http://127.0.0.1:53124/?state=state-1";
        signIn.SubmitRedirectCommand.Execute(null);
        Assert.True(signIn.RedirectRejected);
        Assert.Equal(NoCode, signIn.RedirectRejectedDetail);
        Assert.Equal("http://127.0.0.1:53124/?state=state-1", signIn.PastedRedirect);
        Assert.True(signIn.IsWaiting);

        signIn.PastedRedirect = Redirect;
        signIn.SubmitRedirectCommand.Execute(null);
        await Guarded(signIn.Ended);

        Assert.True(signIn.IsCompleted);
        Assert.True(fixture.Row("perso").IsReady);
    }

    [Fact]
    public async Task A_refused_sign_in_shows_the_verbs_sentence_and_is_not_tried_again()
    {
        var cli = new ScriptedOrkeonCli()
            .Answer("chat accounts", 0, Accounts())
            .Answer("chat login", 1, ErrorLine(EmailCheckCodes.CredentialMissing, "The client secret of chat account 'perso' is not set."));
        var fixture = Build(cli: cli);

        fixture.Row("perso").SignInCommand.Execute(null);
        var signIn = fixture.Row("perso").SignIn!;
        await Guarded(signIn.Ended);

        Assert.True(signIn.IsFailed);
        Assert.Equal("The client secret of chat account 'perso' is not set.", signIn.FailureDetail);
        Assert.False(signIn.CanRestart);
        Assert.Equal(1, fixture.Runs("login"));
    }

    [Fact]
    public async Task Signing_out_asks_first_then_forgets_the_tokens_and_reads_the_states_again()
    {
        var fixture = Build(cli: new ScriptedOrkeonCli().Answer("chat accounts", 0, Accounts()).Answer("chat logout", 0));
        var office = fixture.Row("office");

        office.SignOutCommand.Execute(null);
        Assert.True(office.IsConfirmingSignOut);
        Assert.Equal("Forget the stored tokens of office? The account will need a new sign-in.", office.SignOutConfirmText);
        Assert.Equal(0, fixture.Runs("logout"));

        await Guarded(office.ConfirmSignOutCommand.ExecuteAsync());

        Assert.Equal(["chat", "logout", "office", "--settings", SettingsFile], fixture.Cli.Requests.Single(r => r.Arguments[1] == "logout").Arguments);
        Assert.Equal(1, fixture.Runs("accounts"));
        Assert.False(office.HasSignOutFailure);
    }

    [Fact]
    public void Stopping_the_tab_stops_a_sign_in_that_waits_and_takes_its_panel_away()
    {
        var cli = new ScriptedOrkeonCli().Answer("chat accounts", 0, Accounts()).Converse("chat login", [DeviceCodeLine], _ => []);
        var fixture = Build(cli: cli);
        fixture.Row("office").SignInCommand.Execute(null);
        Assert.Equal(1, fixture.Cli.LiveConversations);

        fixture.Section.StopActivity();

        Assert.False(fixture.Row("office").HasSignIn);
        Assert.False(fixture.Section.IsSigningIn);
        Assert.False(fixture.Ticker.IsRunning);
        Assert.Equal(1, fixture.Cli.StoppedConversations);
    }

    // ── the settings screen ──

    [Fact]
    public async Task The_chat_tab_reads_the_states_on_arrival_and_stops_its_activity_when_left()
    {
        var cli = new ScriptedOrkeonCli().Answer("chat accounts", 0, Accounts()).Converse("chat login", [DeviceCodeLine], _ => []);
        var config = new ConfigTabViewModel(
            new StudioServices
            {
                SettingsStore = new FakeAppSettingsStore(),
                Directories = new FakeDirectoryProbe(),
                KeyStore = new FakeApiKeyStore(),
                ChatCli = new ChatCliClient(new OrkeonProcessRunner(cli, new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled()))),
                Clipboard = new InMemoryClipboardService(),
            },
            globalPathOverride: SettingsFile);
        config.SetDocument(AppSettingsDocument.Parse(Two), SettingsFile);
        var screen = new SettingsScreenViewModel(
            config,
            new ModelProfilesViewModel(new InMemoryModelProfileStore(), config.Llm),
            new UiModeViewModel(UiModeViewModel.Novice));

        Assert.False(screen.IsChatTab);
        screen.ShowChatCommand.Execute(null);
        Assert.True(screen.IsChatTab);
        await Polling.WaitUntilAsync(() => config.Chat.Accounts.All(row => row.HasState));
        Assert.True(config.Chat.Accounts.Single(row => row.Name == "perso").IsReady);

        config.Chat.Accounts.Single(row => row.Name == "office").SignInCommand.Execute(null);
        Assert.True(config.Chat.IsSigningIn);

        screen.ShowMailsCommand.Execute(null);

        Assert.False(config.Chat.IsSigningIn);
        Assert.Equal(1, cli.StoppedConversations);
    }

    [Fact]
    public void The_mode_switch_reaches_the_chat_tab_and_a_new_document_rebuilds_its_rows()
    {
        var config = new ConfigTabViewModel(
            new StudioServices
            {
                SettingsStore = new FakeAppSettingsStore(),
                Directories = new FakeDirectoryProbe(),
                KeyStore = new FakeApiKeyStore(),
            },
            globalPathOverride: SettingsFile);
        config.SetDocument(AppSettingsDocument.Parse(Two), SettingsFile);
        var mode = new UiModeViewModel(UiModeViewModel.Novice);
        _ = new SettingsScreenViewModel(config, new ModelProfilesViewModel(new InMemoryModelProfileStore(), config.Llm), mode);

        Assert.False(config.Chat.IsExpert);
        mode.Mode = UiModeViewModel.Expert;
        Assert.True(config.Chat.IsExpert);

        config.SetDocument(AppSettingsDocument.CreateEmpty(), SettingsFile);
        Assert.False(config.Chat.HasAccounts);
        Assert.False(config.Chat.HasSelectedAccount);
    }
}
