using System.Text.Json.Nodes;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Forge;

/// <summary>
/// A session written before <c>--crew-decision</c> holds no mode: the engine derives it from the
/// plan, and a session Studio reads off the disk shows the mode the engine will state on resume.
/// </summary>
public sealed class ForgeCrewDecisionDeriveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "studio-derive-" + Guid.NewGuid().ToString("N"));

    public ForgeCrewDecisionDeriveTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("""{"agents":[{"key":"a","tools":["file_read"]}],"tasks":[{"key":"t","agent":"a"}]}""", "none")]
    [InlineData("""{"agents":[{"key":"a"}],"tasks":[{"key":"d","decision":{"questions":{"q":{"choice":{"x":"X"}}}}}]}""", "required")]
    [InlineData("""{"agents":[{"key":"a","tools":["system_one_decide"]}],"tasks":[{"key":"t","agent":"a"}]}""", "required")]
    [InlineData("""{"agents":[{"key":"a"}],"tasks":[{"key":"t","agent":"a","tools":["system_one_decide"]}]}""", "required")]
    [InlineData("""{}""", "none")]
    public void The_mode_is_derived_from_the_plan_as_the_engine_derives_it(string blueprint, string expected)
    {
        Assert.Equal(expected, ForgeCrewDecisions.Derive(JsonNode.Parse(blueprint) as JsonObject));
    }

    [Fact]
    public void No_plan_derives_none()
    {
        Assert.Equal(ForgeCrewDecisions.None, ForgeCrewDecisions.Derive(null));
    }

    [Fact]
    public void A_session_without_a_mode_shows_the_one_its_plan_derives()
    {
        File.WriteAllText(Path.Combine(_directory, "session.json"), """{"slug":"veille","id":"s1","format":"yaml"}""");
        File.WriteAllText(Path.Combine(_directory, "blueprint.json"),
            """{"agents":[{"key":"a","role":"A","goal":"g"}],"tasks":[{"key":"d","description":"Trier","decision":{"questions":{"q":{"choice":{"x":"X"}}}}}]}""");

        var model = new ForgeSessionModel();
        ForgeSessionHydrator.Hydrate(model, _directory);

        Assert.Equal(ForgeCrewDecisions.Required, model.CrewDecision);
    }

    [Fact]
    public void A_session_that_states_its_mode_keeps_it()
    {
        File.WriteAllText(Path.Combine(_directory, "session.json"), """{"slug":"veille","id":"s1","format":"yaml","crewDecision":"optional"}""");
        File.WriteAllText(Path.Combine(_directory, "blueprint.json"),
            """{"agents":[{"key":"a","role":"A","goal":"g","tools":["system_one_decide"]}],"tasks":[]}""");

        var model = new ForgeSessionModel();
        ForgeSessionHydrator.Hydrate(model, _directory);

        Assert.Equal(ForgeCrewDecisions.Optional, model.CrewDecision);
    }

    /// <summary>The rule is the engine's: a change to its derivation fails here rather than show another mode than the resume states.</summary>
    [Fact]
    public void The_derivation_is_the_engines()
    {
        var forge = Path.Combine(OrkeonSources.Root(), "src", "scripting", "Orkeon.Scripting.Cli", "Commands", "Forge");
        var decision = File.ReadAllText(Path.Combine(forge, "ForgeCrewDecision.cs"));
        var session = File.ReadAllText(Path.Combine(forge, "ForgeSession.cs"));

        Assert.Contains("Document.CrewDecision ?? ForgeCrewDecision.Derive(TryLoadArtifact<ForgeBlueprint>(BlueprintFileName))", session, StringComparison.Ordinal);
        Assert.Contains("BlueprintFileName = \"blueprint.json\"", session, StringComparison.Ordinal);
        var start = decision.IndexOf("public static string Derive(ForgeBlueprint? blueprint)", StringComparison.Ordinal);
        Assert.True(start >= 0, "ForgeCrewDecision.Derive not found");
        var derive = decision[start..decision.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
        Assert.Contains("if (blueprint is null)\n            return None;", derive, StringComparison.Ordinal);
        Assert.Contains("(blueprint.Tasks ?? []).Any(static t => t.Decision is not null || HoldsDecideTool(t.Tools))", derive, StringComparison.Ordinal);
        Assert.Contains("|| (blueprint.Agents ?? []).Any(static a => HoldsDecideTool(a.Tools))", derive, StringComparison.Ordinal);
        Assert.Contains("return decides ? Required : None;", derive, StringComparison.Ordinal);
        Assert.Contains("tools?.Contains(DecideTool, StringComparer.Ordinal)", decision, StringComparison.Ordinal);
    }
}
