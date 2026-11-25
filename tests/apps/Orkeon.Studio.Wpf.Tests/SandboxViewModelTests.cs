using System.Collections.Immutable;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Decision;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;
using Orkeon.Studio.Wpf.ViewModels.Services;
using Orkeon.Studio.Wpf.ViewModels.Shell;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>The « Test » settings tab: every model and the decision provider, a free message, the log.</summary>
public sealed class SandboxViewModelTests
{
    private const string KeyVariable = "SANDBOX_TEST_KEY";
    private const string Secret = "sk-sandbox-secret-0123456789";

    private sealed class World
    {
        public FakeApiKeyStore Keys { get; } = new();
        public FakeLlmEndpointProbe Probe { get; } = new();
        public FakeDecisionProbe Decision { get; } = new();
        public InMemoryClipboardService Clipboard { get; } = new();
        public FakePathPicker Picker { get; } = new();
        public List<(string Path, string Text)> Written { get; } = [];
        public Exception? WriteFailure { get; set; }
        public ConfigTabViewModel Config { get; }
        public ModelProfilesViewModel Profiles { get; }
        public SettingsScreenViewModel Screen { get; }
        public SandboxViewModel Sandbox => Screen.Sandbox;

        public World(string document = "{}", params ModelProfile[] profiles)
        {
            Config = new ConfigTabViewModel(new StudioServices
            {
                SettingsStore = new FakeAppSettingsStore(),
                Directories = new FakeDirectoryProbe(),
                KeyStore = Keys,
            });
            Config.SetDocument(AppSettingsDocument.Parse(document));
            var store = new InMemoryModelProfileStore();
            Profiles = new ModelProfilesViewModel(store, Config.Llm, EnglishStudioStrings.Instance, Probe, Keys);
            foreach (var profile in profiles)
                Profiles.CommitEdit(profile, null);
            Screen = new SettingsScreenViewModel(Config, Profiles, new UiModeViewModel(UiModeViewModel.Novice), tests: new ModelTestSeams
            {
                Probe = Probe,
                DecisionProbe = Decision,
                KeyStore = Keys,
                Clipboard = Clipboard,
                Picker = Picker,
                SaveText = (path, text) =>
                {
                    if (WriteFailure is { } failure)
                        throw failure;
                    Written.Add((path, text));
                    return Task.CompletedTask;
                },
            });
        }
    }

    private static ModelProfile Local(string name, string model = "m1") => new()
    {
        Name = name,
        Provider = LlmPresets.Ollama,
        Model = model,
        BaseUrl = "http://localhost:11434",
    };

    private static ModelProfile Cloud(string name) => new()
    {
        Name = name,
        Provider = LlmPresets.Custom,
        Model = "m2",
        BaseUrl = "https://llm.example/v1",
        KeyEnvName = KeyVariable,
    };

    private const string WithDecision = """{"Orkeon":{"TypeSafe":{}}}""";

    [Fact]
    public async Task Test_all_asks_each_model_and_the_decision_provider_and_says_the_result()
    {
        var world = new World(WithDecision, Local("one"), Local("two", "m2"));

        await world.Sandbox.Tests.TestAllCommand.ExecuteAsync();

        Assert.Equal(["m1", "m2"], world.Probe.Requests.Select(r => r.Model));
        Assert.Single(world.Decision.Requests);
        Assert.All(world.Sandbox.Tests.Rows, row => Assert.Equal(ModelTestState.Passed, row.State));
        Assert.Equal(3, world.Sandbox.Tests.Rows.Count);
        Assert.Equal("3 passed, 0 failed, 0 not sent", world.Sandbox.Tests.Summary);
        Assert.Equal("Verified", world.Screen.SandboxTabState);
    }

    [Fact]
    public async Task A_rebuilt_list_keeps_the_decision_verdict_until_its_key_changes()
    {
        var world = new World(WithDecision, Local("one"));
        await world.Sandbox.Tests.TestAllCommand.ExecuteAsync();

        world.Profiles.CommitEdit(Local("two"), null);
        Assert.Equal(ModelTestState.Passed, world.Sandbox.Tests.Rows.Single(row => row.IsDecision).State);

        world.Keys.Stage(world.Decision.Requests[0].KeyVariable!, "ts-changed-0123456789");
        world.Screen.ShowSandboxCommand.Execute(null);
        Assert.Equal(ModelTestState.Idle, world.Sandbox.Tests.Rows.Single(row => row.IsDecision).State);
    }

    [Fact]
    public void A_key_named_or_held_by_the_settings_file_is_masked()
    {
        var world = new World("""{"Orkeon":{"TypeSafe":{"ApiKeyEnvVar":"MY_TS"},"Other":{"ApiKey":"held-in-file-0123"}}}""");
        world.Keys.Stage("MY_TS", Secret);

        var text = world.Profiles.MaskKeys($"a {Secret} b held-in-file-0123");

        Assert.Equal("a *** b ***", text);
    }

    [Fact]
    public void Nothing_is_sent_before_a_click()
    {
        var world = new World(WithDecision, Local("one"));

        world.Screen.ShowSandboxCommand.Execute(null);

        Assert.True(world.Screen.IsSandboxTab);
        Assert.Empty(world.Probe.Requests);
        Assert.Empty(world.Decision.Requests);
    }

    [Fact]
    public async Task A_missing_key_or_an_ollama_card_elsewhere_is_explained_without_a_request()
    {
        var world = new World("{}", Cloud("cloud"), Local("lost") with { BaseUrl = "https://api.openai.com/v1" });

        await world.Sandbox.Tests.TestAllCommand.ExecuteAsync();

        Assert.Empty(world.Probe.Requests);
        Assert.All(world.Sandbox.Tests.Rows, row => Assert.Equal(ModelTestState.Skipped, row.State));
        Assert.Contains(KeyVariable, world.Sandbox.Tests.Rows[0].Message, StringComparison.Ordinal);
        Assert.Contains("openai", world.Sandbox.Tests.Rows[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_decision_provider_only_the_models_are_tested_and_the_tab_says_so()
    {
        var world = new World("{}", Local("one"));

        await world.Sandbox.Tests.TestAllCommand.ExecuteAsync();

        Assert.Single(world.Sandbox.Tests.Rows);
        Assert.Empty(world.Decision.Requests);
        Assert.Equal(EnglishStudioStrings.Instance[StudioStringKeys.SandboxNoDecision], world.Sandbox.DecisionNote);
    }

    [Fact]
    public async Task A_stop_leaves_the_rest_untested_and_leaving_the_tab_stops()
    {
        var world = new World("{}", Local("one"), Local("two"));
        world.Probe.Gate = new TaskCompletionSource();
        world.Screen.ShowSandboxCommand.Execute(null);

        var running = world.Sandbox.Tests.TestAllCommand.ExecuteAsync();
        Assert.Equal(ModelTestState.Running, world.Sandbox.Tests.Rows[0].State);
        Assert.Equal(ModelTestState.Queued, world.Sandbox.Tests.Rows[1].State);
        Assert.False(world.Sandbox.SendCommand.CanExecute(null));
        world.Screen.ShowModelCommand.Execute(null);
        await running;

        Assert.Single(world.Probe.Requests);
        Assert.All(world.Sandbox.Tests.Rows, row => Assert.Equal(ModelTestState.Stopped, row.State));
        Assert.True(world.Sandbox.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_wrong_decision_answer_is_a_failure_that_names_it()
    {
        var world = new World(WithDecision);
        world.Decision.Answer = request => new DecisionProbeResult
        {
            Provider = request.Provider,
            Checks = [new DecisionProbeCheck("yes_no", false, "no (0.71)")],
        };

        await world.Sandbox.Tests.TestAllCommand.ExecuteAsync();

        var row = Assert.Single(world.Sandbox.Tests.Rows);
        Assert.Equal(ModelTestState.Failed, row.State);
        Assert.Contains("no (0.71)", row.Message, StringComparison.Ordinal);
        Assert.Contains("WRONG", world.Sandbox.Tests.JournalText, StringComparison.Ordinal);
        Assert.Equal("Warning", world.Screen.SandboxTabState);
    }

    [Fact]
    public async Task A_message_shows_the_whole_reply_its_time_and_its_tokens()
    {
        var world = new World("{}", Local("one"));
        world.Probe.Result = LlmProbeResult.Reachable(ImmutableArray<string>.Empty) with
        {
            ReplyText = "Hello there.",
            Usage = new LlmProbeUsage(7, 3),
            Elapsed = TimeSpan.FromSeconds(2),
        };

        world.Sandbox.Sample = SandboxViewModel.Samples[1];
        await world.Sandbox.SendCommand.ExecuteAsync();

        Assert.Equal(SandboxViewModel.Samples[1].Text, world.Probe.LastRequest.Prompt);
        Assert.Equal("Hello there.", world.Sandbox.Reply);
        Assert.Contains("7 in, 3 out", world.Sandbox.ReplyNote, StringComparison.Ordinal);
        Assert.False(world.Sandbox.HasReplyFailed);
        Assert.Contains("Hello there.", world.Sandbox.Tests.JournalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_reply_is_said_and_not_a_silent_success()
    {
        var world = new World("{}", Local("one"));
        world.Probe.Result = LlmProbeResult.Reachable(ImmutableArray<string>.Empty) with { ReplyText = "" };

        await world.Sandbox.SendCommand.ExecuteAsync();

        Assert.True(world.Sandbox.HasReplyFailed);
        Assert.Equal(EnglishStudioStrings.Instance[StudioStringKeys.SandboxEmptyReply], world.Sandbox.ReplyNote);
    }

    [Fact]
    public async Task A_decision_case_of_the_users_own_is_asked()
    {
        var world = new World(WithDecision);

        Assert.True(world.Sandbox.IsDecisionTarget);
        world.Sandbox.CaseState = "The parcel is late.";
        world.Sandbox.CaseQuestion = "";
        world.Sandbox.CaseOptions = "refund\r\nwait\n";
        await world.Sandbox.SendCommand.ExecuteAsync();

        var asked = Assert.Single(world.Decision.Requests).Case;
        Assert.Equal("The parcel is late.", asked?.State);
        Assert.Equal(["refund", "wait"], asked?.Options);
        Assert.Contains("yes_no", world.Sandbox.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_is_copied_saved_and_cleared_without_a_key()
    {
        var world = new World("{}", Cloud("cloud"));
        world.Keys.Stage(KeyVariable, Secret);
        world.Probe.FailWith = new InvalidOperationException($"refused {Secret}");
        world.Picker.SaveFileToReturn = "/tmp/log.txt";

        await world.Sandbox.Tests.TestAllCommand.ExecuteAsync();
        world.Sandbox.Tests.CopyJournalCommand.Execute(null);
        await world.Sandbox.Tests.SaveJournalCommand.ExecuteAsync();

        Assert.True(world.Sandbox.Tests.IsJournalCopied);
        Assert.Contains("FAILED", world.Clipboard.LastText, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, world.Clipboard.LastText, StringComparison.Ordinal);
        Assert.Equal(world.Sandbox.Tests.JournalText, Assert.Single(world.Written).Text);

        world.WriteFailure = new IOException("disk full");
        await world.Sandbox.Tests.SaveJournalCommand.ExecuteAsync();
        Assert.Contains("disk full", world.Sandbox.Tests.SaveError, StringComparison.Ordinal);

        world.Sandbox.Tests.ClearJournalCommand.Execute(null);
        Assert.False(world.Sandbox.Tests.HasJournal);
        Assert.False(world.Sandbox.Tests.IsJournalCopied);
    }

    [Fact]
    public async Task A_changed_setting_loses_its_verdict_and_the_others_keep_theirs()
    {
        var world = new World("{}", Local("one"), Local("two"));
        await world.Sandbox.Tests.TestAllCommand.ExecuteAsync();

        world.Profiles.CommitEdit(Local("two", "another"), "two");

        Assert.Equal(ModelTestState.Passed, world.Sandbox.Tests.Rows.Single(r => r.Name == "one").State);
        Assert.Equal(ModelTestState.Idle, world.Sandbox.Tests.Rows.Single(r => r.Name == "two").State);
    }

    [Fact]
    public void A_row_opens_its_setting_and_the_decision_row_its_tab()
    {
        var world = new World(WithDecision, Local("one"));

        world.Sandbox.Tests.Rows[0].OpenCommand.Execute(null);
        Assert.True(world.Profiles.IsEditorOpen);

        world.Sandbox.Tests.Rows[1].OpenCommand.Execute(null);
        Assert.True(world.Screen.IsDecisionTab);
    }
}
