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
        Set(form, field, value);

        var errors = form.ApplyTo(document);

        Assert.Contains(errors, error => error.Contains(named, StringComparison.Ordinal));
        Assert.Equal(before, document.ToJson());
    }

    // A theory names its field by text: a misspelt row says so, instead of a null reference.
    private static void Set(object form, string field, string value)
    {
        var property = form.GetType().GetProperty(field);
        Assert.True(property is not null, $"{form.GetType().Name} has no field named '{field}': the theory's row is misspelt.");
        property.SetValue(form, value);
    }

    /// <summary>
    /// A field left as loaded is not written: a number or a switch the file writes as text stays text,
    /// through any number of applies.
    /// </summary>
    [Fact]
    public void A_field_left_as_loaded_keeps_its_key_as_the_file_writes_it()
    {
        var document = AppSettingsDocument.Parse("""
            {"Llm":{"ContextWindowTokens":"8000"},
             "Orkeon":{"Compaction":{"Enabled":"True","KeepRecentTurns":"3","Prune":{"FillShare":"0.5"}},"Skills":{"MaxSkillChars":"900"}}}
            """);
        var before = document.ToJson();
        var form = new ContextForm();
        form.LoadFrom(document);

        Assert.Empty(form.ApplyTo(document));
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal(before, document.ToJson());

        // One field edited: it alone is written.
        form.KeepRecentTurns = "4";
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal(before.Replace("\"3\"", "4", StringComparison.Ordinal), document.ToJson());
    }

    /// <summary>
    /// The screen cannot remove a key it does not show: a key that is no setting of its section is the
    /// settings check's to report, and must not lock the fields shown (nor, with them, the whole save).
    /// </summary>
    [Fact]
    public void A_key_the_screen_does_not_show_blocks_no_edit()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Compaction":{"Typo":1,"Prune":{"Other":2}},"Skills":{"Unknown":3}}}""");
        var form = new ContextForm();
        form.LoadFrom(document);

        Assert.Empty(form.ApplyTo(document));
        form.TriggerShare = "0.5";
        Assert.Empty(form.ApplyTo(document));

        Assert.Equal(0.5, document.Compaction.TriggerShare);
        Assert.Equal(1, document.GetInt32("Orkeon:Compaction:Typo"));
        // The section's own check still names it.
        Assert.Contains(document.Compaction.Problems(), problem => problem.Path.EndsWith(":Typo", StringComparison.Ordinal));
    }

    /// <summary>
    /// The configuration reads a key in whatever letter case: so does the screen, which shows it and
    /// writes it where the file spells it, never on a twin the run would read as a duplicate.
    /// </summary>
    [Fact]
    public void A_file_in_another_letter_case_is_shown_and_written_in_place()
    {
        var document = AppSettingsDocument.Parse("""
            {"llm":{"contextWindowTokens":64000},
             "orkeon":{"compaction":{"enabled":false,"triggerShare":0.6,"keepRecentTurns":3,"profile":"strong",
                                     "prune":{"enabled":true,"fillShare":0.3,"keepRecentMessages":4,"headChars":120}},
                       "skills":{"library":"/workspace/skills","maxSkillChars":900,"maxBlockChars":9000}}}
            """);
        var before = document.ToJson();
        var form = new ContextForm();

        form.LoadFrom(document);

        Assert.Equal(
            ["false", "0.6", "3", "strong", "64000", "true", "0.3", "4", "120", "/workspace/skills", "900", "9000"],
            new[]
            {
                form.CompactionEnabled, form.TriggerShare, form.KeepRecentTurns, form.Profile, form.ContextWindowTokens,
                form.PruneEnabled, form.PruneFillShare, form.PruneKeepRecentMessages, form.PruneHeadChars,
                form.SkillsLibrary, form.MaxSkillChars, form.MaxBlockChars,
            }.Select(text => text.ToLowerInvariant()));
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal(before, document.ToJson());

        form.TriggerShare = "0.7";
        form.ContextWindowTokens = "32000";
        form.PruneHeadChars = "";
        form.MaxSkillChars = "800";
        Assert.Empty(form.ApplyTo(document));

        Assert.Equal(0.7, document.GetDouble("orkeon:compaction:triggerShare"));
        Assert.Equal(32000, document.GetInt32("llm:contextWindowTokens"));
        Assert.False(document.ContainsPath("orkeon:compaction:prune:headChars"));
        Assert.Equal(800, document.GetInt32("orkeon:skills:maxSkillChars"));
        // No twin beside the keys in place.
        Assert.Equal(["llm", "orkeon"], document.Root.AsObject().Select(pair => pair.Key));
        Assert.Equal(["compaction", "skills"], document.Root["orkeon"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["contextWindowTokens"], document.Root["llm"]!.AsObject().Select(pair => pair.Key));
    }

    /// <summary>
    /// Applied twice to its document: a value set, then blanked, is written then removed; a refused
    /// apply writes nothing and keeps the text for a correction, which is then written.
    /// </summary>
    [Fact]
    public void A_second_apply_writes_what_changed_since_the_first()
    {
        var document = AppSettingsDocument.Parse("{}");
        var form = new ContextForm();
        form.LoadFrom(document);

        form.KeepRecentTurns = "4";
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal(4, document.Compaction.KeepRecentTurns);

        form.KeepRecentTurns = "";
        Assert.Empty(form.ApplyTo(document));
        Assert.False(document.ContainsPath("Orkeon:Compaction:KeepRecentTurns"));

        form.TriggerShare = "2";
        Assert.NotEmpty(form.ApplyTo(document));
        Assert.Equal("2", form.TriggerShare);
        Assert.Null(document.Compaction.TriggerShare);

        form.TriggerShare = "0.9";
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal(0.9, document.Compaction.TriggerShare);
    }

    /// <summary>
    /// A switch the file writes without a value (a JSON null, an empty object) binds to false: the
    /// field shows false, what the run gets, not the blank that stands for the default; the key stays
    /// as written until the field changes, and a field blanked removes it.
    /// </summary>
    [Fact]
    public void A_switch_written_without_a_value_is_shown_as_the_run_reads_it()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Compaction":{"Enabled":null,"Prune":{"Enabled":{}}}}}""");
        var before = document.ToJson();
        var form = new ContextForm();
        form.LoadFrom(document);

        Assert.Equal("false", form.CompactionEnabled);
        Assert.Equal("false", form.PruneEnabled);
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal(before, document.ToJson());
        Assert.Equal("false", form.CompactionEnabled);

        form.CompactionEnabled = "";
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal(CompactionSection.DefaultEnabled, document.Compaction.IsEnabled);
        Assert.Equal("", form.CompactionEnabled);
        Assert.False(document.Compaction.IsPruneEnabled);
        Assert.Equal("false", form.PruneEnabled);

        // No key: the blank that stands for the default.
        var empty = new ContextForm();
        empty.LoadFrom(AppSettingsDocument.CreateEmpty());
        Assert.Equal("", empty.CompactionEnabled);
        Assert.Equal("", empty.PruneEnabled);
    }
}
