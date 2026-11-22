using System.ComponentModel;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;
using Orkeon.Studio.Wpf.ViewModels.Services;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The compaction and skills cards of the settings: a switch set back to the engine's default removes
/// its key, a share is typed with a point or a comma, and what the engine's start refuses is said
/// under the fields.
/// </summary>
public sealed class CompactionAndSkillsViewModelTests
{
    private static (CompactionSectionViewModel Compaction, SkillsSectionViewModel Skills, AppSettingsDocument Document, List<int> Dirty) Forms(string json = "{}")
    {
        var document = AppSettingsDocument.Parse(json);
        var dirty = new List<int>();
        return (
            new CompactionSectionViewModel(() => document, () => dirty.Add(1)),
            new SkillsSectionViewModel(() => document, () => dirty.Add(1)),
            document,
            dirty);
    }

    [Fact]
    public void An_empty_file_shows_the_engines_defaults_and_no_problem()
    {
        var (compaction, skills, _, _) = Forms();

        Assert.False(compaction.Exists);
        Assert.True(compaction.Enabled);
        Assert.False(compaction.PruneEnabled);
        Assert.Equal("", compaction.TriggerShare);
        Assert.Equal("0.75", CompactionSectionViewModel.TriggerShareDefault);
        Assert.Equal("", compaction.ProblemsText);
        Assert.Equal("/workspace/.orkeon/skills", SkillsSectionViewModel.LibraryDefault);
        Assert.Equal("", skills.ProblemsText);
    }

    [Fact]
    public void A_switch_writes_what_differs_from_the_default_and_removes_its_key_when_set_back()
    {
        var (compaction, _, document, dirty) = Forms();

        compaction.Enabled = false;
        compaction.PruneEnabled = true;
        Assert.False(document.Compaction.Enabled);
        Assert.True(document.Compaction.PruneEnabled);
        Assert.Equal(2, dirty.Count);

        compaction.Enabled = true;
        compaction.PruneEnabled = false;
        Assert.False(document.ContainsPath("Orkeon:Compaction:Enabled"));
        Assert.False(document.ContainsPath("Orkeon:Compaction:Prune:Enabled"));
    }

    [Fact]
    public void A_share_is_typed_with_a_point_or_a_comma_and_an_unreadable_one_changes_nothing()
    {
        var (compaction, _, document, _) = Forms();

        compaction.TriggerShare = "0,6";
        Assert.Equal(0.6, document.Compaction.TriggerShare);
        Assert.Equal("0.6", compaction.TriggerShare);

        compaction.TriggerShare = "most of it";
        Assert.Equal(0.6, document.Compaction.TriggerShare);

        compaction.TriggerShare = " ";
        Assert.Null(document.Compaction.TriggerShare);
    }

    /// <summary>NaN and the infinities parse as numbers, and the engine refuses them at start (Orkeon #454): nothing is written.</summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void A_share_that_is_no_finite_number_changes_nothing(string typed)
    {
        var (compaction, _, document, _) = Forms();
        compaction.TriggerShare = "0.6";
        compaction.PruneFillShare = "0.5";

        compaction.TriggerShare = typed;
        compaction.PruneFillShare = typed;

        Assert.Equal(0.6, document.Compaction.TriggerShare);
        Assert.Equal(0.5, document.Compaction.PruneFillShare);
        Assert.Equal("0.6", compaction.TriggerShare);
    }

    [Fact]
    public void What_the_engines_start_refuses_is_said_under_the_fields()
    {
        var (compaction, skills, document, _) = Forms();

        compaction.TriggerShare = "1.5";
        compaction.Profile = "auto";
        skills.MaxBlockChars = 100;
        compaction.ContextWindowTokens = 128000;

        Assert.Contains("Orkeon:Compaction:TriggerShare", compaction.ProblemsText, StringComparison.Ordinal);
        Assert.Contains("Orkeon:Compaction:Profile", compaction.ProblemsText, StringComparison.Ordinal);
        Assert.Contains("Orkeon:Skills:MaxBlockChars", skills.ProblemsText, StringComparison.Ordinal);
        Assert.Equal(128000, document.Llm.ContextWindowTokens);

        compaction.TriggerShare = "";
        compaction.Profile = "";
        skills.MaxBlockChars = null;
        Assert.Equal("", compaction.ProblemsText);
        Assert.Equal("", skills.ProblemsText);
    }

    /// <summary>
    /// The two switches written without a value (a JSON null, an empty object) bind to false: the
    /// form shows them off, as the run gets them, never the default; nothing is rewritten until a
    /// switch is touched.
    /// </summary>
    [Fact]
    public void Switches_written_without_a_value_are_shown_off_and_kept_until_touched()
    {
        var (compaction, _, document, dirty) = Forms("""{"Orkeon":{"Compaction":{"Enabled":null,"Prune":{"Enabled":{}}}}}""");
        var before = document.ToJson();

        Assert.False(compaction.Enabled);
        Assert.False(compaction.PruneEnabled);
        compaction.Enabled = false;
        compaction.PruneEnabled = false;
        Assert.Equal(before, document.ToJson());
        Assert.Empty(dirty);

        compaction.Enabled = true;
        Assert.True(document.Compaction.IsEnabled);
        Assert.False(document.Compaction.IsPruneEnabled);
        Assert.NotEmpty(dirty);
    }

    /// <summary>
    /// A summary profile typed that no profile bears joins the choices as it is typed: the combo shows
    /// an item of its list only, and would blank a name the list was not said again with.
    /// </summary>
    [Fact]
    public void A_profile_typed_that_no_profile_bears_is_said_with_the_choices()
    {
        var (compaction, _, _, _) = Forms();
        var raised = new List<string?>();
        compaction.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        compaction.Profile = "summariser";

        Assert.Contains(nameof(CompactionSectionViewModel.ProfileChoices), raised);
        Assert.Contains("summariser", compaction.ProfileChoices);
    }

    /// <summary>
    /// A default model elected takes out the window stated for the one it replaces
    /// (<c>Llm:ContextWindowTokens</c>): the compaction card's field says so at once, not at the next load.
    /// </summary>
    [Fact]
    public void A_default_model_changed_empties_the_window_the_card_shows()
    {
        var config = new ConfigTabViewModel(
            new StudioServices { SettingsStore = new FakeAppSettingsStore(), Directories = new FakeDirectoryProbe("/data") });
        config.Compaction.ContextWindowTokens = 128000;
        var raised = new List<string?>();
        ((INotifyPropertyChanged)config.Compaction).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        config.Llm.ElectDefault(new ModelProfile
        {
            Name = "DeepSeek", Provider = "DeepSeek", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat", KeyEnvName = "DEEPSEEK_API_KEY",
        });

        Assert.Null(config.Compaction.ContextWindowTokens);
        Assert.Contains(nameof(CompactionSectionViewModel.ContextWindowTokens), raised);
    }
}
