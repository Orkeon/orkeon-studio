using Orkeon.Application.Crew.Compaction;
using Orkeon.Application.Crew.Skills;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// The compaction of a context, the pruning of its tool results and the skills, as Studio reads and
/// writes them: every key optional, the defaults the engine's own, and a value the engine's start
/// refuses judged by the engine's rules, at the path of its key.
/// </summary>
public sealed class CompactionAndSkillsSectionTests
{
    [Fact]
    public void An_empty_file_gets_the_engines_defaults()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var compaction = new ContextCompactionOptions();
        var prune = new ToolResultPruningOptions();
        var skills = new AgentSkillsOptions();

        Assert.False(document.Compaction.Exists);
        Assert.Equal(compaction.Enabled, document.Compaction.IsEnabled);
        Assert.Equal(prune.Enabled, document.Compaction.IsPruneEnabled);
        Assert.True(document.Compaction.IsEnabled);
        Assert.False(document.Compaction.IsPruneEnabled);
        Assert.Equal(CompactionSection.DefaultTriggerShare, compaction.TriggerShare);
        Assert.Equal(CompactionSection.DefaultKeepRecentTurns, compaction.KeepRecentTurns);
        Assert.Equal(CompactionSection.DefaultPruneFillShare, prune.FillShare);
        Assert.Equal(CompactionSection.DefaultPruneKeepRecentMessages, prune.KeepRecentMessages);
        Assert.Equal(CompactionSection.DefaultPruneHeadChars, prune.HeadChars);
        Assert.Equal(SkillsSection.DefaultLibrary, skills.Library);
        Assert.Equal(SkillsSection.DefaultMaxSkillChars, skills.MaxSkillChars);
        Assert.Equal(SkillsSection.DefaultMaxBlockChars, skills.MaxBlockChars);
        Assert.Empty(document.Compaction.Problems());
        Assert.Empty(document.Skills.Problems());
    }

    [Fact]
    public void Every_key_is_written_in_place_and_removed_when_unset()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Compaction": { "Mine": 1 }, "Decision": { "Provider": "liquid" } } }""");
        var compaction = document.Compaction;
        compaction.Enabled = false;
        compaction.TriggerShare = 0.6;
        compaction.KeepRecentTurns = 4;
        compaction.Profile = " cheap ";
        compaction.PruneEnabled = true;
        compaction.PruneFillShare = 0.5;
        compaction.PruneKeepRecentMessages = 2;
        compaction.PruneHeadChars = 120;
        document.Skills.Library = "/workspace/skills";
        document.Skills.MaxSkillChars = 2000;
        document.Skills.MaxBlockChars = 6000;

        var reparsed = AppSettingsDocument.Parse(document.ToJson());
        Assert.False(reparsed.Compaction.Enabled);
        Assert.Equal(0.6, reparsed.Compaction.TriggerShare);
        Assert.Equal(4, reparsed.Compaction.KeepRecentTurns);
        Assert.Equal("cheap", reparsed.Compaction.Profile);
        Assert.True(reparsed.Compaction.PruneEnabled);
        Assert.Equal(0.5, reparsed.Compaction.PruneFillShare);
        Assert.Equal(2, reparsed.Compaction.PruneKeepRecentMessages);
        Assert.Equal(120, reparsed.Compaction.PruneHeadChars);
        Assert.Equal("/workspace/skills", reparsed.Skills.Library);
        Assert.Equal(2000, reparsed.Skills.MaxSkillChars);
        Assert.Equal(6000, reparsed.Skills.MaxBlockChars);
        // What Studio does not model stays.
        Assert.Equal(1, reparsed.GetInt32("Orkeon:Compaction:Mine"));
        Assert.Equal("liquid", reparsed.Decision.Provider);

        compaction.Enabled = null;
        compaction.Profile = "";
        Assert.False(document.ContainsPath("Orkeon:Compaction:Enabled"));
        Assert.False(document.ContainsPath("Orkeon:Compaction:Profile"));
    }

    [Theory]
    [InlineData("""{ "Orkeon": { "Compaction": { "TriggerShare": 1.5 } } }""", "Orkeon:Compaction:TriggerShare")]
    [InlineData("""{ "Orkeon": { "Compaction": { "KeepRecentTurns": -1 } } }""", "Orkeon:Compaction:KeepRecentTurns")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Profile": "auto" } } }""", "Orkeon:Compaction:Profile")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Profile": "missing" } } }""", "Orkeon:Compaction:Profile")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Prune": { "FillShare": 0 } } } }""", "Orkeon:Compaction:Prune:FillShare")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Prune": { "HeadChars": 0 } } } }""", "Orkeon:Compaction:Prune:HeadChars")]
    [InlineData("""{ "Orkeon": { "Skills": { "Library": "skills" } } }""", "Orkeon:Skills:Library")]
    [InlineData("""{ "Orkeon": { "Skills": { "MaxSkillChars": 0 } } }""", "Orkeon:Skills:MaxSkillChars")]
    [InlineData("""{ "Orkeon": { "Skills": { "MaxBlockChars": 100 } } }""", "Orkeon:Skills:MaxBlockChars")]
    public void A_value_the_engines_start_refuses_is_an_error_at_its_key(string json, string path)
    {
        var messages = new AppSettingsValidator().Validate(AppSettingsDocument.Parse(json));

        var error = Assert.Single(messages, m => m.Code == ValidationCodes.EngineSettingRefused);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Equal(path, error.Path);
    }

    [Fact]
    public void A_value_the_binder_cannot_convert_is_an_error_at_its_section()
    {
        var messages = new AppSettingsValidator().Validate(
            AppSettingsDocument.Parse("""{ "Orkeon": { "Compaction": { "KeepRecentTurns": "many" } } }"""));

        var error = Assert.Single(messages, m => m.Code == ValidationCodes.EngineSettingRefused);
        Assert.Equal(CompactionSection.SectionPath, error.Path);
        Assert.Contains("many", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_profile_of_the_settings_and_the_default_profile_are_accepted()
    {
        var document = AppSettingsDocument.Parse("""
            { "Llm": { "Model": "small", "Profiles": { "Cheap": { "Model": "tiny" } } },
              "Orkeon": { "Compaction": { "Profile": "cheap" } } }
            """);
        Assert.Empty(document.Compaction.Problems());

        document.Compaction.Profile = "default";
        Assert.Empty(document.Compaction.Problems());
    }

    /// <summary>
    /// The window a compaction measures against: above zero, in the default section and in a profile's.
    /// A profile's entry does not model it, so the one written by hand in a profile survives a save of
    /// that profile.
    /// </summary>
    [Fact]
    public void A_context_window_is_above_zero_and_a_profiles_own_survives_its_save()
    {
        var document = AppSettingsDocument.Parse("""
            { "Llm": { "Model": "small", "ContextWindowTokens": 0,
                       "Profiles": { "big": { "Model": "large", "ContextWindowTokens": 1000000 } } } }
            """);

        var error = Assert.Single(new AppSettingsValidator().Validate(document), m => m.Severity == ValidationSeverity.Error);
        Assert.Equal("Llm:ContextWindowTokens", error.Path);
        Assert.Contains("above zero", error.Text, StringComparison.Ordinal);

        document.Llm.ContextWindowTokens = 128000;
        Assert.DoesNotContain(new AppSettingsValidator().Validate(document), m => m.Severity == ValidationSeverity.Error);

        var entry = document.Llm.Profiles.Get("big");
        Assert.NotNull(entry);
        Assert.True(document.Llm.Profiles.Set(entry with { Model = "larger" }));
        Assert.Equal(1000000, document.GetInt32("Llm:Profiles:big:ContextWindowTokens"));
        Assert.Equal(128000, document.Llm.ContextWindowTokens);
    }
}
