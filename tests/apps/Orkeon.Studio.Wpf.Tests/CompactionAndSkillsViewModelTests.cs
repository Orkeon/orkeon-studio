using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Wpf.ViewModels.Config;

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
}
