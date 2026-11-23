using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Profiles;

namespace Orkeon.Studio.Core.Tests.Profiles;

/// <summary>
/// A connection of several models is as many host profiles, one per pair: renaming the connection
/// moves each entry with what names it, the editor judges each model's id against the others, and
/// the ids the engine reserves — <c>default</c> and <c>auto</c> — are refused before saving.
/// </summary>
public sealed class HostLlmProfilesConnectionTests
{
    private static ModelConnection DeepSeek(string name = "mydeepseek") => new()
    {
        Name = name,
        Provider = "deepseek",
        BaseUrl = "https://api.deepseek.com",
        KeyEnvName = "DEEPSEEK_API_KEY",
        Models =
        [
            new ConnectionModel { Alias = "flash", Model = "deepseek-v4-flash" },
            new ConnectionModel { Alias = "pro", Model = "deepseek-v4-pro", MaxTokens = 64000 },
        ],
    };

    [Fact]
    public void Each_pair_is_an_entry_of_its_own_carrying_the_connections_key_variable()
    {
        var document = AppSettingsDocument.Parse("{}");
        var set = ModelProfileSet.Empty.UpsertConnection(DeepSeek()).Set;

        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);

        Assert.Equal(["mydeepseek-flash", "mydeepseek-pro"], document.Llm.Profiles.Ids);
        Assert.All(document.Llm.Profiles.Ids, id => Assert.Equal("DEEPSEEK_API_KEY", document.Llm.Profiles.Get(id)?.ApiKeyEnvVar));
        Assert.Equal("deepseek-v4-pro", document.Llm.Profiles.Get("mydeepseek-pro")?.Model);
        Assert.Equal(64000, document.Llm.Profiles.Get("mydeepseek-pro")?.MaxTokens);
        Assert.Null(document.Llm.Profiles.Get("mydeepseek-flash")?.MaxTokens);
    }

    [Fact]
    public void Renaming_a_connection_moves_each_entry_and_what_names_it()
    {
        var document = AppSettingsDocument.Parse("{}");
        var set = ModelProfileSet.Empty.UpsertConnection(DeepSeek()).Set;
        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);
        document.SetInt32("Llm:Profiles:mydeepseek-pro:MaxRetries", 3);
        document.Rag.LlmProfile = "mydeepseek-flash";
        document.Compaction.Profile = "mydeepseek-pro";
        document.Decision.RouteLadder = ["mydeepseek-flash", "mydeepseek-pro", "default"];

        var upsert = set.UpsertConnection(DeepSeek("ds"), previousName: "mydeepseek");
        Assert.True(HostLlmProfiles.Mirror(document, set, upsert.Set, upsert.Renames));

        Assert.Equal(["ds-flash", "ds-pro"], document.Llm.Profiles.Ids.Order(StringComparer.Ordinal));
        Assert.Equal(3, document.GetInt32("Llm:Profiles:ds-pro:MaxRetries"));
        Assert.Equal("ds-flash", document.Rag.LlmProfile);
        Assert.Equal("ds-pro", document.Compaction.Profile);
        Assert.Equal(["ds-flash", "ds-pro", "default"], document.Decision.RouteLadder);
    }

    [Fact]
    public void Two_models_that_swap_their_aliases_swap_their_entries()
    {
        var document = AppSettingsDocument.Parse("{}");
        var set = ModelProfileSet.Empty.UpsertConnection(DeepSeek()).Set;
        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);
        document.SetInt32("Llm:Profiles:mydeepseek-pro:MaxRetries", 3);
        document.Decision.RouteLadder = ["mydeepseek-flash", "mydeepseek-pro"];

        var swapped = DeepSeek() with
        {
            Models =
            [
                new ConnectionModel { Alias = "pro", Model = "deepseek-v4-flash" },
                new ConnectionModel { Alias = "flash", Model = "deepseek-v4-pro", MaxTokens = 64000 },
            ],
        };
        var upsert = set.UpsertConnection(swapped, previousPairNames: ["mydeepseek.flash", "mydeepseek.pro"]);
        HostLlmProfiles.Mirror(document, set, upsert.Set, upsert.Renames);

        Assert.Equal("deepseek-v4-pro", document.Llm.Profiles.Get("mydeepseek-flash")?.Model);
        Assert.Equal(3, document.GetInt32("Llm:Profiles:mydeepseek-flash:MaxRetries"));
        Assert.Equal("deepseek-v4-flash", document.Llm.Profiles.Get("mydeepseek-pro")?.Model);
        Assert.Equal(["mydeepseek-pro", "mydeepseek-flash"], document.Decision.RouteLadder);
    }

    [Fact]
    public void A_model_dropped_from_its_connection_takes_its_entry_out()
    {
        var document = AppSettingsDocument.Parse("{}");
        var set = ModelProfileSet.Empty.UpsertConnection(DeepSeek()).Set;
        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);

        var edited = set.Remove("mydeepseek.pro");
        HostLlmProfiles.Mirror(document, set, edited);

        Assert.Equal(["mydeepseek-flash"], document.Llm.Profiles.Ids);
    }

    [Fact]
    public void A_model_whose_id_a_model_above_it_gives_is_told_whose_it_is()
    {
        var document = AppSettingsDocument.Parse("{}");

        var check = HostLlmProfiles.Check("mydeepseek.Flash", [], ["mydeepseek.flash"], describesProvider: true, ModelProfileSet.Empty, document);

        Assert.Equal(HostProfileStatus.TakenBySetting, check.Status);
        Assert.Equal("mydeepseek.flash", check.TakenBy);
        Assert.True(check.BlocksSave);
    }

    [Fact]
    public void The_pairs_the_edit_replaces_own_no_id_but_another_connections_do()
    {
        var document = AppSettingsDocument.Parse("{}");
        var set = ModelProfileSet.Empty
            .UpsertConnection(DeepSeek()).Set
            .Upsert(new ModelProfile { Name = "ds.flash", Model = "m" });

        var own = HostLlmProfiles.Check("mydeepseek.flash", ["mydeepseek.flash", "mydeepseek.pro"], [], true, set, document);
        var other = HostLlmProfiles.Check("ds.flash", ["mydeepseek.flash", "mydeepseek.pro"], [], true, set, document);

        Assert.Equal(HostProfileStatus.Offered, own.Status);
        Assert.Equal(HostProfileStatus.TakenBySetting, other.Status);
        Assert.Equal("ds.flash", other.TakenBy);
    }

    [Theory]
    [InlineData("Auto", HostProfileStatus.AutoName)]
    [InlineData("auto", HostProfileStatus.AutoName)]
    [InlineData("Default", HostProfileStatus.DefaultName)]
    public void A_name_the_engine_reserves_is_refused_by_the_editor_and_offered_to_no_crew(string name, HostProfileStatus status)
    {
        var document = AppSettingsDocument.Parse("{}");
        var set = ModelProfileSet.Empty.Upsert(new ModelProfile { Name = name, Model = "m", BaseUrl = "http://localhost:1" });

        var check = HostLlmProfiles.Check(name, previousName: null, describesProvider: true, ModelProfileSet.Empty, document);

        Assert.Equal(status, check.Status);
        Assert.True(check.BlocksSave);
        Assert.Equal(status, HostLlmProfiles.Classify(set)[name].Status);
        Assert.Empty(HostLlmProfiles.Offered(set));
    }

    private static ModelConnection Ladder(params (string Alias, string Model)[] models) => new()
    {
        Name = "x",
        Provider = "deepseek",
        BaseUrl = "https://api.deepseek.com",
        KeyEnvName = "DEEPSEEK_API_KEY",
        Models = [.. models.Select(model => new ConnectionModel { Alias = model.Alias, Model = model.Model })],
    };

    /// <summary>The connection x of three models a, b, c, mirrored, its pairs the rungs of the ladder.</summary>
    private static (AppSettingsDocument Document, ModelProfileSet Set) ThreeRungs()
    {
        var document = AppSettingsDocument.Parse("{}");
        var set = ModelProfileSet.Empty.UpsertConnection(Ladder(("a", "m-a"), ("b", "m-b"), ("c", "m-c"))).Set;
        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);
        document.SetInt32("Llm:Profiles:x-b:MaxRetries", 3);
        document.Decision.RouteLadder = ["x-a", "x-b", "x-c"];
        return (document, set);
    }

    /// <summary>
    /// Removing a and renaming b to a and c to b in one save: the removal comes first, so each entry
    /// lands under its new id — none left behind under a staging id —, the rung of the removed model
    /// goes rather than name another model twice, and what named a falls back before the move points
    /// what named b at the new a.
    /// </summary>
    [Fact]
    public void Removing_a_model_and_shifting_the_others_onto_its_name_moves_each_entry_once()
    {
        var (document, set) = ThreeRungs();
        document.Rag.LlmProfile = "x-a";
        document.Compaction.Profile = "x-b";

        var upsert = set.UpsertConnection(Ladder(("a", "m-b"), ("b", "m-c")), previousPairNames: ["x.b", "x.c"]);
        Assert.Equal([("x.b", "x.a"), ("x.c", "x.b")], upsert.Renames);
        HostLlmProfiles.Mirror(document, set, upsert.Set, upsert.Renames);

        Assert.Equal(["x-a", "x-b"], document.Llm.Profiles.Ids.Order(StringComparer.Ordinal));
        Assert.Equal("m-b", document.Llm.Profiles.Get("x-a")?.Model);
        Assert.Equal(3, document.GetInt32("Llm:Profiles:x-a:MaxRetries"));
        Assert.Equal("m-c", document.Llm.Profiles.Get("x-b")?.Model);
        Assert.Equal(["x-a", "x-b"], document.Decision.RouteLadder);
        Assert.Null(document.Rag.LlmProfile);
        Assert.Equal("x-a", document.Compaction.Profile);
    }

    /// <summary>The single variant: removing a and renaming b to a moves b's entry onto the freed id.</summary>
    [Fact]
    public void Removing_a_model_and_renaming_another_onto_its_name_moves_the_entry_onto_the_freed_id()
    {
        var (document, set) = ThreeRungs();

        var upsert = set.UpsertConnection(Ladder(("a", "m-b"), ("c", "m-c")), previousPairNames: ["x.b", "x.c"]);
        Assert.Equal([("x.b", "x.a")], upsert.Renames);
        HostLlmProfiles.Mirror(document, set, upsert.Set, upsert.Renames);

        Assert.Equal(["x-a", "x-c"], document.Llm.Profiles.Ids.Order(StringComparer.Ordinal));
        Assert.Equal("m-b", document.Llm.Profiles.Get("x-a")?.Model);
        Assert.Equal(3, document.GetInt32("Llm:Profiles:x-a:MaxRetries"));
        Assert.Equal(["x-a", "x-c"], document.Decision.RouteLadder);
    }

    /// <summary>A chain a → b, b → c with no removal moves each entry once, never onto the end of the chain.</summary>
    [Fact]
    public void A_chain_of_renames_moves_each_entry_once()
    {
        var document = AppSettingsDocument.Parse("{}");
        var set = ModelProfileSet.Empty.UpsertConnection(Ladder(("a", "m-a"), ("b", "m-b"))).Set;
        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);
        document.Decision.RouteLadder = ["x-a", "x-b"];

        var upsert = set.UpsertConnection(Ladder(("b", "m-a"), ("c", "m-b")), previousPairNames: ["x.a", "x.b"]);
        HostLlmProfiles.Mirror(document, set, upsert.Set, upsert.Renames);

        Assert.Equal(["x-b", "x-c"], document.Llm.Profiles.Ids.Order(StringComparer.Ordinal));
        Assert.Equal("m-a", document.Llm.Profiles.Get("x-b")?.Model);
        Assert.Equal("m-b", document.Llm.Profiles.Get("x-c")?.Model);
        Assert.Equal(["x-b", "x-c"], document.Decision.RouteLadder);
    }

    private static ModelProfile Legacy(string name) => new()
    {
        Name = name,
        Provider = "deepseek",
        BaseUrl = "https://api.deepseek.com",
        Model = "deepseek-v4-flash",
        KeyEnvName = "DEEPSEEK_API_KEY",
    };

    /// <summary>What an older Studio wrote for a setting named « Auto », before <c>auto</c> was reserved.</summary>
    private const string LegacyAutoFile = """
        { "Llm": { "Model": "qwen3", "Profiles": { "auto": { "BaseUrl": "https://api.deepseek.com", "Model": "deepseek-v4-flash", "ApiKeyEnvVar": "DEEPSEEK_API_KEY", "MaxRetries": 2 } } } }
        """;

    /// <summary>
    /// The entry an older Studio wrote under <c>auto</c> follows its setting renamed to a free name,
    /// what Studio does not model with it: the engine refuses to start on <c>Llm:Profiles:auto</c>.
    /// </summary>
    [Fact]
    public void A_legacy_entry_under_auto_follows_its_setting_renamed_to_a_free_name()
    {
        var document = AppSettingsDocument.Parse(LegacyAutoFile);
        var set = ModelProfileSet.Empty.Upsert(Legacy("Auto"));
        Assert.Equal(HostProfileStatus.AutoName, HostLlmProfiles.Classify(set)["Auto"].Status);

        var renamed = set.Upsert(Legacy("Auto pick"), previousName: "Auto");
        Assert.True(HostLlmProfiles.Mirror(document, set, renamed, "Auto", "Auto pick"));

        Assert.Equal(["auto-pick"], document.Llm.Profiles.Ids);
        Assert.Equal(2, document.GetInt32("Llm:Profiles:auto-pick:MaxRetries"));
    }

    /// <summary>
    /// Left under its name, the setting is offered to no crew, and its legacy entry goes at the next
    /// change of the settings; an <c>auto</c> entry no setting answers to was written by hand, and stays.
    /// </summary>
    [Fact]
    public void A_legacy_entry_under_auto_goes_at_the_next_change_and_a_hand_written_one_stays()
    {
        var document = AppSettingsDocument.Parse(LegacyAutoFile);
        var set = ModelProfileSet.Empty.Upsert(Legacy("Auto"));

        HostLlmProfiles.Mirror(document, set, set.Upsert(Legacy("Other")));

        Assert.Equal(["other"], document.Llm.Profiles.Ids);

        var handWritten = AppSettingsDocument.Parse(LegacyAutoFile);
        var none = ModelProfileSet.Empty.Upsert(Legacy("Z"));
        HostLlmProfiles.Mirror(handWritten, none, none.Upsert(Legacy("Other")));

        Assert.Contains("auto", handWritten.Llm.Profiles.Ids);
    }

    /// <summary>
    /// The call mode a setting pins is written into its entry and, elected, into <c>Llm</c>, as a
    /// Studio launch lays it: a run outside Studio reads the same one.
    /// </summary>
    [Fact]
    public void The_call_mode_a_setting_pins_is_written_in_its_entry_and_in_llm_when_elected()
    {
        var document = AppSettingsDocument.Parse("{}");
        var profile = Legacy("Stream") with { CallMode = "Stream" };
        var set = ModelProfileSet.Empty.Upsert(profile);

        HostLlmProfiles.Mirror(document, ModelProfileSet.Empty, set);
        HostLlmProfiles.ElectDefault(document, profile);

        Assert.Equal("Stream", document.Llm.Profiles.Get("stream")?.CallMode);
        Assert.Equal("Stream", document.GetString("Llm:CallMode"));
        Assert.Equal("Stream", profile.HostEnvironment("stream", _ => null)["ORKEON_Llm__Profiles__stream__CallMode"]);
    }
}
