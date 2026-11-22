using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// One connection, several models, on the model-settings screen: the editor holds a row per model,
/// each one a setting named connection.alias and mirrored as the host profile of its slug; the
/// cards group under their connection; a setting teams name is removed only once confirmed.
/// </summary>
public sealed class ConnectionEditorTests
{
    private static (ModelProfilesViewModel Profiles, AppSettingsDocument Document) Build(IReadOnlyList<TeamSummary>? teams = null)
    {
        var document = AppSettingsDocument.Parse("{}");
        var llm = new LlmSectionViewModel(() => document, () => { });
        var profiles = new ModelProfilesViewModel(
            new InMemoryModelProfileStore(), llm, probe: new FakeLlmEndpointProbe(), keyStore: new FakeApiKeyStore(),
            links: teams is null ? null : new ModelProfilesLinks { LoadTeams = () => teams });
        return (profiles, document);
    }

    private static ModelProfileEditorViewModel OpenDeepSeek(ModelProfilesViewModel profiles, string name = "mydeepseek")
    {
        profiles.NewProfileCommand.Execute(null);
        var editor = profiles.Editor!;
        editor.SelectedProvider = editor.Providers.First(p => p.Name == LlmPresets.DeepSeek);
        editor.Name = name;
        editor.ApiKeyInput = "sk-ds";
        return editor;
    }

    private static void SaveTwoModels(ModelProfilesViewModel profiles)
    {
        var editor = OpenDeepSeek(profiles);
        editor.Models[0].Alias = "flash";
        editor.Models[0].Model = "deepseek-v4-flash";
        editor.AddModelCommand.Execute(null);
        editor.Models[1].Alias = "pro";
        editor.Models[1].Model = "deepseek-v4-pro";
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.SaveCommand.Execute(null);
    }

    [Fact]
    public void A_connection_of_two_models_is_two_settings_and_two_host_profiles_under_one_card()
    {
        var (profiles, document) = Build();

        SaveTwoModels(profiles);

        Assert.Equal(["mydeepseek.flash", "mydeepseek.pro"], profiles.Set.Profiles.Select(p => p.Name));
        Assert.Equal(["mydeepseek-flash", "mydeepseek-pro"], document.Llm.Profiles.Ids);
        Assert.Equal("deepseek-v4-pro", document.Llm.Profiles.Get("mydeepseek-pro")?.Model);
        Assert.Equal("DEEPSEEK_API_KEY", document.Llm.Profiles.Get("mydeepseek-pro")?.ApiKeyEnvVar);
        var connection = Assert.Single(profiles.Connections);
        Assert.True(connection.HasSeveralModels);
        Assert.Equal(2, connection.Models.Count);
        Assert.Equal(2, profiles.Profiles.Count);
    }

    [Fact]
    public void The_second_model_gives_the_first_an_alias_and_needs_one_of_its_own()
    {
        var (profiles, _) = Build();
        var editor = OpenDeepSeek(profiles);
        editor.Model = "deepseek/deepseek-v4-flash";
        Assert.True(editor.Models[0].RemoveCommand.CanExecute(null) is false);

        editor.AddModelCommand.Execute(null);

        Assert.Equal("deepseek-v4-flash", editor.Models[0].Alias);
        Assert.Same(editor.Models[1], editor.SelectedModelRow);
        Assert.True(editor.Models[1].AliasMissing);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Models[1].Alias = "DeepSeek-V4-Flash";
        Assert.True(editor.Models[1].AliasTaken);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Models[1].Alias = "pro";
        Assert.Equal("mydeepseek.pro", editor.Models[1].PairName);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Picking_a_found_model_fills_the_selected_row_and_suggests_its_alias()
    {
        var (profiles, _) = Build();
        var editor = OpenDeepSeek(profiles);
        editor.AddModelCommand.Execute(null);

        editor.PickModelCommand.Execute("deepseek/deepseek-v4-pro");

        Assert.Equal("deepseek/deepseek-v4-pro", editor.Models[1].Model);
        Assert.Equal("deepseek-v4-pro", editor.Models[1].Alias);
        Assert.Equal("deepseek/deepseek-v4-pro", editor.Model);
    }

    [Fact]
    public void A_pair_named_like_another_setting_refuses_the_save()
    {
        var (profiles, _) = Build();
        var first = OpenDeepSeek(profiles, "mydeepseek.pro");
        first.SaveCommand.Execute(null);

        var editor = OpenDeepSeek(profiles);
        editor.Models[0].Alias = "flash";
        editor.AddModelCommand.Execute(null);
        editor.Models[1].Alias = "pro";

        Assert.True(editor.Models[1].NameCollision);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Renaming_the_connection_moves_every_host_profile_and_keeps_the_aliases()
    {
        var (profiles, document) = Build();
        SaveTwoModels(profiles);

        profiles.BeginEdit(profiles.Set.Find("mydeepseek.pro")!);
        var editor = profiles.Editor!;
        Assert.Equal("mydeepseek", editor.Name);
        Assert.Same(editor.Models[1], editor.SelectedModelRow);
        editor.Name = "ds";
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.SaveCommand.Execute(null);

        Assert.Equal(["ds.flash", "ds.pro"], profiles.Set.Profiles.Select(p => p.Name));
        Assert.Equal(["ds-flash", "ds-pro"], document.Llm.Profiles.Ids);
    }

    [Fact]
    public void Removing_a_model_in_the_editor_removes_its_setting_and_its_host_profile()
    {
        var (profiles, document) = Build();
        SaveTwoModels(profiles);

        profiles.BeginEdit(profiles.Set.Find("mydeepseek.flash")!);
        var editor = profiles.Editor!;
        editor.Models[1].RemoveCommand.Execute(null);
        Assert.Single(editor.Models);
        Assert.False(editor.Models[0].RemoveCommand.CanExecute(null));
        editor.SaveCommand.Execute(null);

        Assert.Equal(["mydeepseek.flash"], profiles.Set.Profiles.Select(p => p.Name));
        Assert.Equal(["mydeepseek-flash"], document.Llm.Profiles.Ids);
    }

    [Fact]
    public void A_model_pins_its_own_settings_over_the_connection()
    {
        var (profiles, document) = Build();
        var editor = OpenDeepSeek(profiles);
        editor.TimeoutText = "600";
        editor.Models[0].Alias = "flash";
        editor.AddModelCommand.Execute(null);
        editor.Models[1].Alias = "pro";
        editor.Models[1].TimeoutText = "900";
        editor.Models[1].Thinking = editor.ModelThinkingChoices.First(c => c.Value == false);
        editor.SaveCommand.Execute(null);

        Assert.Equal(600, profiles.Set.Find("mydeepseek.flash")?.TimeoutSeconds);
        Assert.Equal(900, profiles.Set.Find("mydeepseek.pro")?.TimeoutSeconds);
        Assert.False(profiles.Set.Find("mydeepseek.pro")?.ThinkingEnabled);
        Assert.Equal(900, document.Llm.Profiles.Get("mydeepseek-pro")?.TimeoutSeconds);

        // Reopened, the row shows what it pins and nothing of the connection's.
        profiles.BeginEdit(profiles.Set.Find("mydeepseek.pro")!);
        Assert.Equal("900", profiles.Editor!.Models[1].TimeoutText);
        Assert.Equal("", profiles.Editor.Models[0].TimeoutText);
        Assert.Equal("600", profiles.Editor.TimeoutText);
    }

    [Fact]
    public void Deleting_a_setting_teams_name_asks_first_and_names_them()
    {
        var teams = new List<TeamSummary>
        {
            new() { Name = "Veille", Slug = "veille", Path = "/teams/veille", Metadata = new StudioTeamMetadata { Profile = "mydeepseek.pro" } },
        };
        var (profiles, _) = Build(teams);
        SaveTwoModels(profiles);
        var card = profiles.Profiles.Single(p => p.Profile.Name == "mydeepseek.pro");

        card.DeleteCommand.Execute(null);

        Assert.True(card.IsConfirmingDelete);
        Assert.Contains("Veille", card.DeleteWarning, StringComparison.Ordinal);
        Assert.NotNull(profiles.Set.Find("mydeepseek.pro"));

        card.ConfirmDeleteCommand.Execute(null);
        Assert.Null(profiles.Set.Find("mydeepseek.pro"));
        Assert.NotNull(profiles.Set.Find("mydeepseek.flash"));
    }

    [Fact]
    public void Renaming_the_connection_has_the_teams_follow_each_of_its_settings()
    {
        var document = AppSettingsDocument.Parse("{}");
        var followed = new List<IReadOnlyList<(string From, string To)>>();
        var profiles = new ModelProfilesViewModel(
            new InMemoryModelProfileStore(), new LlmSectionViewModel(() => document, () => { }),
            probe: new FakeLlmEndpointProbe(), keyStore: new FakeApiKeyStore(),
            links: new ModelProfilesLinks { FollowRename = followed.Add });
        SaveTwoModels(profiles);

        profiles.BeginEdit(profiles.Set.Find("mydeepseek.flash")!);
        profiles.Editor!.Name = "ds";
        profiles.Editor.SaveCommand.Execute(null);

        // One call carrying every rename: the teams follow them at once.
        var renames = Assert.Single(followed);
        Assert.Equal([("mydeepseek.flash", "ds.flash"), ("mydeepseek.pro", "ds.pro")], renames);
    }

    [Fact]
    public void Duplicating_a_card_copies_its_whole_connection()
    {
        var (profiles, _) = Build();
        SaveTwoModels(profiles);

        profiles.Profiles[0].DuplicateCommand.Execute(null);

        Assert.Equal(2, profiles.Connections.Count);
        Assert.Equal(4, profiles.Set.Profiles.Count);
        Assert.All(profiles.Connections, c => Assert.True(c.HasSeveralModels));
    }
}
