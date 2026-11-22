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
}
