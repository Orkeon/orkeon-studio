using System.Text.Json.Nodes;
using Orkeon.Studio.Core.Tools;

namespace Orkeon.Studio.Core.Forge;

/// <summary>The tools of one catalogue family a forged agent or task may still be given.</summary>
/// <param name="FamilyKey">The family's key (<see cref="ToolCatalog"/>), which a screen turns into its label.</param>
/// <param name="Tools">Its tools, in the catalogue's order.</param>
public sealed record ForgeToolGroup(string FamilyKey, IReadOnlyList<string> Tools);

/// <summary>
/// What an editor of a forged blueprint offers as tools: the ones the team already holds first,
/// then the rest of the catalogue a forged crew may draw from, by family.
/// </summary>
/// <param name="TeamTools">Every tool an agent or a task of the blueprint names, sorted.</param>
/// <param name="Families">The other tools, by family; a family left with none is not listed.</param>
public sealed record ForgeToolPalette(IReadOnlyList<string> TeamTools, IReadOnlyList<ForgeToolGroup> Families)
{
    /// <summary>
    /// The tools the engine never lets into a forged crew (<c>ForgeSandbox.DeniedTools</c>): the
    /// shell and the interpreter escape the folder confinement, the drive tools would act on the
    /// operator's actual drive. Copied from the engine and pinned there by a drift test.
    /// </summary>
    public static IReadOnlySet<string> DeniedTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "shell_command", "code_interpreter",
        "drive_accounts", "drive_create_folder", "drive_delete", "drive_download", "drive_info", "drive_list",
        "drive_move", "drive_read", "drive_search", "drive_share", "drive_upload",
    };

    /// <summary>
    /// The tools of <c>orkeon run</c> the forge's host never holds, so its catalogue
    /// (<c>ForgeSandbox.SelectCrewTools</c> over the host's <c>IBaseTool</c>s) refuses each with
    /// <c>FORGE-TOOL-UNKNOWN</c>: <c>human_input</c>, which <c>AddOrkeonHumanInput</c> registers
    /// (<c>RunnerExecution</c> calls it, the forge's <c>RunnerHost.Build</c> does not);
    /// <c>semantic_search</c>, which <c>AddSemanticSearchTool</c> registers (<c>orkeon run</c> calls
    /// it, the forge does not); and the two coworker tools, never registered at all but built per
    /// agent from its <c>allowDelegation</c> by <c>AgentDelegationToolsProvider</c>. Pinned on the
    /// engine's composition by a drift test, so a registration the forge gains or loses fails there.
    /// </summary>
    public static IReadOnlySet<string> OutsideTheForgeHost { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "human_input", "semantic_search", "ask_question_to_coworker", "delegate_work_to_coworker",
    };

    /// <summary>
    /// Reads the palette of <paramref name="blueprintJson"/>. Besides the denied tools and the ones
    /// outside the forge's host (<see cref="OutsideTheForgeHost"/>), the catalogue offers neither a
    /// script-only tool nor one the engine registers only once something is set (a key, an MCP
    /// server, a decision provider, a cognitive memory): on most machines the engine
    /// would refuse it as unknown. A tool the blueprint already names stays offered whatever its
    /// kind: the engine accepted it, and an edit never drops it silently.
    /// </summary>
    public static ForgeToolPalette For(string? blueprintJson)
    {
        var team = TeamToolsOf(blueprintJson);
        var taken = team.ToHashSet(StringComparer.Ordinal);
        var families = new List<ForgeToolGroup>();
        foreach (var family in ToolCatalog.Families)
        {
            var tools = family.Tools
                .Where(tool => Offered(tool) && !taken.Contains(tool.Name))
                .Select(tool => tool.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var name in tools)
                taken.Add(name);
            if (tools.Count > 0)
                families.Add(new ForgeToolGroup(family.Key, tools));
        }

        return new ForgeToolPalette(team, families);
    }

    /// <summary>Whether the catalogue offers <paramref name="tool"/> to a forged crew that does not hold it yet.</summary>
    public static bool Offered(ToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return !DeniedTools.Contains(tool.Name) && !OutsideTheForgeHost.Contains(tool.Name) && tool.Requirement is not (
            ToolRequirement.ScriptOnly
            or ToolRequirement.OnlyWithStoredKey
            or ToolRequirement.OnlyWithMcpServer
            or ToolRequirement.OnlyWithDecisionProvider
            or ToolRequirement.OnlyWithCognitiveMemory);
    }

    private static List<string> TeamToolsOf(string? blueprintJson)
    {
        if (string.IsNullOrWhiteSpace(blueprintJson))
            return [];

        try
        {
            if (JsonNode.Parse(blueprintJson) is not JsonObject blueprint)
                return [];

            return [.. new[] { blueprint["agents"], blueprint["tasks"] }
                .OfType<JsonArray>()
                .SelectMany(entries => entries.OfType<JsonObject>())
                .SelectMany(entry => BlueprintJson.Strings(entry["tools"]))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}

/// <summary>Small readers shared by the editors of a blueprint's JSON.</summary>
public static class BlueprintJson
{
    /// <summary>The non-empty strings of a JSON array; none when <paramref name="node"/> is no array.</summary>
    public static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? [.. array.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var text) ? text : null)
                .Where(text => text is { Length: > 0 })
                .Cast<string>()]
            : [];

    /// <summary>The string of a JSON value; null when it is none.</summary>
    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Parses a blueprint; null when the text is blank, no JSON, or no object.</summary>
    public static JsonObject? Parse(string? blueprintJson)
    {
        if (string.IsNullOrWhiteSpace(blueprintJson))
            return null;

        try
        {
            return JsonNode.Parse(blueprintJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A slug key from <paramref name="name"/>, unique among <paramref name="taken"/>: <c>-2</c>,
    /// <c>-3</c>… appended until it is. A blank name takes <paramref name="placeholder"/>, a name
    /// that keeps no usable character the team fallback, as a team folder would.
    /// </summary>
    public static string UniqueKey(string? name, string placeholder, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var basis = string.IsNullOrWhiteSpace(name)
            ? placeholder
            : Orkeon.Domain.FileSystem.FolderSlug.From(name) ?? Orkeon.Domain.FileSystem.FolderSlug.TeamFallback;
        var keys = taken.ToHashSet(StringComparer.Ordinal);
        var key = basis;
        for (var i = 2; keys.Contains(key); i++)
            key = $"{basis}-{i}";
        return key;
    }
}
