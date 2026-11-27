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
        Assert.Contains("web_search", offered);
        // The team's own tools are listed apart.
        Assert.DoesNotContain("file_read", offered);
        Assert.DoesNotContain("pdf_reader", offered);
        // Denied by the forge's sandbox.
        Assert.DoesNotContain("shell_command", offered);
        Assert.DoesNotContain("drive_read", offered);
        // Script-only, or registered only once a key, an MCP server, a decision provider or the cognitive memory is set.
        Assert.DoesNotContain("spawn_agent", offered);
        Assert.DoesNotContain("brave_search", offered);
        Assert.DoesNotContain("system_one_decide", offered);
        // Held by orkeon run, never by the forge's host: the forge refuses them as unknown.
        Assert.DoesNotContain("human_input", offered);
        Assert.DoesNotContain("semantic_search", offered);
        Assert.DoesNotContain("ask_question_to_coworker", offered);
        Assert.DoesNotContain("delegate_work_to_coworker", offered);
        Assert.DoesNotContain("chat_attach", offered);
        Assert.Equal(offered.Count, offered.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_tool_that_works_only_in_a_conversation_is_never_offered()
    {
        var offered = ForgeToolPalette.For(Blueprint).Families.SelectMany(family => family.Tools).ToHashSet(StringComparer.Ordinal);
        var conversational = ToolCatalog.All.Where(t => t.Requirement == ToolRequirement.OnlyInAConversation).Select(t => t.Name).ToList();

        Assert.NotEmpty(conversational);
        Assert.All(conversational, name => Assert.DoesNotContain(name, offered));
    }

    [Fact]
    public void A_tool_outside_the_forge_host_the_blueprint_names_stays_a_team_tool()
    {
        const string blueprint =
            """{"agents":[{"key":"a","role":"A","goal":"g","tools":["human_input","system_one_decide"]}],"tasks":[]}""";

        var palette = ForgeToolPalette.For(blueprint);

        Assert.Equal(["human_input", "system_one_decide"], palette.TeamTools);
        Assert.DoesNotContain("human_input", palette.Families.SelectMany(family => family.Tools));
    }

    [Fact]
    public void Every_tool_outside_the_forge_host_is_in_the_catalogue()
    {
        var catalogue = ToolCatalog.All.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(ForgeToolPalette.OutsideTheForgeHost, name => Assert.Contains(name, catalogue));
    }

    /// <summary>
    /// The tools outside the forge's host are the engine's facts: the extensions <c>orkeon run</c>
    /// composes its host with and the forge's host leaves out (<c>RunnerHost.Build</c>, then
    /// <c>ForgeCommand.AddEngineServices</c> and <c>RunCommand.AddCliRagServices</c>) are exactly
    /// <c>AddOrkeonChatAttach</c>, <c>AddOrkeonHumanInput</c> and <c>AddSemanticSearchTool</c>, each
    /// registering one tool, and the
    /// coworker tools are built per agent, never registered. A registration either host gains or
    /// loses fails here rather than be offered and refused, or withheld for nothing.
    /// </summary>
    [Fact]
    public void The_tools_outside_the_forge_host_are_the_ones_its_composition_leaves_out()
    {
        var root = OrkeonSources.Root();
        var hosting = Path.Combine(root, "src", "hosting", "Orkeon.Hosting");
        var cli = Path.Combine(root, "src", "scripting", "Orkeon.Scripting.Cli", "Commands");
        var infrastructure = Path.Combine(root, "src", "core", "Orkeon.Infrastructure");
        var runCommand = File.ReadAllText(Path.Combine(cli, "RunCommand.cs"));
        var forgeCommand = File.ReadAllText(Path.Combine(cli, "Forge", "ForgeCommand.cs"));

        var runSources = Directory.GetFiles(hosting, "RunnerExecution*.cs").Select(File.ReadAllText).Append(runCommand).ToList();
        var forgeSources = new[]
        {
            File.ReadAllText(Path.Combine(hosting, "RunnerHost.cs")),
            Member(forgeCommand, "static Microsoft.Extensions.Hosting.IHost BuildEngineHost("),
            Member(forgeCommand, "static void AddEngineServices("),
            Member(runCommand, "static void AddCliRagServices("),
        };
        Assert.Contains("AddEngineServices(services,", forgeSources[1], StringComparison.Ordinal);
        Assert.Contains("AddCliRagServices(services)", forgeSources[2], StringComparison.Ordinal);

        var runOnly = Extensions(runSources).Except(Extensions(forgeSources), StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal(["AddOrkeonChatAttach", "AddOrkeonHumanInput", "AddSemanticSearchTool"], runOnly);
        Assert.All(runSources, source => Assert.DoesNotMatch(DirectToolRegistration(), source));

        var humanInput = File.ReadAllText(Path.Combine(infrastructure, "DependencyInjection", "HumanInputExtensions.cs"));
        var semanticSearch = File.ReadAllText(Path.Combine(hosting, "SemanticSearchToolExtensions.cs"));
        var chatAttach = File.ReadAllText(Path.Combine(hosting, "Conversations", "ChatAttachTool.cs"));
        Assert.Equal(["HumanInputTool"], RegisteredTools(humanInput).Distinct(StringComparer.Ordinal));
        Assert.Equal(["SearchTool"], RegisteredTools(semanticSearch));
        Assert.Equal(["ChatAttachTool"], RegisteredTools(chatAttach));
        var registered = new[]
        {
            ContractName(Path.Combine(infrastructure, "Tools", "HumanInput", "HumanInputTool.cs")),
            ContractName(Path.Combine(infrastructure, "Tools", "Search", "SearchTool.cs")),
            ContractName(Path.Combine(hosting, "Conversations", "ChatAttachTool.cs")),
        };

        var delegation = File.ReadAllText(Path.Combine(infrastructure, "Agent", "AgentDelegationToolsProvider.cs"));
        Assert.Contains("if (!agent.AllowDelegation)", delegation, StringComparison.Ordinal);
        Assert.Contains("new DelegateWorkTool(", delegation, StringComparison.Ordinal);
        Assert.Contains("new AskQuestionTool(", delegation, StringComparison.Ordinal);
        var coworker = new[]
        {
            ContractName(Path.Combine(infrastructure, "Tools", "DelegateWorkTool.cs")),
            ContractName(Path.Combine(infrastructure, "Tools", "AskQuestionTool.cs")),
        };
        var separator = Path.DirectorySeparatorChar;
        var anywhere = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
        Assert.All(anywhere, source =>
        {
            Assert.DoesNotContain("IBaseTool, DelegateWorkTool>", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IBaseTool, AskQuestionTool>", source, StringComparison.Ordinal);
        });

        Assert.Equal(
            registered.Concat(coworker).Order(StringComparer.Ordinal),
            ForgeToolPalette.OutsideTheForgeHost.Order(StringComparer.Ordinal));
    }

    /// <summary>The member that starts with <paramref name="signature"/>, up to its closing brace at four spaces.</summary>
    private static string Member(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>The named service extensions the sources call, the generic registrations aside.</summary>
    private static HashSet<string> Extensions(IEnumerable<string> sources) =>
        [.. sources.SelectMany(source => ExtensionCall().Matches(source))
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Where(name => name is not ("AddSingleton" or "AddScoped" or "AddTransient"))];

    private static IEnumerable<string> RegisteredTools(string source) =>
        BaseToolRegistration().Matches(source).Select(match => match.Groups[1].Value);

    private static string ContractName(string path)
    {
        var match = ToolContract().Match(File.ReadAllText(path));
        Assert.True(match.Success, "No [ToolContract] in " + path);
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"services\.(Add[A-Za-z]+)\(|\b(Add[A-Za-z]+)\(services\b")]
    private static partial Regex ExtensionCall();

    [GeneratedRegex(@"(?:Add|TryAdd|ServiceDescriptor\.)\w*<IBaseTool\b")]
    private static partial Regex DirectToolRegistration();

    [GeneratedRegex(@"<IBaseTool, ([A-Za-z]+)>")]
    private static partial Regex BaseToolRegistration();

    [GeneratedRegex(@"\[ToolContract\(""([a-z0-9_]+)""")]
    private static partial Regex ToolContract();

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
