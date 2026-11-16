using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.Targets;
using YamlDotNet.RepresentationModel;

namespace Orkeon.Studio.Core.Teams;

/// <summary>A task whose agent must write its deliverable itself and does not hold <c>file_write</c>.</summary>
/// <param name="Task">The task's key in the crew.</param>
/// <param name="Agent">The key of the agent that runs it.</param>
/// <param name="Path">The deliverable's virtual path, as written.</param>
public sealed record CrewDeliverableWithoutWriter(string Task, string Agent, string Path);

/// <summary>
/// The tasks of a YAML crew whose deliverable may never be written (Orkeon #392): the task declares
/// a <c>deliverable:</c> its agent writes with a tool (<c>source: tool_call</c>, also what an absent
/// source means), the engine tells the agent to use <c>file_write</c>, and neither the agent's
/// <c>tools:</c> nor the task's own hold it. The engine loads such a crew and counts the task a
/// success when the agent answers in its message instead: no file, and the next task gets the answer
/// cut short. The launchers warn before the run; they never block, the run still ending on a result.
/// <para>
/// A warning is given only where the crew tells who runs the task: under a sequential, parallel or
/// graph process, the agent its <c>agent:</c> names, else the only agent of a one-agent crew; under
/// a hierarchical or autonomous one, the only worker. A decision task (no agent answers it), a
/// consensual crew and a task whose agent is not a declared one are left to the engine.
/// </para>
/// <para>
/// Nothing is said either of an agent that may deliver another way: one holding another tool that
/// creates a file (<see cref="OtherCreatingTools"/>), or a tool Studio's catalogue does not know (an
/// MCP server's, say), which may write too. <c>edit_file</c> does not count: it changes a file that
/// exists and refuses one that does not. What stays possible, and why the warning says "may": the
/// agent of a sequential or graph crew can delegate the writing to a coworker that holds the tool.
/// Not judged: a <c>permissionMode:</c> that denies the tool to an agent holding it, and a key the
/// loader accepts under another spelling (<c>tools_</c>), as <see cref="CrewFileSystemScopes"/>.
/// The crew is read as that class reads it: nothing is said of a crew the engine refuses at load,
/// nor of a <c>.ork.ts</c> one.
/// </para>
/// </summary>
public static class CrewDeliverableWriters
{
    /// <summary>The tool the engine's prompt names for a deliverable the agent writes.</summary>
    private const string FileWrite = "file_write";

    /// <summary>
    /// The other tools of the catalogue that create a file at a path the agent gives: an agent
    /// holding one may deliver with it (a <c>.docx</c> report, a picture, a shell redirection).
    /// </summary>
    private static readonly HashSet<string> OtherCreatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "docx_writer", "xlsx_writer", "image_generation", "firefox", "shell_command",
    };

    /// <summary>The tools Studio knows: a name outside them may write a file for all Studio can tell.</summary>
    private static readonly HashSet<string> KnownTools =
        new(Tools.ToolCatalog.All.Select(tool => tool.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>The tasks of the crew <paramref name="target"/> runs whose deliverable may not be written, in the crew's order.</summary>
    public static IReadOnlyList<CrewDeliverableWithoutWriter> Of(RunTarget? target) =>
        CrewFileSystemScopes.Read<CrewDeliverableWithoutWriter>(target, Collect);

    /// <summary>The same, of a single-file crew's text.</summary>
    public static IReadOnlyList<CrewDeliverableWithoutWriter> OfCrewFile(string yaml) =>
        CrewFileSystemScopes.ReadCrewFile<CrewDeliverableWithoutWriter>(yaml, Collect);

    private static List<CrewDeliverableWithoutWriter> Collect(
        IEnumerable<KeyValuePair<string, YamlNode?>> agents,
        IEnumerable<KeyValuePair<string, YamlNode?>> tasks,
        YamlMappingNode? settings)
    {
        var result = new List<CrewDeliverableWithoutWriter>();

        // Absent is sequential; an unknown process is refused at load.
        var process = ProcessType.Sequential;
        if (settings is not null
            && CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(settings, "process")) is { } written
            && !ProcessType.TryFrom(written, out process))
        {
            return result;
        }

        if (process == ProcessType.Consensual)
            return result;

        var byKey = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        foreach (var (key, node) in agents)
        {
            if (node is YamlMappingNode agent)
                byKey.TryAdd(key, agent);
        }

        var manager = settings is null
            ? null
            : CrewFileSystemScopes.Text(
                CrewFileSystemScopes.Value(settings, "managerAgent") ?? CrewFileSystemScopes.Value(settings, "manager_agent"));
        var workers = byKey.Keys.Where(key => key != manager).ToList();
        var assigned = process == ProcessType.Hierarchical || process == ProcessType.Autonomous;

        foreach (var (key, node) in tasks)
        {
            if (node is not YamlMappingNode task
                || !CrewFileSystemScopes.IsNull(CrewFileSystemScopes.Value(task, "decision"))
                || DeliverableToWrite(task) is not { } path)
            {
                continue;
            }

            // A manager or a planner hands the task out whatever its agent: line.
            var runner = assigned
                ? (workers.Count == 1 ? workers[0] : null)
                : CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(task, "agent")) ?? (byKey.Count == 1 ? byKey.Keys.First() : null);
            if (runner is null || !byKey.TryGetValue(runner, out var agent))
                continue;

            var belt = ToolsOf(agent).Concat(ToolsOf(task)).ToList();
            if (!belt.Exists(MayDeliver))
                result.Add(new CrewDeliverableWithoutWriter(key, runner, path));
        }

        return result;
    }

    /// <summary>
    /// The path of the deliverable the task's agent writes with a tool, as the loader reads the block
    /// (<c>YamlCrewMapper.MapDeliverable</c>): null without a block or a path, and for a source the
    /// framework persists itself (<c>final_message</c>, <c>structured_output</c>) or <c>none</c>.
    /// </summary>
    private static string? DeliverableToWrite(YamlMappingNode task)
    {
        if (CrewFileSystemScopes.Value(task, "deliverable") is not YamlMappingNode deliverable
            || CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(deliverable, "path")) is not { } path)
        {
            return null;
        }

        var source = CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(deliverable, "source"));
        return source is null || source.ToUpperInvariant() is "TOOL_CALL" or "TOOLCALL" ? path : null;
    }

    /// <summary>Whether a tool of the belt may write the deliverable, for all Studio can tell.</summary>
    private static bool MayDeliver(string tool) =>
        string.Equals(tool, FileWrite, StringComparison.OrdinalIgnoreCase)
        || OtherCreatingTools.Contains(tool)
        || !KnownTools.Contains(tool);

    /// <summary>The names of a <c>tools:</c> list; empty when absent, as the loader reads it.</summary>
    private static IEnumerable<string> ToolsOf(YamlMappingNode owner) =>
        CrewFileSystemScopes.Value(owner, "tools") is YamlSequenceNode tools
            ? tools.Children.Select(CrewFileSystemScopes.Text).OfType<string>()
            : [];
}
