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
    [InlineData("""{ "Orkeon": { "Compaction": { "Prune": { "FillShare": 0 } } } }""", "Orkeon:Compaction:Prune:FillShare")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Prune": { "HeadChars": 0 } } } }""", "Orkeon:Compaction:Prune:HeadChars")]
    [InlineData("""{ "Orkeon": { "Skills": { "Library": "skills" } } }""", "Orkeon:Skills:Library")]
    [InlineData("""{ "Orkeon": { "Skills": { "MaxSkillChars": 0 } } }""", "Orkeon:Skills:MaxSkillChars")]
    [InlineData("""{ "Orkeon": { "Skills": { "MaxBlockChars": 100 } } }""", "Orkeon:Skills:MaxBlockChars")]
    [InlineData("""{ "Orkeon": { "Skills": { "Libary": "/x" } } }""", "Orkeon:Skills:Libary")]
    [InlineData("""{ "Orkeon": { "Compaction": { "TrigerShare": 0.5 } } }""", "Orkeon:Compaction:TrigerShare")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Prune": { "Head": 3 } } } }""", "Orkeon:Compaction:Prune:Head")]
    [InlineData("""{ "orkeon": { "compaction": { "triggerShare": 2 } } }""", "Orkeon:Compaction:TriggerShare")]
    public void A_value_the_engines_start_refuses_is_an_error_at_its_key(string json, string path)
    {
        var messages = new AppSettingsValidator().Validate(AppSettingsDocument.Parse(json));

        var error = Assert.Single(messages, m => m.Code == ValidationCodes.EngineSettingRefused);
        Assert.Equal(ValidationSeverity.Error, error.Severity);
        Assert.Equal(path, error.Path);
    }

    [Fact]
    public void A_value_the_binder_cannot_convert_is_an_error_at_its_key()
    {
        var messages = new AppSettingsValidator().Validate(
            AppSettingsDocument.Parse("""{ "Orkeon": { "Compaction": { "KeepRecentTurns": "many" } } }"""));

        var error = Assert.Single(messages, m => m.Code == ValidationCodes.EngineSettingRefused);
        Assert.Equal("Orkeon:Compaction:KeepRecentTurns", error.Path);
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
    /// that profile, until the profile gets another model.
    /// </summary>
    [Fact]
    public void A_context_window_is_above_zero_and_stays_with_the_model_it_was_written_for()
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
        Assert.True(document.Llm.Profiles.Set(entry with { Temperature = 0.2 }));
        Assert.Equal(1000000, document.GetInt32("Llm:Profiles:big:ContextWindowTokens"));
        Assert.Equal(128000, document.Llm.ContextWindowTokens);

        // The window was stated for that model: another model in the section does not inherit it.
        Assert.True(document.Llm.Profiles.Set(entry with { Model = "small-local" }));
        Assert.False(document.ContainsPath("Llm:Profiles:big:ContextWindowTokens"));
        Assert.Equal(128000, document.Llm.ContextWindowTokens);
    }

    /// <summary>
    /// The engine registers the decision profiles as profiles too: one of them may write the summary.
    /// A profile these settings do not define is the engine's refusal, said as a warning: a run launched
    /// from Studio is given Studio's own model profiles through its environment.
    /// </summary>
    [Fact]
    public void A_summary_profile_may_be_a_decision_profile_and_an_unknown_one_only_warns()
    {
        var guarded = AppSettingsDocument.Parse("""
            { "Orkeon": { "Decision": { "Profiles": { "Guarded": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" } } ] } } }, "Compaction": { "Profile": "guarded" } } }
            """);
        Assert.Empty(guarded.Compaction.Problems());
        Assert.Null(guarded.Compaction.UnknownProfile());
        Assert.DoesNotContain(new AppSettingsValidator().Validate(guarded), m => m.Code == ValidationCodes.EngineSettingRefused);

        var missing = AppSettingsDocument.Parse("""{ "Orkeon": { "Compaction": { "Profile": "missing" } } }""");
        Assert.Empty(missing.Compaction.Problems());
        var warning = Assert.Single(new AppSettingsValidator().Validate(missing), m => m.Code == ValidationCodes.EngineSettingRefused);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Equal("Orkeon:Compaction:Profile", warning.Path);
        // The engine's own sentence, built by its public helper.
        Assert.Equal(
            Orkeon.Application.Interfaces.Ports.LlmProfiles.UnknownMessage("missing", "Orkeon:Compaction:Profile", []),
            warning.Text);
    }

    /// <summary>
    /// The summary's profile against the profiles of the file: left out, the default in any letter case
    /// and <c>auto</c> (which the start refuses in its own words) are no unknown profile; a profile of
    /// either section is known whatever its letter case and the spaces around it; any other is unknown.
    /// </summary>
    [Theory]
    [InlineData("null", false)]
    [InlineData("\"\"", false)]
    [InlineData("[]", false)]
    [InlineData("\"default\"", false)]
    [InlineData("\" Default \"", false)]
    [InlineData("\"auto\"", false)]
    [InlineData("\" AUTO \"", false)]
    [InlineData("\"cheap\"", false)]
    [InlineData("\" CHEAP \"", false)]
    [InlineData("\"guarded\"", false)]
    [InlineData("\"cheaper\"", true)]
    [InlineData("\"defaults\"", true)]
    public void A_summary_profile_is_unknown_when_no_section_of_the_file_defines_it(string written, bool unknown)
    {
        var document = AppSettingsDocument.Parse($$"""
            { "llm": { "Model": "small", "profiles": { "Cheap": { "Model": "tiny" } } },
              "Orkeon": { "Decision": { "Profiles": { "Guarded": { "Target": "default" } } }, "Compaction": { "profile": {{written}} } } }
            """);

        var problem = document.Compaction.UnknownProfile();

        Assert.Equal(unknown, problem is not null);
        if (problem is { } said)
        {
            Assert.Equal("Orkeon:Compaction:Profile", said.Path);
            Assert.Contains("Cheap", said.Problem, StringComparison.Ordinal);
            Assert.Contains("Guarded", said.Problem, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A section's context window goes when its model changes, whoever writes the model: the election
    /// of a setting, a preset, a form through the section's own <c>Model</c>. It stays when the model
    /// written is the one the section holds, in whatever letter case the file spells its keys and with
    /// whatever spaces around the model: no twin key is written beside the file's.
    /// </summary>
    [Fact]
    public void A_context_window_goes_with_its_model_whoever_writes_the_model()
    {
        const string Json = """{ "llm": { "model": " x ", "ContextWindowTokens": 200000, "baseUrl": "http://localhost:11434" } }""";
        var entry = new LlmProfileEntry { Id = "default", Model = "x", BaseUrl = "http://localhost:11434" };

        // The election of a setting with the same model: the window stays, the keys stay where they are.
        var elected = AppSettingsDocument.Parse(Json);
        elected.Llm.Set(entry);
        Assert.Equal(200000, elected.Llm.ContextWindowTokens);
        Assert.Equal("x", elected.Llm.Model);
        Assert.Null(elected.LoadRefusal());
        Assert.DoesNotContain("\"Model\"", elected.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"Llm\"", elected.ToJson(), StringComparison.Ordinal);

        // The election of a setting with another model.
        elected.Llm.Set(entry with { Model = "y" });
        Assert.Null(elected.Llm.ContextWindowTokens);
        Assert.Equal("y", elected.GetString("llm:model"));

        // A form, which writes the section's Model: the same model, then none over it, then another.
        var form = AppSettingsDocument.Parse(Json);
        form.Llm.Model = "x";
        Assert.Equal(200000, form.Llm.ContextWindowTokens);
        form.Llm.Model = "z";
        Assert.Null(form.Llm.ContextWindowTokens);
        Assert.Equal("z", form.GetString("llm:model"));
        Assert.False(form.ContainsPath("llm:ContextWindowTokens"));

        // A model that is only spaces is no model: writing none over it changes nothing.
        var blank = AppSettingsDocument.Parse("""{ "Llm": { "Model": "  ", "ContextWindowTokens": 200000, "BaseUrl": "http://localhost:11434" } }""");
        blank.Llm.Set(entry with { Model = null });
        Assert.Equal(200000, blank.Llm.ContextWindowTokens);
        blank.Llm.Model = null;
        Assert.Equal(200000, blank.Llm.ContextWindowTokens);

        // A preset.
        var preset = AppSettingsDocument.Parse(Json);
        Orkeon.Studio.Core.Presets.LlmPresets.Apply(preset, new Orkeon.Studio.Core.Presets.LlmPresetPlan
        {
            Preset = "ollama", BaseUrl = "http://localhost:11434", Model = "qwen3",
        });
        Assert.Null(preset.Llm.ContextWindowTokens);
        Assert.Equal("qwen3", preset.GetString("llm:model"));
    }

    /// <summary>
    /// A profile is named by more than the crews: the summary's profile and the rungs of the ladder
    /// follow the rename of a model setting, as the knowledge base's profile does; when the setting
    /// goes, the summary falls back to the agent's own model and the ladder is left as it is.
    /// </summary>
    [Fact]
    public void The_summary_profile_and_the_ladder_follow_the_rename_of_a_model_setting()
    {
        var before = Orkeon.Studio.Core.Profiles.ModelProfileSet.Empty
            .Upsert(new Orkeon.Studio.Core.Profiles.ModelProfile { Name = "Deep Seek", Provider = "DeepSeek", BaseUrl = "https://api.deepseek.com", Model = "deepseek-v4-flash" })
            .Upsert(new Orkeon.Studio.Core.Profiles.ModelProfile { Name = "Local", Provider = "Ollama", BaseUrl = "http://localhost:11434", Model = "qwen3" });
        var document = AppSettingsDocument.CreateEmpty();
        Orkeon.Studio.Core.Profiles.HostLlmProfiles.Mirror(document, Orkeon.Studio.Core.Profiles.ModelProfileSet.Empty, before);
        document.Compaction.Profile = "Deep-Seek";
        document.Decision.RouteLadder = ["local", " deep-seek ", "default"];
        document.Rag.LlmProfile = "deep-seek";

        var renamed = before.Upsert(before.Find("Deep Seek")! with { Name = "Reasoner" }, previousName: "Deep Seek");
        Assert.True(Orkeon.Studio.Core.Profiles.HostLlmProfiles.Mirror(document, before, renamed, "Deep Seek", "Reasoner"));

        Assert.Equal("reasoner", document.Compaction.Profile);
        Assert.Equal(["local", "reasoner", "default"], document.Decision.RouteLadder);
        Assert.Equal("reasoner", document.Rag.LlmProfile);
        Assert.Null(document.Compaction.UnknownProfile());
        Assert.Empty(document.Decision.Resolve(_ => null).UnknownRungs);

        var removed = renamed.Remove("Reasoner");
        Assert.True(Orkeon.Studio.Core.Profiles.HostLlmProfiles.Mirror(document, renamed, removed));

        Assert.Null(document.Compaction.Profile);
        Assert.Null(document.Rag.LlmProfile);
        Assert.Equal(["local", "reasoner", "default"], document.Decision.RouteLadder);
    }
}
