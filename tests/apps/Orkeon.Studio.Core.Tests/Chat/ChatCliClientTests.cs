using Orkeon.Constants.Protocol;
using Orkeon.Studio.Core.Chat;
using Orkeon.Studio.Core.Email;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Tests.Doubles;

namespace Orkeon.Studio.Core.Tests.Chat;

/// <summary>
/// The Studio side of <c>orkeon chat</c>, on the model of <c>EmailCliClientTests</c>: the argv
/// always names the settings file, the list is read off standard output alone, the check and the
/// sign-in are read off the event lines of <see cref="ChatEventKinds"/> — the e-mail protocol
/// under the <c>chat.</c> prefix —, and whatever the process did comes back as a typed result,
/// never as an exception. Everything runs on <see cref="FakeProcessLauncher"/>.
/// </summary>
public sealed class ChatCliClientTests
{
    private static readonly string InstallDirectory = Path.Combine("/", "opt", "orkeon");
    private static readonly string BinaryPath = Path.Combine(InstallDirectory, "orkeon");
    private static readonly string Settings = Path.Combine("/", "home", "me", "my settings", "appsettings.json");

    // What `chat accounts --json` prints: indented, one property per line, nulls written.
    private static readonly string[] TwoAccounts =
    [
        "[",
        "  {",
        "    \"name\": \"work\",",
        "    \"provider\": \"Teams\",",
        "    \"rights\": \"Read, Send\",",
        "    \"application\": false,",
        "    \"default\": false,",
        "    \"ready\": false,",
        "    \"problem\": \"Chat account 'work' is not signed in: run `orkeon chat login work`.\"",
        "  },",
        "  {",
        "    \"name\": \"perso\",",
        "    \"provider\": \"Teams\",",
        "    \"rights\": \"Read\",",
        "    \"application\": true,",
        "    \"default\": true,",
        "    \"ready\": true,",
        "    \"problem\": null",
        "  }",
        "]",
    ];

    private const string Redirect = "http://127.0.0.1:53124/?code=auth-code-1&state=state-1";

    private const string DeviceCodeLine =
        """{"v":2,"seq":1,"ts":"2026-10-17T09:00:00Z","kind":"chat.login.device_code","verification_uri":"https://microsoft.com/devicelogin","user_code":"WXYZ-1234","expires_in":900}""";

    private const string AuthorizationLine =
        """{"v":2,"seq":1,"ts":"2026-10-17T09:00:00Z","kind":"chat.login.authorization_url","authorization_uri":"https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id=c&state=state-1"}""";

    private static string CompletedLine(int seq, string account) =>
        $$"""{"v":2,"seq":{{seq}},"ts":"2026-10-17T09:00:05Z","kind":"chat.login.completed","account":"{{account}}"}""";

    private static string RejectedLine(int seq, string message) =>
        $$"""{"v":2,"seq":{{seq}},"ts":"2026-10-17T09:00:03Z","kind":"chat.login.redirect_rejected","message":"{{message}}"}""";

    private static string ErrorLine(int seq, string code, string message) =>
        $$"""{"v":2,"seq":{{seq}},"ts":"2026-10-17T09:00:05Z","kind":"error","code":"{{code}}","message":"{{message}}","recoverable":false}""";

    private static ChatCliClient CreateClient(IProcessLauncher launcher, bool binaryPresent = true)
    {
        var probe = new FakeExecutableProbe { BaseDirectory = InstallDirectory };
        if (binaryPresent)
            probe.WithFile(BinaryPath);

        return new ChatCliClient(new OrkeonProcessRunner(launcher, new OrkeonBinaryLocator(probe, ["orkeon"])));
    }

    // ── the argv ──

    [Fact]
    public void Every_verb_names_the_settings_file_and_the_two_that_converse_ask_for_events()
    {
        Assert.Equal(["chat", "accounts", "--json", "--settings", Settings], ChatArgumentsBuilder.BuildAccounts(Settings));
        Assert.Equal(["chat", "check", "perso", "--events", "jsonl", "--settings", Settings], ChatArgumentsBuilder.BuildCheck("perso", Settings));
        Assert.Equal(["chat", "login", "perso", "--events", "jsonl", "--settings", Settings], ChatArgumentsBuilder.BuildLogin("perso", Settings));
        Assert.Equal(["chat", "logout", "perso", "--settings", Settings], ChatArgumentsBuilder.BuildLogout("perso", Settings));
        Assert.Throws<ArgumentException>(() => ChatArgumentsBuilder.BuildCheck(" ", Settings));
        Assert.Throws<ArgumentException>(() => ChatArgumentsBuilder.BuildAccounts(""));
    }

    // ── the list ──

    [Fact]
    public async Task Listing_asks_for_the_json_form_of_the_named_settings_file_and_reads_every_account()
    {
        var launcher = new FakeProcessLauncher().WithStandardError("Using settings: " + Settings).WithStandardOutput(TwoAccounts);

        var result = await CreateClient(launcher).ListAsync(Settings, TestContext.Current.CancellationToken);

        var request = Assert.Single(launcher.Requests);
        Assert.Equal(BinaryPath, request.FileName);
        Assert.Equal(["chat", "accounts", "--json", "--settings", Settings], request.Arguments);

        Assert.Null(result.Failure);
        Assert.Equal(2, result.Accounts.Count);

        var work = result.Accounts[0];
        Assert.Equal("work", work.Name);
        Assert.False(work.Ready);
        Assert.Equal("Chat account 'work' is not signed in: run `orkeon chat login work`.", work.Problem);
        Assert.Equal("Teams", work.Provider);
        Assert.Equal("Read, Send", work.Rights);
        Assert.False(work.IsApplication);
        Assert.False(work.IsDefault);
        Assert.False(work.IsSetAside);

        var perso = result.Accounts[1];
        Assert.True(perso.Ready);
        Assert.Null(perso.Problem);
        Assert.True(perso.IsApplication);
        Assert.True(perso.IsDefault);
        Assert.Same(perso, result.Find("PERSO"));
        Assert.Null(result.Find("other"));
    }

    [Fact]
    public async Task An_account_the_engine_set_aside_is_told_apart_from_one_that_is_not_ready()
    {
        var launcher = new FakeProcessLauncher().WithStandardOutput(
            """[{"name":"broken","provider":"?","default":false,"ready":false,"problem":"Provider is required: Teams or TeamsWebhook","unknown":42}]""");

        var result = await CreateClient(launcher).ListAsync(Settings, TestContext.Current.CancellationToken);

        var broken = Assert.Single(result.Accounts);
        Assert.True(broken.IsSetAside);
        Assert.Null(broken.Rights);
        Assert.False(broken.IsApplication);
    }

    [Fact]
    public async Task An_answer_that_is_no_json_array_is_a_typed_failure_with_its_reason()
    {
        var launcher = new FakeProcessLauncher().WithStandardOutput("perso (default)", "  ready");

        var result = await CreateClient(launcher).ListAsync(Settings, TestContext.Current.CancellationToken);

        Assert.Empty(result.Accounts);
        Assert.Equal(EmailCliFailureKind.Unreadable, result.Failure?.Kind);
        Assert.Contains("--json", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_and_an_empty_answer_are_typed_failures_too()
    {
        var malformed = await CreateClient(new FakeProcessLauncher().WithStandardOutput("[ {\"name\": ]"))
            .ListAsync(Settings, TestContext.Current.CancellationToken);
        var empty = await CreateClient(new FakeProcessLauncher()).ListAsync(Settings, TestContext.Current.CancellationToken);

        Assert.Equal(EmailCliFailureKind.Unreadable, malformed.Failure?.Kind);
        Assert.Equal(EmailCliFailureKind.Unreadable, empty.Failure?.Kind);
    }

    [Fact]
    public async Task A_non_zero_exit_code_is_a_failure_that_carries_what_the_verb_said_without_its_prefix()
    {
        var launcher = new FakeProcessLauncher { ExitCode = 1 }.WithStandardError(
            "Using settings: " + Settings,
            "orkeon chat accounts: the settings file is not valid JSON.");

        var result = await CreateClient(launcher).ListAsync(Settings, TestContext.Current.CancellationToken);

        Assert.Equal(EmailCliFailureKind.Stopped, result.Failure?.Kind);
        Assert.Equal("the settings file is not valid JSON.", result.Failure!.Reason);
        Assert.Equal(1, result.Failure.ExitCode);
    }

    [Fact]
    public async Task A_missing_binary_is_a_typed_failure_and_spawns_nothing()
    {
        var launcher = new FakeProcessLauncher();

        var result = await CreateClient(launcher, binaryPresent: false).ListAsync(Settings, TestContext.Current.CancellationToken);

        Assert.Equal(EmailCliFailureKind.EngineMissing, result.Failure?.Kind);
        Assert.Equal(0, launcher.StartCount);
    }

    // ── the connection check ──

    [Fact]
    public async Task A_completed_event_is_reachable_with_the_engines_summary_and_no_code()
    {
        const string Summary = "Chat account 'perso' is reachable: Read, Send ready.";
        var launcher = new FakeProcessLauncher()
            .WithStandardError("Using settings: " + Settings)
            .WithStandardOutput(
                $$"""{"v":1,"seq":1,"ts":"2026-10-17T10:00:00Z","kind":"chat.check.completed","account":"perso","ready":["Read"],"summary":"{{Summary}}"}""");

        var outcome = await CreateClient(launcher).CheckAsync("perso", Settings, TestContext.Current.CancellationToken);

        var request = Assert.Single(launcher.Requests);
        Assert.Equal(["chat", "check", "perso", "--events", "jsonl", "--settings", Settings], request.Arguments);
        Assert.Equal(EmailCheckKind.Reachable, outcome.Kind);
        Assert.Null(outcome.Code);
        Assert.Equal(Summary, outcome.Sentence);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task An_error_event_gives_the_refusal_its_code_and_its_message()
    {
        const string Message = "Chat account 'perso' needs an OAuth sign-in: run `orkeon chat login perso` in a terminal.";
        var launcher = new FakeProcessLauncher { ExitCode = 1 }.WithStandardOutput(ErrorLine(1, EmailCheckCodes.SignInRequired, Message));

        var outcome = await CreateClient(launcher).CheckAsync("perso", Settings, TestContext.Current.CancellationToken);

        Assert.Equal(EmailCheckKind.OperatorFixable, outcome.Kind);
        Assert.Equal(EmailCheckCodes.SignInRequired, outcome.Code);
        Assert.Equal(Message, outcome.Sentence);
    }

    [Fact]
    public async Task An_email_event_is_not_this_verbs_verdict()
    {
        // The vocabularies differ by their prefix alone: a chat check reads none of the e-mail one.
        var launcher = new FakeProcessLauncher().WithStandardOutput(
            """{"v":1,"seq":1,"kind":"email.check.completed","account":"perso","summary":"E-mail account 'perso' is reachable."}""",
            "Chat account 'perso' is reachable.");

        var outcome = await CreateClient(launcher).CheckAsync("perso", Settings, TestContext.Current.CancellationToken);

        Assert.Equal("Chat account 'perso' is reachable.", outcome.Sentence);
    }

    [Fact]
    public async Task Exit_code_two_is_the_server_or_the_network_and_the_message_loses_the_verbs_prefix()
    {
        var launcher = new FakeProcessLauncher { ExitCode = 2 }.WithStandardError(
            "Using settings: " + Settings,
            "orkeon chat check: Microsoft Graph refused the request: quota exceeded.");

        var outcome = await CreateClient(launcher).CheckAsync("perso", Settings, TestContext.Current.CancellationToken);

        Assert.Equal(EmailCheckKind.ServerOrNetwork, outcome.Kind);
        Assert.Equal("Microsoft Graph refused the request: quota exceeded.", outcome.Sentence);
    }

    [Fact]
    public async Task Without_a_binary_the_check_is_unavailable_and_spawns_nothing()
    {
        var launcher = new FakeProcessLauncher();

        var outcome = await CreateClient(launcher, binaryPresent: false).CheckAsync("perso", Settings, TestContext.Current.CancellationToken);

        Assert.Equal(EmailCheckKind.Unavailable, outcome.Kind);
        Assert.Null(outcome.ExitCode);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task Cancelling_a_check_stops_the_child_and_says_so_instead_of_throwing()
    {
        var launcher = new FakeProcessLauncher { RunsUntilCancelled = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var check = CreateClient(launcher).CheckAsync("perso", Settings, cts.Token);
        Assert.False(check.IsCompleted);
        await cts.CancelAsync();
        var outcome = await check;

        Assert.Equal(EmailCheckKind.Cancelled, outcome.Kind);
    }

    // ── the sign-in ──

    [Fact]
    public async Task Signing_in_reads_a_device_code_then_the_completion()
    {
        var launcher = new FakeProcessLauncher()
            .WithStandardError("Using settings: " + Settings)
            .WithStandardOutput(DeviceCodeLine, CompletedLine(2, "work"));
        var steps = new List<EmailLoginStep>();

        var ended = await CreateClient(launcher).LoginAsync("work", Settings, steps.Add, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(launcher.Requests);
        Assert.Equal(["chat", "login", "work", "--events", "jsonl", "--settings", Settings], request.Arguments);
        Assert.NotNull(request.OnInputReady);

        var code = Assert.Single(steps);
        Assert.Equal(EmailLoginStepKind.DeviceCode, code.Kind);
        Assert.Equal("https://microsoft.com/devicelogin", code.Address);
        Assert.Equal("WXYZ-1234", code.UserCode);
        Assert.Equal(TimeSpan.FromMinutes(15), code.ExpiresIn);
        Assert.Equal(EmailLoginStepKind.Completed, ended.Kind);
    }

    [Fact]
    public async Task A_teams_sign_in_shows_its_address_takes_the_pasted_one_and_says_why_one_is_rejected()
    {
        const string NoCode = "The pasted address carries no authorization code: it may be cut short.";
        var launcher = new ConversingProcessLauncher
        {
            Reply = line => line == Redirect ? [CompletedLine(3, "perso")] : [RejectedLine(2, NoCode)],
        };
        launcher.Opening.Add(AuthorizationLine);
        var steps = new List<EmailLoginStep>();
        var input = new EmailLoginInput();

        var login = CreateClient(launcher).LoginAsync("perso", Settings, steps.Add, input, TestContext.Current.CancellationToken);

        Assert.Equal(EmailLoginStepKind.AuthorizationUrl, Assert.Single(steps).Kind);
        Assert.Equal("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id=c&state=state-1", steps[0].Address);
        Assert.True(input.PasteRedirect("http://127.0.0.1:53124/?state=state-1"));

        // Said while the verb still waits: the rejection ends nothing, and its input stays open.
        Assert.Equal([EmailLoginStepKind.AuthorizationUrl, EmailLoginStepKind.RedirectRejected], steps.Select(step => step.Kind));
        Assert.Equal(NoCode, steps[1].Message);
        Assert.False(login.IsCompleted);
        Assert.Equal(0, launcher.ClosedInputs);

        Assert.True(input.PasteRedirect("  " + Redirect + "\r\n"));
        var ended = await login;

        Assert.Equal(EmailLoginStepKind.Completed, ended.Kind);
        Assert.Equal(["http://127.0.0.1:53124/?state=state-1", Redirect], launcher.InputLines);
        Assert.Equal(1, launcher.ClosedInputs);
    }

    [Fact]
    public async Task A_refusal_of_the_verb_ends_the_sign_in_with_its_code_and_its_message()
    {
        const string Message = "Chat account 'perso' has no client secret: the environment variable DRIVE_PERSO_CLIENT_SECRET is not set.";
        var launcher = new FakeProcessLauncher { ExitCode = 1 }.WithStandardOutput(ErrorLine(1, EmailCheckCodes.CredentialMissing, Message));

        var ended = await CreateClient(launcher).LoginAsync("perso", Settings, _ => { }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(EmailLoginStepKind.Failed, ended.Kind);
        Assert.Equal(EmailCliFailureKind.Refused, ended.Failure?.Kind);
        Assert.Equal(EmailCheckCodes.CredentialMissing, ended.Failure!.Code);
        Assert.Equal(Message, ended.Failure.Reason);
        Assert.Equal(1, ended.Failure.ExitCode);
    }

    [Fact]
    public async Task A_verb_that_ended_well_without_saying_it_completed_names_the_chat_event_it_missed()
    {
        var launcher = new FakeProcessLauncher().WithStandardOutput(CompletedLine(1, "perso").Replace("chat.", "email.", StringComparison.Ordinal));

        var ended = await CreateClient(launcher).LoginAsync("perso", Settings, _ => { }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(EmailCliFailureKind.Unreadable, ended.Failure?.Kind);
        Assert.Contains(ChatEventKinds.LoginCompleted, ended.Failure!.Reason, StringComparison.Ordinal);
        Assert.Contains("orkeon chat login", ended.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_sign_in_closes_the_verbs_input_at_once_and_says_so_instead_of_throwing()
    {
        var launcher = new FakeProcessLauncher { RunsUntilCancelled = true }.WithStandardOutput(AuthorizationLine);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var login = CreateClient(launcher).LoginAsync("perso", Settings, _ => { }, cancellationToken: cts.Token);
        Assert.False(launcher.InputClosed);

#pragma warning disable CA1849, S6966 // the synchronous call is the behaviour under test
        cts.Cancel();
#pragma warning restore CA1849, S6966
        Assert.True(launcher.InputClosed);

        var ended = await login;
        Assert.Equal(EmailCliFailureKind.Cancelled, ended.Failure?.Kind);
    }

    /// <summary>
    /// Every kind the chat vocabulary declares is one the client reads: a kind added to
    /// <see cref="ChatEventKinds"/> without its reading here fails this test.
    /// </summary>
    [Fact]
    public void Every_kind_of_the_chat_vocabulary_is_read_by_the_client()
    {
        var declared = typeof(ChatEventKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                ChatEventKinds.CheckCompleted, ChatEventKinds.Error, ChatEventKinds.LoginAuthorizationUrl,
                ChatEventKinds.LoginCompleted, ChatEventKinds.LoginDeviceCode, ChatEventKinds.LoginRedirectRejected,
            }.Order(StringComparer.Ordinal),
            declared);
    }

    // ── the sign-out ──

    [Fact]
    public async Task Signing_out_names_the_account_and_the_settings_file_and_a_refusal_carries_what_the_verb_said()
    {
        var launcher = new FakeProcessLauncher();
        var refusing = new FakeProcessLauncher { ExitCode = 1 }.WithStandardError(
            "orkeon chat logout: Unknown chat account 'other'. Configured: perso.");

        var failure = await CreateClient(launcher).LogoutAsync("perso", Settings, TestContext.Current.CancellationToken);
        var refused = await CreateClient(refusing).LogoutAsync("other", Settings, TestContext.Current.CancellationToken);

        Assert.Equal(["chat", "logout", "perso", "--settings", Settings], Assert.Single(launcher.Requests).Arguments);
        Assert.Null(failure);
        Assert.Equal(EmailCliFailureKind.Stopped, refused?.Kind);
        Assert.Equal("Unknown chat account 'other'. Configured: perso.", refused!.Reason);
    }
}
