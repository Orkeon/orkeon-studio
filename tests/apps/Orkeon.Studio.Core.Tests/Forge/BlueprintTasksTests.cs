using System.Text.Json.Nodes;
using Orkeon.Studio.Core.Forge;

namespace Orkeon.Studio.Core.Tests.Forge;

/// <summary>
/// The tasks of a forged blueprint, read and amended in JSON: every field the editors do not know
/// travels untouched, and nothing a condition reads is erased in silence.
/// </summary>
public sealed class BlueprintTasksTests
{
    private const string Blueprint = """
        {"crew":{"name":"rapport"},"process":"sequential",
         "agents":[{"key":"lecteur","role":"Lecteur","goal":"Lire"},{"key":"redacteur","goal":"Écrire"}],
         "tasks":[
           {"key":"lire","description":"Lire les documents","expectedOutput":"Une synthèse","agent":"lecteur","tools":["pdf_reader"],"x-extra":42},
           {"key":"controle","description":"Contrôler la couverture","dependencies":["lire"],"decision":{"questions":{"couverture":{"options":["complet","partiel"]}}}},
           {"key":"rediger","description":"Rédiger le rapport","expectedOutput":"Un rapport","agent":"redacteur","dependencies":["lire","controle"],"deliverable":"/output/rapport.docx","when":{"controle.couverture":["complet"]}},
           {"description":"sans clé"}
         ]}
        """;

    [Fact]
    public void Read_lists_the_tasks_with_their_fields_in_order_and_skips_one_without_a_key()
    {
        var tasks = BlueprintTasks.Read(Blueprint);

        Assert.Equal(["lire", "controle", "rediger"], tasks.Select(task => task.Key));
        var lire = tasks[0];
        Assert.Equal("Lire les documents", lire.Description);
        Assert.Equal("Une synthèse", lire.ExpectedOutput);
        Assert.Equal("lecteur", lire.AgentKey);
        Assert.Equal(["pdf_reader"], lire.Tools);
        Assert.False(lire.IsDecision);
        Assert.Null(lire.When);

        var controle = tasks[1];
        Assert.True(controle.IsDecision);
        Assert.Null(controle.AgentKey);
        Assert.Equal(["couverture"], controle.Questions);

        var rediger = tasks[2];
        Assert.Equal(["lire", "controle"], rediger.Dependencies);
        Assert.Equal("/output/rapport.docx", rediger.Deliverable);
        Assert.NotNull(rediger.When);
        Assert.Equal([("controle.couverture", "complet")], rediger.WhenTerms);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void An_unreadable_blueprint_has_no_tasks_and_amends_to_null(string? json)
    {
        Assert.Empty(BlueprintTasks.Read(json));
        Assert.Null(BlueprintTasks.Remove(json, "lire"));
        Assert.Null(BlueprintTasks.Move(json, "lire", 1));
        Assert.Null(BlueprintTasks.Upsert(json, new BlueprintTaskEdit(null, "d", "e", "a", [], null, [])));
    }

    [Fact]
    public void Agents_show_their_role_or_their_key()
    {
        Assert.Equal(
            [new BlueprintAgentRef("lecteur", "Lecteur"), new BlueprintAgentRef("redacteur", "redacteur")],
            BlueprintTasks.Agents(Blueprint));
    }

    [Fact]
    public void Upsert_rewrites_its_fields_and_keeps_the_condition_and_unknown_fields()
    {
        var json = BlueprintTasks.Upsert(Blueprint, new BlueprintTaskEdit(
            "rediger", "  Rédiger la note  ", "Une note", "lecteur", ["lire"], " ", []));

        var task = TaskNode(json, "rediger");
        Assert.Equal("Rédiger la note", (string?)task["description"]);
        Assert.Equal("Une note", (string?)task["expectedOutput"]);
        Assert.Equal("lecteur", (string?)task["agent"]);
        Assert.Equal(["lire"], task["dependencies"]!.AsArray().Select(node => (string?)node));
        Assert.False(task.ContainsKey("deliverable"));
        Assert.False(task.ContainsKey("tools"));
        Assert.Equal("""{"controle.couverture":["complet"]}""", task["when"]!.ToJsonString());

        var lire = BlueprintTasks.Upsert(json, new BlueprintTaskEdit("lire", "Lire", "Une synthèse", "lecteur", [], null, ["pdf_reader", "file_read"]));
        Assert.Equal(42, (int?)TaskNode(lire, "lire")["x-extra"]);
        Assert.Equal("sequential", (string?)JsonNode.Parse(lire!)!["process"]);
    }

    [Fact]
    public void Upsert_appends_a_new_task_under_a_unique_key_from_its_description()
    {
        var once = BlueprintTasks.Upsert(Blueprint, new BlueprintTaskEdit(null, "Lire les documents encore une fois", "x", "lecteur", [], "/output/a.md", []));
        var twice = BlueprintTasks.Upsert(once, new BlueprintTaskEdit(null, "Lire les documents encore", "y", "lecteur", [], null, []));

        var keys = BlueprintTasks.Read(twice).Select(task => task.Key).ToList();
        Assert.Equal(5, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.StartsWith(keys[3], keys[4], StringComparison.Ordinal);
        Assert.EndsWith("-2", keys[4], StringComparison.Ordinal);
        Assert.Equal("/output/a.md", BlueprintTasks.Read(twice)[3].Deliverable);
    }

    [Fact]
    public void A_blank_description_takes_the_placeholder_key()
    {
        Assert.Equal("task", BlueprintTasks.NewKey(Blueprint, "  "));
    }

    [Fact]
    public void Remove_drops_the_task_and_the_others_stop_waiting_for_it()
    {
        var json = BlueprintTasks.Remove(Blueprint, "lire");

        var tasks = BlueprintTasks.Read(json);
        Assert.Equal(["controle", "rediger"], tasks.Select(task => task.Key));
        Assert.False(TaskNode(json, "controle").ContainsKey("dependencies"));
        Assert.Equal(["controle"], tasks[1].Dependencies);
    }

    [Fact]
    public void A_task_a_condition_reads_is_named_as_a_blocker()
    {
        Assert.Equal(["rediger"], BlueprintTasks.ConditionedOn(Blueprint, "controle"));
        Assert.Empty(BlueprintTasks.ConditionedOn(Blueprint, "lire"));
        // A key that only prefixes another is no blocker.
        Assert.Empty(BlueprintTasks.ConditionedOn(Blueprint, "contro"));
    }

    [Fact]
    public void A_condition_on_the_whole_task_is_a_blocker_too()
    {
        const string json = """{"tasks":[{"key":"d"},{"key":"x","when":{"d":"otherwise"}}]}""";

        Assert.Equal(["x"], BlueprintTasks.ConditionedOn(json, "d"));
        Assert.Equal([("d", "otherwise")], BlueprintTasks.Read(json)[1].WhenTerms);
    }

    [Fact]
    public void Move_shifts_a_task_and_stays_within_the_list()
    {
        Assert.Equal(["controle", "lire", "rediger"], BlueprintTasks.Read(BlueprintTasks.Move(Blueprint, "lire", 1)).Select(task => task.Key));
        Assert.Equal(["lire", "rediger", "controle"], BlueprintTasks.Read(BlueprintTasks.Move(Blueprint, "rediger", -1)).Select(task => task.Key));
        Assert.Equal(["lire", "controle", "rediger"], BlueprintTasks.Read(BlueprintTasks.Move(Blueprint, "lire", -1)).Select(task => task.Key));
    }

    private static JsonObject TaskNode(string? json, string key) =>
        JsonNode.Parse(json!)!["tasks"]!.AsArray().OfType<JsonObject>().Single(task => (string?)task["key"] == key);
}
