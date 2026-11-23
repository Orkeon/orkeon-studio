using System.Text.Json;
using Orkeon.Studio.Core.Profiles;

namespace Orkeon.Studio.Core.Tests.Profiles;

/// <summary>
/// A connection declares one or more models; each (connection, model) pair is the setting teams and
/// elections name, « connection.alias », and a connection of one model without alias keeps the name and
/// the file form a setting had before connections.
/// </summary>
public sealed class ModelConnectionTests
{
    private static ModelConnection DeepSeek(string name = "mydeepseek", params (string Alias, string Model)[] models) => new()
    {
        Name = name,
        Provider = "deepseek",
        BaseUrl = "https://api.deepseek.com/v1",
        KeyEnvName = "DEEPSEEK_API_KEY",
        TimeoutSeconds = 120,
        ThinkingEnabled = true,
        Models = [.. (models.Length > 0 ? models : [("flash", "deepseek-v4-flash"), ("pro", "deepseek-v4-pro")])
            .Select(model => new ConnectionModel { Alias = model.Alias, Model = model.Model })],
    };

    [Fact]
    public void Each_model_is_a_setting_named_connection_dot_alias_that_inherits_the_connection()
    {
        var connection = DeepSeek() with
        {
            Models =
            [
                new ConnectionModel { Alias = "flash", Model = "deepseek-v4-flash", ThinkingEnabled = false },
                new ConnectionModel { Alias = " pro ", Model = "deepseek-v4-pro", MaxTokens = 64000 },
            ],
        };

        var pairs = connection.Flatten();

        Assert.Equal(["mydeepseek.flash", "mydeepseek.pro"], pairs.Select(pair => pair.Name));
        Assert.All(pairs, pair =>
        {
            Assert.Equal("mydeepseek", pair.Connection);
            Assert.Equal("https://api.deepseek.com/v1", pair.BaseUrl);
            Assert.Equal("DEEPSEEK_API_KEY", pair.KeyEnvName);
            Assert.Equal(120, pair.TimeoutSeconds);
        });
        Assert.Equal("flash", pairs[0].Alias);
        Assert.False(pairs[0].ThinkingEnabled);
        Assert.True(pairs[1].ThinkingEnabled);
        Assert.Null(pairs[0].MaxTokens);
        Assert.Equal(64000, pairs[1].MaxTokens);
        Assert.Equal(["mydeepseek-flash", "mydeepseek-pro"], pairs.Select(pair => pair.HostProfileId));
    }

    [Fact]
    public void A_connection_in_the_historical_form_is_one_setting_bearing_its_name()
    {
        var connection = new ModelConnection { Name = "Claude", Provider = "anthropic", Model = "claude-opus-5-5" };

        var pair = Assert.Single(connection.Flatten());

        Assert.Equal("Claude", pair.Name);
        Assert.Null(pair.Alias);
        Assert.Equal("claude-opus-5-5", pair.Model);
        Assert.Equal("claude", pair.HostProfileId);
        Assert.Equal("Claude", ModelConnection.PairName("Claude", "  "));
        Assert.Equal("Claude.opus", ModelConnection.PairName("Claude", "opus"));
    }

    [Fact]
    public void One_model_without_alias_nor_setting_of_its_own_is_written_in_the_historical_form()
    {
        var connection = new ModelConnection { Name = "Local", Models = [new ConnectionModel { Model = "qwen2.5:14b" }] };

        var json = JsonSerializer.Serialize(ModelProfileSet.Empty.UpsertConnection(connection).Set);

        Assert.DoesNotContain("\"Models\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Model\":\"qwen2.5:14b\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_a_studio_before_connections_wrote_reads_as_connections_of_one_model()
    {
        const string json = """
            {
              "Profiles": [
                { "Name": "Kimi", "Provider": "kimi", "Model": "kimi-k3", "Temperature": 1 },
                { "Name": "Local", "Provider": "ollama", "Model": "qwen2.5:14b", "BaseUrl": "http://localhost:11434/v1" }
              ],
              "DefaultProfile": "Local",
              "StudioProfile": "Kimi"
            }
            """;

        var set = JsonSerializer.Deserialize<ModelProfileSet>(json)!;

        Assert.Equal(["Kimi", "Local"], set.Connections.Select(connection => connection.Name));
        Assert.All(set.Connections, connection => Assert.Null(connection.Models));
        Assert.Equal(["Kimi", "Local"], set.Profiles.Select(profile => profile.Name));
        Assert.Equal("kimi-k3", set.Studio?.Model);
        Assert.Equal("qwen2.5:14b", set.Default?.Model);
    }

    [Fact]
    public async Task A_connection_of_several_models_round_trips_through_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orkeon-connections-{Guid.NewGuid():N}.json");
        try
        {
            var connection = DeepSeek() with
            {
                Models =
                [
                    new ConnectionModel { Alias = "flash", Model = "deepseek-v4-flash", Temperature = 0.2 },
                    new ConnectionModel { Alias = "pro", Model = "deepseek-v4-pro" },
                ],
            };
            var set = ModelProfileSet.Empty.UpsertConnection(connection).Set.WithStudio("mydeepseek.pro");
            var store = new ModelProfileFileStore(path);

            await store.SaveAsync(set, TestContext.Current.CancellationToken);
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

            Assert.False(loaded.Failed);
            var read = Assert.Single(loaded.Set.Connections);
            Assert.Null(read.Model);
            Assert.Equal(["flash", "pro"], read.DeclaredModels.Select(model => model.Alias));
            Assert.Equal(0.2, loaded.Set.Find("mydeepseek.flash")?.Temperature);
            Assert.Null(loaded.Set.Find("mydeepseek.pro")?.Temperature);
            Assert.Equal("mydeepseek.flash", loaded.Set.DefaultProfile);
            Assert.Equal("mydeepseek.pro", loaded.Set.StudioProfile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_temperature_a_model_pins_out_of_range_of_json_numbers_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orkeon-connections-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                { "Profiles": [ { "Name": "c", "Models": [ { "Alias": "a", "Model": "m", "Temperature": 1e400 }, null ] }, null ] }
                """, TestContext.Current.CancellationToken);

            var loaded = await new ModelProfileFileStore(path).LoadAsync(TestContext.Current.CancellationToken);

            Assert.True(loaded.Failed);
            Assert.Contains("'c.a'", loaded.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Renaming_a_connection_renames_each_pair_and_the_elections_follow()
    {
        var set = ModelProfileSet.Empty.UpsertConnection(DeepSeek()).Set.WithDefault("mydeepseek.pro").WithStudio("mydeepseek.flash");

        var upsert = set.UpsertConnection(DeepSeek("ds"), previousName: "mydeepseek");

        Assert.Equal([("mydeepseek.flash", "ds.flash"), ("mydeepseek.pro", "ds.pro")], upsert.Renames);
        Assert.Empty(upsert.Removed);
        Assert.Equal(["ds.flash", "ds.pro"], upsert.Set.Profiles.Select(profile => profile.Name));
        Assert.Equal("ds.pro", upsert.Set.DefaultProfile);
        Assert.Equal("ds.flash", upsert.Set.StudioProfile);
    }

    [Fact]
    public void A_renamed_alias_given_by_its_previous_pair_is_a_rename_and_a_dropped_model_is_removed()
    {
        var set = ModelProfileSet.Empty.UpsertConnection(DeepSeek()).Set.WithDefault("mydeepseek.pro").WithStudio("mydeepseek.flash");

        var upsert = set.UpsertConnection(
            DeepSeek(models: [("fast", "deepseek-v4-flash"), ("chat", "deepseek-v4-chat")]),
            previousPairNames: ["mydeepseek.flash", null]);

        Assert.Equal([("mydeepseek.flash", "mydeepseek.fast")], upsert.Renames);
        Assert.Equal(["mydeepseek.pro"], upsert.Removed);
        Assert.Equal("mydeepseek.fast", upsert.Set.StudioProfile);
        // The default's pair is gone: it falls back to the first remaining one.
        Assert.Equal("mydeepseek.fast", upsert.Set.DefaultProfile);
    }

    [Fact]
    public void Without_the_previous_pairs_models_are_matched_by_alias()
    {
        var set = ModelProfileSet.Empty.UpsertConnection(DeepSeek()).Set;

        var upsert = set.UpsertConnection(DeepSeek("ds", ("pro", "deepseek-v4-pro"), ("lite", "deepseek-lite")), previousName: "mydeepseek");

        Assert.Equal([("mydeepseek.pro", "ds.pro")], upsert.Renames);
        Assert.Equal(["mydeepseek.flash"], upsert.Removed);
    }

    [Fact]
    public void A_setting_that_gains_a_second_model_keeps_its_first_as_a_rename()
    {
        var set = ModelProfileSet.Empty.Upsert(new ModelProfile { Name = "Kimi", Model = "kimi-k3" }).WithStudio("Kimi");

        var upsert = set.UpsertConnection(
            new ModelConnection
            {
                Name = "Kimi",
                Models = [new ConnectionModel { Alias = "k3", Model = "kimi-k3" }, new ConnectionModel { Alias = "k2", Model = "kimi-k2.6" }],
            },
            previousPairNames: ["Kimi", null]);

        Assert.Equal([("Kimi", "Kimi.k3")], upsert.Renames);
        Assert.Equal("Kimi.k3", upsert.Set.DefaultProfile);
        Assert.Equal("Kimi.k3", upsert.Set.StudioProfile);
    }

    [Fact]
    public void A_pair_of_another_connection_bearing_a_new_pairs_name_is_absorbed()
    {
        var set = ModelProfileSet.Empty
            .Upsert(new ModelProfile { Name = "mydeepseek.flash", Model = "old" })
            .Upsert(new ModelProfile { Name = "Local", Model = "qwen" });

        var upsert = set.UpsertConnection(DeepSeek());

        Assert.Equal(["Local", "mydeepseek.flash", "mydeepseek.pro"], upsert.Set.Profiles.Select(profile => profile.Name));
        Assert.Equal("deepseek-v4-flash", upsert.Set.Find("mydeepseek.flash")?.Model);
        Assert.Empty(upsert.Removed);
    }

    [Fact]
    public void Removing_a_pair_keeps_its_connection_and_removing_the_last_removes_it()
    {
        var set = ModelProfileSet.Empty.UpsertConnection(DeepSeek()).Set.WithStudio("mydeepseek.flash");

        var one = set.Remove("mydeepseek.flash");

        var connection = Assert.Single(one.Connections);
        Assert.Equal(["mydeepseek.pro"], one.Profiles.Select(profile => profile.Name));
        Assert.Equal("pro", Assert.Single(connection.Models!).Alias);
        Assert.Null(one.StudioProfile);
        Assert.Equal("mydeepseek.pro", one.DefaultProfile);
        Assert.Empty(one.Remove("mydeepseek.pro").Connections);
    }

    [Fact]
    public void Removing_a_connection_removes_every_pair_and_clears_what_named_them()
    {
        var set = ModelProfileSet.Empty
            .Upsert(new ModelProfile { Name = "Local", Model = "qwen" })
            .UpsertConnection(DeepSeek()).Set
            .WithDefault("mydeepseek.pro")
            .WithStudio("mydeepseek.flash");

        var removed = set.RemoveConnection("mydeepseek");

        Assert.Equal(["Local"], removed.Profiles.Select(profile => profile.Name));
        Assert.Equal("Local", removed.DefaultProfile);
        Assert.Null(removed.StudioProfile);
    }

    [Fact]
    public void A_previous_pair_list_of_the_wrong_length_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ModelProfileSet.Empty.UpsertConnection(DeepSeek(), previousPairNames: [null]));
    }
}
