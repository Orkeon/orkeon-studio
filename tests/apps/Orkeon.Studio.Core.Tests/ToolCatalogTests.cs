using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Orkeon.Domain.Attributes;
using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Tools;
using Orkeon.Tools.Drive.DependencyInjection;
using Orkeon.Tools.Email.DependencyInjection;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// STUDIO-21 — the tool catalogue the Settings screen lists is pinned against the documented
/// inventory: every name it carries is a real registry name, and its families carry exactly the
/// tools the <c>orkeon run</c> column of the availability matrix says a run exposes, those a run
/// registers only under a setting of their own included.
/// </summary>
public sealed partial class ToolCatalogTests
{
    /// <summary>
    /// The <c>orkeon run</c> column, counted by hand from the matrix: Web core 6 (firefox joined), search 8
    /// (web_search, brave_search, cache_search, semantic_search, local_embed_text and the three RAG tools),
    /// files 8, data 22, e-mail 13 (the twelve account tools and email_parser, which left the files
    /// family), drive 11, shell_command, session 7 (the six session tools and memory_recall), EventHub 7,
    /// Analysis 29, collaboration 4 (human_input, the two per-agent coworker tools, spawn_agent),
    /// system_one_decide, mcp_prompt_get, list_mounts.
    /// </summary>
    private const int ToolsARunExposes = 119;

    /// <summary>The one e-mail tool that works on a file rather than on an account.</summary>
    private const string EmailParser = "email_parser";

    // The package cell may carry a note after the code span (« `Orkeon.Hosting` (opt-in) »).
    [GeneratedRegex(@"^\| `([a-z0-9_]+)` \| `[A-Za-z]+` \| `Orkeon\.[A-Za-z.]+`[^|]*\|", RegexOptions.Multiline)]
    private static partial Regex InventoryRowPattern();


    private static HashSet<string> DocumentedNames()
    {
        var inventory = File.ReadAllText(Path.Combine(OrkeonSources.Root(), "docs", "tools", "inventory.md"));
        var table = inventory[inventory.IndexOf("### Names to use in YAML", StringComparison.Ordinal)..];
        return [.. InventoryRowPattern().Matches(table).Select(m => m.Groups[1].Value)];
    }

    /// <summary>
    /// The other direction: every documented registry name is in the catalogue but the four the
    /// matrix keeps out of a run. The matrix names the members of a suite row (Data, Files...) only
    /// by count; this list does not, so a tool a suite gains fails here.
    /// </summary>
    [Fact]
    public void Every_documented_registry_name_is_in_the_catalogue_but_the_four_a_run_never_registers()
    {
        var catalogue = ToolCatalog.All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var outside = DocumentedNames().Where(name => !catalogue.Contains(name)).Order(StringComparer.Ordinal);

        Assert.Equal(["code_interpreter", "progress_report", "slack_read_messages", "slack_send_message"], outside);
    }

    [Fact]
    public void Every_catalogue_name_is_a_documented_registry_name()
    {
        var documented = DocumentedNames();
        Assert.NotEmpty(documented);

        var unknown = ToolCatalog.All.Select(t => t.Name).Where(n => !documented.Contains(n)).ToList();

        Assert.True(unknown.Count == 0, "Not in docs/tools/inventory.md: " + string.Join(", ", unknown));
    }

    [Fact]
    [Trait("BR", "RT-35")]
    [Trait("BR", "RT-32")]
    [Trait("BR", "RT-42")]
    public void The_catalogue_lists_exactly_the_tools_a_run_exposes_once_each()
    {
        var names = ToolCatalog.All.Select(t => t.Name).ToList();

        Assert.Equal(ToolsARunExposes, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        // The tools the matrix keeps out of a run never appear: Slack, the code interpreter
        // (concrete type only), progress_report (the REPL's). RAG is in every run.
        Assert.Contains("structural_rewrite", names);
        Assert.Contains("rag_search", names);
        Assert.DoesNotContain("slack_send_message", names);
        Assert.DoesNotContain("code_interpreter", names);
        Assert.DoesNotContain("progress_report", names);
    }

    // A row of the availability matrix: its first cell, then the start of the orkeon run cell.
    [GeneratedRegex(@"^\| ([^|]+) \| ([^|]+) \|[^\n]*$", RegexOptions.Multiline)]
    private static partial Regex MatrixRowPattern();

    [GeneratedRegex(@"`([a-z][a-z0-9_]*)`")]
    private static partial Regex ToolNamePattern();

    /// <summary>The availability matrix of the inventory, up to the next section.</summary>
    private static string AvailabilityMatrix()
    {
        var inventory = File.ReadAllText(Path.Combine(OrkeonSources.Root(), "docs", "tools", "inventory.md"));
        var start = inventory.IndexOf("## Availability by composition root", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/tools/inventory.md has no availability matrix.");
        var matrix = inventory[start..];
        return matrix[..matrix.IndexOf("\n## ", 1, StringComparison.Ordinal)];
    }

    /// <summary>
    /// Every tool the <c>orkeon run</c> column of the availability matrix names as registered
    /// (a row that starts with a tick, a conditional one included) is in the families: a name
    /// missing from them would count as a tool Orkeon does not ship, one that may write any file,
    /// in the pre-launch deliverable check.
    /// </summary>
    [Fact]
    public void Every_tool_a_run_registers_is_in_a_family()
    {
        var matrix = AvailabilityMatrix();

        var registered = MatrixRowPattern().Matches(matrix)
            .Where(row => row.Groups[2].Value.TrimStart().StartsWith('\u2705'))
            .SelectMany(row => ToolNamePattern().Matches(row.Groups[1].Value).Select(m => m.Groups[1].Value))
            .ToList();
        Assert.Contains("rag_search", registered);
        Assert.Contains("memory_recall", registered);

        var known = ToolCatalog.All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var missing = registered.Where(name => !known.Contains(name)).ToList();
        Assert.True(missing.Count == 0, "Registered by orkeon run, unknown to Studio: " + string.Join(", ", missing));
    }

    /// <summary>
    /// A tool a run registers only under a setting of its own says which: the matrix row of the
    /// tool names the setting the catalogue's line names, so a condition the engine moves fails here.
    /// </summary>
    [Theory]
    [InlineData("system_one_decide", ToolRequirement.OnlyWithDecisionProvider, "Orkeon:Decision:Provider", "Orkeon:Decision:Provider")]
    [InlineData("memory_recall", ToolRequirement.OnlyWithCognitiveMemory, "Orkeon:CognitiveMemory:Enabled = true", "Orkeon:CognitiveMemory:Enabled")]
    [InlineData("mcp_prompt_get", ToolRequirement.OnlyWithMcpServer, "MCP:Servers", "MCP:Servers")]
    [InlineData("brave_search", ToolRequirement.OnlyWithStoredKey, "BRAVE_API_KEY", "BRAVE_API_KEY")]
    public void A_tool_registered_under_a_setting_names_the_setting_the_matrix_names(
        string tool, ToolRequirement requirement, string argument, string setting)
    {
        var info = Assert.Single(ToolCatalog.All, t => t.Name == tool);
        Assert.Equal(requirement, info.Requirement);
        Assert.Equal(argument, info.Argument);

        var row = MatrixRowPattern().Matches(AvailabilityMatrix()).Single(r => r.Groups[1].Value.Trim() == $"`{tool}`");
        Assert.Contains($"`{setting}`", row.Groups[2].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conditions are pinned against the engine's code, not only its inventory: the runner host
    /// reads the memory switch off by default, and MCP is registered only while it is not switched
    /// off and declares a server, which the catalogue's MCP line says.
    /// </summary>
    [Fact]
    public void The_memory_and_mcp_conditions_are_the_ones_the_runner_host_reads()
    {
        var hosting = Path.Combine(OrkeonSources.Root(), "src", "hosting", "Orkeon.Hosting");
        var runner = File.ReadAllText(Path.Combine(hosting, "RunnerHost.cs"));
        var mcp = File.ReadAllText(Path.Combine(hosting, "McpStartup.cs"));

        Assert.Contains($"GetValue(\"{ToolCatalog.CognitiveMemoryEnabled}\", defaultValue: false)", runner, StringComparison.Ordinal);
        Assert.Contains("GetValue(\"Enabled\", defaultValue: true)", mcp, StringComparison.Ordinal);
        Assert.Contains("GetSection(\"Servers\")", mcp, StringComparison.Ordinal);
        Assert.Equal("MCP:Servers", ToolCatalog.McpServers);
        Assert.Contains("is on and declares a server", EnglishStudioStrings.Instance[StudioStringKeys.ToolOnlyWithMcpServer], StringComparison.Ordinal);
        Assert.Contains("memory: true", EnglishStudioStrings.Instance[StudioStringKeys.ToolOnlyWithCognitiveMemory], StringComparison.Ordinal);
    }

    /// <summary>
    /// system_one_decide is registered once the engine resolves a decision provider: named by the
    /// setting, or deduced from a hosted provider's section or its API key. The line names each.
    /// </summary>
    [Fact]
    public void The_decision_line_names_every_way_the_engine_resolves_a_provider()
    {
        static DecisionProviderChoice Resolve(Dictionary<string, string?> settings, string? key = null) =>
            DecisionProviderChoice.Resolve(
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                name => name == key ? "secret" : null);

        Assert.False(Resolve([]).HasProvider);
        Assert.Equal(DecisionProviderSelection.Named, Resolve(new() { [ToolCatalog.DecisionProvider] = "typesafe" }).How);
        var line = EnglishStudioStrings.Instance[StudioStringKeys.ToolOnlyWithDecisionProvider];
        Assert.Contains(ToolCatalog.DecisionProvider, line, StringComparison.Ordinal);

        var hosted = DecisionProviderDescriptor.All.Where(d => d.Hosted).ToList();
        Assert.Equal(2, hosted.Count);
        foreach (var provider in hosted)
        {
            Assert.Equal(provider, Resolve(new() { [provider.Section + ":Model"] = "m" }).Provider);
            Assert.Equal(provider, Resolve([], provider.DefaultApiKeyVariable).Provider);
            Assert.True(provider.KeyRequired);
            Assert.Contains(provider.Section, line, StringComparison.Ordinal);
            Assert.Contains(provider.DefaultApiKeyVariable, line, StringComparison.Ordinal);
            Assert.Contains(provider.DisplayName, line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The RAG tools are registered by every run and need no key: rag_search reads a collection,
    /// rag_eval takes a golden dataset (a required argument) and ingests its corpus itself, and the
    /// one that fills a collection needs nothing.
    /// </summary>
    [Fact]
    public void The_rag_tools_are_search_tools_and_say_what_each_one_reads()
    {
        var search = Assert.Single(ToolCatalog.Families, f => f.Key == ToolCatalog.SearchFamily);

        Assert.Equal(ToolRequirement.RagCollection, Assert.Single(search.Tools, t => t.Name == "rag_search").Requirement);
        Assert.Equal(ToolRequirement.RagDataset, Assert.Single(search.Tools, t => t.Name == "rag_eval").Requirement);
        Assert.Equal(ToolRequirement.None, Assert.Single(search.Tools, t => t.Name == "rag_ingest").Requirement);
        Assert.Single(ToolCatalog.All, t => t.Requirement == ToolRequirement.RagCollection);
    }

    [Fact]
    [Trait("BR", "RT-01")]
    public void ShouldContainSpawnAgent_WhenCatalogListed()
    {
        var names = ToolCatalog.All.Select(t => t.Name).ToList();

        Assert.Single(names, n => n == "spawn_agent");
        // The script host's tool: a YAML team cannot list it, and the line says so.
        Assert.Equal(ToolRequirement.ScriptOnly, Assert.Single(ToolCatalog.All, t => t.Name == "spawn_agent").Requirement);
    }

    [Fact]
    public void Every_key_requirement_points_at_a_key_the_card_offers()
    {
        var offered = ToolCatalog.Secrets.Select(s => s.EnvName).ToHashSet(StringComparer.Ordinal);
        var keyed = ToolCatalog.All
            .Where(t => t.Requirement is ToolRequirement.StoredKey or ToolRequirement.OnlyWithStoredKey)
            .ToList();

        Assert.Equal(3, keyed.Count);
        Assert.All(keyed, t => Assert.Contains(t.Argument!, offered));
        Assert.Equal(offered.Count, ToolCatalog.Secrets.Count);
        Assert.All(ToolCatalog.Secrets, s => Assert.Contains(ToolCatalog.All, t => t.Name == s.UsedBy));
    }

    [Fact]
    public void The_expert_setting_names_the_shell_section_the_runtime_reads()
    {
        var shell = Assert.Single(ToolCatalog.All, t => t.Requirement == ToolRequirement.ExpertSetting);

        Assert.Equal("shell_command", shell.Name);
        Assert.Equal(ShellToolsSection.SectionPath, shell.Argument);
        Assert.Equal("Orkeon:Tools:Shell", ShellToolsSection.SectionPath);
    }

    /// <summary>
    /// MAIL-05 — the e-mail family is pinned against the code, not only against the inventory:
    /// it carries exactly the tool contracts the e-mail assembly declares, so a tool added
    /// there and forgotten here, or a name misspelt here, fails on its own.
    /// </summary>
    [Fact]
    public void The_email_family_carries_every_tool_the_email_assembly_declares()
    {
        var email = Assert.Single(ToolCatalog.Families, f => f.Key == ToolCatalog.EmailFamily);
        var declared = typeof(EmailToolsServiceCollectionExtensions).Assembly.GetTypes()
            .Select(type => type.GetCustomAttribute<ToolContractAttribute>()?.UniqueName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(13, declared.Count);
        Assert.Equal(declared, email.Tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        // email_parser changed family: it is listed once, here, and no longer under Files.
        var files = Assert.Single(ToolCatalog.Families, f => f.Key == ToolCatalog.FilesFamily);
        Assert.DoesNotContain(files.Tools, t => t.Name == EmailParser);
    }

    /// <summary>
    /// DRIVE-05 — the drive family is pinned against the code the same way: exactly the tool
    /// contracts the drive assembly declares, every one of them needing a drive account declared
    /// in the section the runtime binds.
    /// </summary>
    [Fact]
    public void The_drive_family_carries_every_tool_the_drive_assembly_declares_each_needing_an_account()
    {
        var drive = Assert.Single(ToolCatalog.Families, f => f.Key == ToolCatalog.DriveFamily);
        var declared = typeof(DriveToolsServiceCollectionExtensions).Assembly.GetTypes()
            .Select(type => type.GetCustomAttribute<ToolContractAttribute>()?.UniqueName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(11, declared.Count);
        Assert.Equal(declared, drive.Tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(drive.Tools, t =>
        {
            Assert.Equal(ToolRequirement.DriveAccount, t.Requirement);
            Assert.Equal(ToolCatalog.DriveAccounts, t.Argument);
        });
        Assert.Equal("Orkeon:Tools:Drive:Accounts", ToolCatalog.DriveAccounts);
        Assert.Equal(11, ToolCatalog.All.Count(t => t.Requirement == ToolRequirement.DriveAccount));
    }

    /// <summary>
    /// Without an account the twelve mailbox tools are still registered and refuse every call,
    /// so what they need is neither a key nor a call parameter: an account declared in the
    /// section the runtime binds. The parser reads an .eml file and needs nothing.
    /// </summary>
    [Fact]
    public void The_email_account_requirement_names_the_email_section_the_runtime_binds()
    {
        var needAccount = ToolCatalog.All.Where(t => t.Requirement == ToolRequirement.EmailAccount).ToList();
        var email = Assert.Single(ToolCatalog.Families, f => f.Key == ToolCatalog.EmailFamily);

        Assert.Equal(12, needAccount.Count);
        Assert.All(needAccount, t =>
        {
            Assert.Contains(t, email.Tools);
            Assert.Equal(ToolCatalog.EmailAccounts, t.Argument);
        });
        Assert.Equal("Orkeon:Tools:Email:Accounts", ToolCatalog.EmailAccounts);
        Assert.Equal(ToolRequirement.None, Assert.Single(email.Tools, t => t.Name == EmailParser).Requirement);
    }

    /// <summary>
    /// STUDIO-67 — Studio has a form for the accounts, so the sentence of the requirement sends to
    /// Settings › E-mail: it names neither the section of the file nor <c>orkeon email login</c>.
    /// </summary>
    [Fact]
    public void The_email_account_requirement_reads_as_a_pointer_to_the_email_settings_tab()
    {
        var sentence = EnglishStudioStrings.Instance[StudioStringKeys.ToolNeedsEmailAccount];

        Assert.Equal("needs an e-mail account, declared in Settings › E-mail", sentence);
        Assert.Contains(EnglishStudioStrings.Instance[StudioStringKeys.ShellMails], sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The drive accounts have their form too, so the sentence of the requirement sends to
    /// Settings › Drive: it names neither the section of the file nor the sign-in verb.
    /// </summary>
    [Fact]
    public void The_drive_account_requirement_reads_as_a_pointer_to_the_drive_settings_tab()
    {
        var sentence = EnglishStudioStrings.Instance[StudioStringKeys.ToolNeedsDriveAccount];

        Assert.Equal("needs a drive account, declared in Settings › Drive", sentence);
        Assert.Contains(EnglishStudioStrings.Instance[StudioStringKeys.ShellDrive], sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("login", sentence, StringComparison.Ordinal);
    }
}
