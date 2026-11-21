using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Tests.Presentation;

/// <summary>
/// The context and skills form of the terminal editor: every field is optional text, a blank one
/// removes its key, and a value the engine's start refuses is refused at apply with nothing written.
/// </summary>
public sealed class ContextFormTests
{
    [Fact]
    public void Load_shows_the_values_as_the_file_writes_them()
    {
        var document = AppSettingsDocument.Parse("""
            {"Llm":{"ContextWindowTokens":128000},
             "Orkeon":{"Compaction":{"Enabled":false,"TriggerShare":0.6,"Prune":{"Enabled":true,"HeadChars":120}},
                       "Skills":{"Library":"/workspace/skills"}}}
            """);
        var form = new ContextForm();

        form.LoadFrom(document);

        Assert.Equal("false", form.CompactionEnabled, ignoreCase: true);
        Assert.Equal("0.6", form.TriggerShare);
        Assert.Equal("", form.KeepRecentTurns);
        Assert.Equal("128000", form.ContextWindowTokens);
        Assert.Equal("true", form.PruneEnabled, ignoreCase: true);
        Assert.Equal("120", form.PruneHeadChars);
        Assert.Equal("/workspace/skills", form.SkillsLibrary);
    }

    [Fact]
    public void Apply_writes_the_fields_and_removes_the_blank_ones()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Compaction":{"Enabled":false,"KeepRecentTurns":3}}}""");
        var form = new ContextForm();
        form.LoadFrom(document);

        form.CompactionEnabled = "";
        form.TriggerShare = "0.5";
        form.PruneEnabled = "true";
        form.MaxSkillChars = "2000";
        form.ContextWindowTokens = "64000";
        Assert.Empty(form.ApplyTo(document));

        Assert.Null(document.Compaction.Enabled);
        Assert.Equal(0.5, document.Compaction.TriggerShare);
        Assert.Equal(3, document.Compaction.KeepRecentTurns);
        Assert.True(document.Compaction.PruneEnabled);
        Assert.Equal(2000, document.Skills.MaxSkillChars);
        Assert.Equal(64000, document.Llm.ContextWindowTokens);
    }

    [Theory]
    [InlineData("TriggerShare", "2", "Orkeon:Compaction:TriggerShare")]
    [InlineData("SkillsLibrary", "skills", "Orkeon:Skills:Library")]
    [InlineData("Profile", "auto", "Orkeon:Compaction:Profile")]
    [InlineData("ContextWindowTokens", "0", "Llm:ContextWindowTokens")]
    [InlineData("KeepRecentTurns", "many", "Orkeon:Compaction:KeepRecentTurns")]
    public void A_value_the_engine_refuses_is_refused_at_apply_and_nothing_is_written(string field, string value, string named)
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Compaction":{"KeepRecentTurns":3}}}""");
        var before = document.ToJson();
        var form = new ContextForm();
        form.LoadFrom(document);
        typeof(ContextForm).GetProperty(field)!.SetValue(form, value);

        var errors = form.ApplyTo(document);

        Assert.Contains(errors, error => error.Contains(named, StringComparison.Ordinal));
        Assert.Equal(before, document.ToJson());
    }
}
