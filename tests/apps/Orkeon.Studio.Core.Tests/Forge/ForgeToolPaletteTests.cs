using System.Text.RegularExpressions;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Tools;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Forge;

/// <summary>
/// The tools an editor of a forged blueprint offers: the team's first, then the catalogue a forged
/// crew may draw from, which leaves out what the engine denies and what it would not know.
/// </summary>
public sealed partial class ForgeToolPaletteTests
{
    private const string Blueprint =
        """{"agents":[{"key":"a","role":"A","goal":"g","tools":["file_read","shell_command"]}],"tasks":[{"key":"t","description":"d","agent":"a","tools":["pdf_reader"]}]}""";

    [Fact]
    public void The_team_tools_are_its_agents_and_its_tasks_sorted()
    {
        Assert.Equal(["file_read", "pdf_reader", "shell_command"], ForgeToolPalette.For(Blueprint).TeamTools);
    }

    [Fact]
    public void The_rest_of_the_catalogue_leaves_out_the_team_tools_the_denied_and_the_conditional()
    {
        var offered = ForgeToolPalette.For(Blueprint).Families.SelectMany(family => family.Tools).ToList();

        Assert.Contains("file_write", offered);
        Assert.DoesNotContain("file_read", offered);
        Assert.DoesNotContain("pdf_reader", offered);
        Assert.All(ForgeToolPalette.DeniedTools, denied => Assert.DoesNotContain(denied, offered));
        Assert.All(
            ToolCatalog.All.Where(tool => tool.Requirement is ToolRequirement.ScriptOnly or ToolRequirement.OnlyWithStoredKey
                or ToolRequirement.OnlyWithMcpServer or ToolRequirement.OnlyWithDecisionProvider or ToolRequirement.OnlyWithCognitiveMemory),
            tool => Assert.DoesNotContain(tool.Name, offered));
        Assert.Equal(offered.Count, offered.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_denied_tool_the_blueprint_names_stays_offered()
    {
        Assert.Contains("shell_command", ForgeToolPalette.For(Blueprint).TeamTools);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    public void No_blueprint_offers_the_catalogue_alone(string? json)
    {
        var palette = ForgeToolPalette.For(json);

        Assert.Empty(palette.TeamTools);
        Assert.NotEmpty(palette.Families);
    }

    /// <summary>The denied set is the engine's: a tool it adds or frees fails here rather than be offered and refused.</summary>
    [Fact]
    public void The_denied_tools_are_the_engines()
    {
        var sandbox = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src", "scripting", "Orkeon.Scripting.Cli", "Commands", "Forge", "ForgeSandbox.cs"));
        var start = sandbox.IndexOf("DeniedTools =", StringComparison.Ordinal);
        Assert.True(start >= 0, "ForgeSandbox.DeniedTools not found");
        var block = sandbox[start..sandbox.IndexOf("};", start, StringComparison.Ordinal)];

        var engine = QuotedName().Matches(block).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal);

        Assert.Equal(engine, ForgeToolPalette.DeniedTools.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex("\"([a-z0-9_]+)\"")]
    private static partial Regex QuotedName();
}
