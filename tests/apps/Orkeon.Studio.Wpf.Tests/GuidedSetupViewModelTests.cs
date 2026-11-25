using System.Collections.Immutable;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;
using Orkeon.Studio.Wpf.ViewModels.Services;
using Orkeon.Studio.Wpf.ViewModels.Shell;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>The guided setup: a provider, its models and the decision provider in one pass, written at the end.</summary>
public sealed class GuidedSetupViewModelTests
{
    private const string Secret = "sk-guided-secret-0123456789";
    private const string DecisionSecret = "ts-guided-secret-0123456789";

    private sealed class World
    {
        public FakeAppSettingsStore Settings { get; } = new();
        public FakeApiKeyStore Keys { get; } = new();
        public FakeLlmEndpointProbe Probe { get; } = new();
        public FakeDecisionProbe Decision { get; } = new();
        public ConfigTabViewModel Config { get; }
        public ModelProfilesViewModel Profiles { get; }
        public SettingsScreenViewModel Screen { get; }
        public GuidedSetupViewModel Guided => Screen.Guided;

        public World(string document = "{}", string mode = UiModeViewModel.Novice)
        {
            Config = new ConfigTabViewModel(new StudioServices
            {
                SettingsStore = Settings,
                Directories = new FakeDirectoryProbe(),
                KeyStore = Keys,
            });
            Config.SetDocument(AppSettingsDocument.Parse(document));
            Profiles = new ModelProfilesViewModel(
                new InMemoryModelProfileStore(), Config.Llm, EnglishStudioStrings.Instance, Probe, Keys);
            Screen = new SettingsScreenViewModel(Config, Profiles, new UiModeViewModel(mode), tests: new ModelTestSeams
            {
                Probe = Probe,
                DecisionProbe = Decision,
                KeyStore = Keys,
            });
        }

        public void Enter()
        {
            Guided.StartCommand.Execute(null);
            Guided.Provider = Guided.CloudProviders.First(p => p.Name == LlmPresets.DeepSeek);
            Guided.ApiKeyInput = Secret;
            Guided.NextCommand.Execute(null);
            Guided.Models.Clear();
            Guided.PickModelCommand.Execute("small");
            Guided.PickModelCommand.Execute("big");
            Guided.NextCommand.Execute(null);
            Guided.SelectedDecision = Guided.DecisionChoices[1];
            Guided.DecisionKeyInput = DecisionSecret;
            Guided.NextCommand.Execute(null);
        }
    }

    [Fact]
    public async Task Two_models_and_the_decision_provider_are_written_in_one_pass()
    {
        var world = new World();
        world.Enter();
        world.Guided.Assistant = world.Guided.ElectionChoices[1];

        Assert.True(world.Guided.IsTestStep);
        await world.Guided.FinishCommand.ExecuteAsync();

        Assert.False(world.Guided.IsOpen);
        var connection = Assert.Single(world.Profiles.Set.Connections);
        Assert.Equal(["small", "big"], connection.Models!.Select(m => m.Model));
        Assert.Equal(connection.PairNames[1], world.Profiles.StudioProfileName);
        Assert.Equal(connection.PairNames[0], world.Profiles.DefaultProfileName);
        Assert.Contains(Secret, world.Keys.Saved.Values);
        Assert.Contains(DecisionSecret, world.Keys.Saved.Values);
        Assert.Equal(2, world.Config.Decision.DocumentForCards.Decision.RouteLadder.Count);
        Assert.Empty(world.Guided.ApiKeyInput);
        Assert.DoesNotContain(Secret, world.Config.RawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(DecisionSecret, world.Config.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_writes_nothing_and_forgets_the_keys()
    {
        var world = new World();
        var before = world.Config.RawJson;
        world.Enter();

        world.Guided.CancelCommand.Execute(null);

        Assert.False(world.Guided.IsOpen);
        Assert.Empty(world.Profiles.Set.Connections);
        Assert.Empty(world.Keys.Saved);
        Assert.Empty(world.Settings.SavedPaths);
        Assert.Equal(before, world.Config.RawJson);
        Assert.Empty(world.Guided.ApiKeyInput);
        Assert.Empty(world.Guided.DecisionKeyInput);
        world.Guided.StartCommand.Execute(null);
        Assert.Null(world.Guided.Provider);
        Assert.Empty(world.Guided.Models);
    }

    [Fact]
    public async Task The_last_step_tests_what_was_typed_and_a_failure_still_lets_finish()
    {
        var world = new World();
        world.Enter();
        Assert.Empty(world.Probe.Requests);
        world.Probe.FailWith = new InvalidOperationException($"refused {Secret}");

        await world.Guided.Tests.TestAllCommand.ExecuteAsync();

        Assert.Equal(["small", "big"], world.Probe.Requests.Select(r => r.Model));
        Assert.All(world.Probe.Requests, r => Assert.Equal(Secret, r.ApiKey));
        Assert.Equal(DecisionSecret, Assert.Single(world.Decision.Requests).ApiKey);
        Assert.Equal(ModelTestState.Failed, world.Guided.Tests.Rows[0].State);
        Assert.DoesNotContain(Secret, world.Guided.Tests.JournalText, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, world.Guided.Tests.Rows[0].Message, StringComparison.Ordinal);
        world.Guided.ApiKeyInput = "replaced";
        Assert.DoesNotContain(Secret, world.Guided.Tests.JournalText, StringComparison.Ordinal);
        Assert.Empty(world.Keys.Saved);
        Assert.True(world.Guided.FinishCommand.CanExecute(null));
    }

    [Fact]
    public async Task Verify_lists_the_models_with_the_typed_key_and_needs_one()
    {
        var world = new World();
        world.Guided.StartCommand.Execute(null);
        world.Guided.Provider = world.Guided.CloudProviders.First(p => p.Name == LlmPresets.DeepSeek);

        await world.Guided.VerifyCommand.ExecuteAsync();
        Assert.Empty(world.Probe.Requests);

        world.Guided.ApiKeyInput = Secret;
        world.Probe.Result = LlmProbeResult.Reachable(ImmutableArray.Create("small", "big"));
        await world.Guided.VerifyCommand.ExecuteAsync();

        Assert.Equal(Secret, world.Probe.LastRequest.ApiKey);
        Assert.Equal(["big", "small"], world.Guided.FoundModels);
        world.Guided.ApiKeyInput = "another";
        Assert.Empty(world.Guided.FoundModels);
    }

    [Fact]
    public async Task A_model_list_answering_an_old_key_is_dropped()
    {
        var world = new World();
        world.Guided.StartCommand.Execute(null);
        world.Guided.Provider = world.Guided.CloudProviders.First(p => p.Name == LlmPresets.DeepSeek);
        world.Guided.ApiKeyInput = Secret;
        world.Probe.Gate = new TaskCompletionSource();
        var verifying = world.Guided.VerifyCommand.ExecuteAsync();

        world.Guided.ApiKeyInput = "another";
        await verifying;

        Assert.Empty(world.Guided.FoundModels);
        Assert.Null(world.Guided.VerifyText);
    }

    [Fact]
    public void The_next_step_waits_for_what_is_missing()
    {
        var world = new World();
        world.Guided.StartCommand.Execute(null);
        Assert.False(world.Guided.NextCommand.CanExecute(null));
        world.Guided.Provider = world.Guided.OtherProviders.First();
        Assert.False(world.Guided.NextCommand.CanExecute(null));
        world.Guided.BaseUrl = "https://llm.example/v1";
        world.Guided.NextCommand.Execute(null);
        world.Guided.Models.Clear();
        world.Guided.AddModelCommand.Execute(null);
        Assert.False(world.Guided.NextCommand.CanExecute(null));
        world.Guided.Models[0].Model = "vendor/one";
        Assert.True(world.Guided.NextCommand.CanExecute(null), world.Guided.Problem);
        world.Guided.AddModelCommand.Execute(null);
        world.Guided.Models[1].Model = "other/one";
        Assert.False(world.Guided.NextCommand.CanExecute(null));
        world.Guided.Models[1].Alias = "two";
        Assert.True(world.Guided.NextCommand.CanExecute(null), world.Guided.Problem);
    }

    [Fact]
    public async Task Later_leaves_the_decision_settings_and_a_second_pass_keeps_the_elections()
    {
        var world = new World();
        world.Enter();
        await world.Guided.FinishCommand.ExecuteAsync();
        var (assistant, elected) = (world.Profiles.StudioProfileName, world.Profiles.DefaultProfileName);
        var ladder = world.Config.Decision.RouteLadderText;

        world.Guided.StartCommand.Execute(null);
        world.Guided.Provider = world.Guided.CloudProviders.First(p => p.Name == LlmPresets.DeepSeek);
        world.Guided.NextCommand.Execute(null);
        Assert.Null(world.Guided.Problem);
        world.Guided.NextCommand.Execute(null);
        Assert.False(world.Guided.OffersLadder);
        world.Guided.NextCommand.Execute(null);
        await world.Guided.FinishCommand.ExecuteAsync();

        Assert.Equal(2, world.Profiles.Set.Connections.Count);
        Assert.Equal(assistant, world.Profiles.StudioProfileName);
        Assert.Equal(elected, world.Profiles.DefaultProfileName);
        Assert.Equal(ladder, world.Config.Decision.RouteLadderText);
    }

    [Fact]
    public async Task The_ladder_order_is_the_one_chosen()
    {
        var world = new World();
        world.Enter();
        world.Guided.Rungs[1].MoveUpCommand.Execute(null);
        var first = world.Guided.Rungs[0].Name;

        await world.Guided.FinishCommand.ExecuteAsync();

        Assert.Equal(world.Profiles.OfferedHostId(first), world.Config.Decision.DocumentForCards.Decision.RouteLadder[0]);
    }

    [Fact]
    public async Task A_key_the_machine_refuses_is_said_and_the_models_stay()
    {
        var world = new World();
        world.Keys.PersistFailure = new InvalidOperationException("no registry");
        world.Enter();

        await world.Guided.FinishCommand.ExecuteAsync();

        Assert.Single(world.Profiles.Set.Connections);
        Assert.Contains("no registry", world.Profiles.KeyStoreError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_left_for_the_next_step_still_brings_its_models()
    {
        var world = new World();
        world.Guided.StartCommand.Execute(null);
        world.Guided.Provider = world.Guided.CloudProviders.First(p => p.Name == LlmPresets.DeepSeek);
        world.Guided.ApiKeyInput = Secret;
        world.Probe.Gate = new TaskCompletionSource();
        var verifying = world.Guided.VerifyCommand.ExecuteAsync();

        world.Guided.NextCommand.Execute(null);
        world.Probe.Gate.SetResult();
        await verifying;

        Assert.True(world.Guided.IsModelsStep);
        Assert.True(world.Guided.HasFoundModels);
    }

    [Fact]
    public async Task A_settings_file_that_is_not_written_is_said_and_finish_is_not_run_twice()
    {
        var world = new World();
        world.Settings.SaveFault = new IOException("locked file");
        world.Enter();

        await world.Guided.FinishCommand.ExecuteAsync();

        Assert.True(world.Guided.IsOpen);
        Assert.Contains("locked file", world.Guided.FinishFailure, StringComparison.Ordinal);
        Assert.False(world.Guided.FinishCommand.CanExecute(null));
        Assert.False(world.Guided.BackCommand.CanExecute(null));
        Assert.Single(world.Profiles.Set.Connections);
        world.Guided.CancelCommand.Execute(null);
        Assert.False(world.Guided.IsOpen);
        world.Guided.StartCommand.Execute(null);
        Assert.Null(world.Guided.FinishFailure);
    }

    [Fact]
    public async Task A_write_that_throws_is_said_too()
    {
        var world = new World();
        world.Settings.SaveFault = new InvalidOperationException("no disk");
        world.Enter();

        await world.Guided.FinishCommand.ExecuteAsync();

        Assert.Contains("no disk", world.Guided.FinishFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pick_follows_the_model_through_its_alias()
    {
        var world = new World();
        world.Enter();
        world.Guided.Assistant = world.Guided.ElectionChoices[1];

        world.Guided.Models[1].Alias = "renamed";

        Assert.EndsWith("renamed", world.Guided.Assistant, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anthropic_workspace_is_asked_sent_and_kept()
    {
        var world = new World();
        world.Guided.StartCommand.Execute(null);
        world.Guided.Provider = world.Guided.CloudProviders.First(p => p.Name == LlmPresets.Anthropic);
        Assert.True(world.Guided.ShowWorkspace);
        world.Guided.ApiKeyInput = Secret;
        world.Guided.WorkspaceId = "wrk-1";

        await world.Guided.VerifyCommand.ExecuteAsync();
        for (var step = 1; step < 4; step++)
            world.Guided.NextCommand.Execute(null);
        await world.Guided.FinishCommand.ExecuteAsync();

        Assert.Equal("wrk-1", world.Probe.Requests[0].WorkspaceId);
        Assert.Equal("wrk-1", Assert.Single(world.Profiles.Set.Connections).WorkspaceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_expert_s_unsaved_edits_are_not_saved_by_finish(bool hasEdits)
    {
        var world = new World(mode: UiModeViewModel.Expert);
        if (hasEdits)
            world.Config.Decision.Enabled = !world.Config.Decision.Enabled;
        Assert.Equal(hasEdits, world.Config.HasPendingChanges);
        world.Enter();

        await world.Guided.FinishCommand.ExecuteAsync();

        Assert.Equal(hasEdits, world.Config.HasPendingChanges);
        Assert.Equal(hasEdits ? 0 : 1, world.Settings.SavedPaths.Count);
    }
}
