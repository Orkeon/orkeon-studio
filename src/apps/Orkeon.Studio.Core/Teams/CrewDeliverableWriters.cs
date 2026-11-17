using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.Targets;
using YamlDotNet.RepresentationModel;
using DeliverableToolCheck = Orkeon.Application.Crew.DeliverableToolCheck;

namespace Orkeon.Studio.Core.Teams;

/// <summary>What a <see cref="CrewDeliverableWithoutWriter"/> is about, as the engine's <c>DeliverableFindingKind</c>.</summary>
public enum CrewDeliverableFindingKind
{
    /// <summary>No agent that may run the task holds a tool that writes its deliverable.</summary>
    NoWriter,

    /// <summary>A decision task declares a deliverable, which nothing ever writes.</summary>
    DecisionTask,
}

/// <summary>
/// A task whose deliverable may never be written: no agent that may run it holds a tool that writes
/// the file (<see cref="CrewDeliverableFindingKind.NoWriter"/>), or it is a decision task, which writes
/// no file (<see cref="CrewDeliverableFindingKind.DecisionTask"/>).
/// </summary>
/// <param name="Task">The task's key in the crew.</param>
/// <param name="Agents">
/// The keys of the agents that may run it, in the crew's order: the one it names when the process binds
/// it, else every agent the runtime may pick. Empty for a decision task. The keys are what the user edits;
/// the CLI's load warning names the same agents by their role.
/// </param>
/// <param name="Path">The deliverable's virtual path, as written.</param>
public sealed record CrewDeliverableWithoutWriter(string Task, IReadOnlyList<string> Agents, string Path)
{
    /// <summary>What the finding is about.</summary>
    public CrewDeliverableFindingKind Kind { get; init; } = CrewDeliverableFindingKind.NoWriter;

    /// <summary>
    /// The built-in tool to give the agent meant to write the file: the one made for its format
    /// (<c>docx_writer</c> for a <c>.docx</c>), else <c>file_write</c> for a text file; null for a binary
    /// format no built-in tool writes (<c>.pdf</c>), and for a decision task.
    /// </summary>
    public string? PreferredWriter =>
        Kind == CrewDeliverableFindingKind.NoWriter ? DeliverableToolCheck.PreferredWriterOf(Path) : null;

    /// <summary>
    /// Whether the file is of a binary format (<c>.docx</c>, <c>.pdf</c>, an image...): the final answer's
    /// text cannot be it, so <c>source: final_message</c> is no fix, a text format is.
    /// </summary>
    public bool IsBinary => DeliverableToolCheck.IsBinary(Path);
}

/// <summary>
/// The tasks of a YAML crew whose deliverable may never be written (Orkeon #392), judged as the
/// engine's own load check (<c>DeliverableToolCheck</c>, Orkeon 1.2) judges them, as far as the YAML
/// tells: a task that declares a <c>deliverable:</c> its agent writes with a tool (<c>source:
/// tool_call</c>, also what an absent source means) while none of the agents that may run it holds a
/// tool that writes that file, and a decision task that declares a deliverable at all. The engine loads
/// such a crew and counts the task a success without the file; an Orkeon 1.2 CLI warns of it at load.
/// The launchers warn before the run; they never block, the run still ending on a result.
/// <para>
/// A belt (the agent's <c>tools:</c> and the task's own) can write the file when it holds a tool
/// Orkeon does not ship, a name the catalogue does not carry (an MCP
/// server's, a plugin's, a script's: it may write, for all Studio can tell), or a built-in tool that writes that file (<c>DeliverableToolCheck.CanWrite</c>,
/// read from the engine, not copied): the tool of the file's format (<c>docx_writer</c> a <c>.docx</c>,
/// <c>xlsx_writer</c> a <c>.xlsx</c>, <c>image_generation</c> an image, <c>firefox</c> a <c>.png</c>), and
/// <c>file_write</c> for a text file only. The editors (<c>edit_file</c>, <c>edit_batch</c>,
/// <c>structural_rewrite</c>), the downloaders and <c>shell_command</c> do not count.
/// </para>
/// <para>
/// The agents that may run a task follow the process, as the engine's: under a sequential, parallel or
/// graph process, the agent the task's <c>agent:</c> names, else every agent (the round-robin picks);
/// under a hierarchical or consensual one, every agent but the manager agent; under an autonomous one,
/// every agent. The task is named only when none of them can write the file.
/// </para>
/// <para>
/// Not judged, where the engine sees more than the YAML: a provider that runs its own tools, and a key
/// the loader accepts under another spelling (<c>tools_</c>), as <see cref="CrewFileSystemScopes"/>. A
/// <c>permissionMode:</c> that denies a tool the agent holds is judged by neither: the engine's check
/// reads the tools the agent holds, as Studio does. The tools a run registers only under a setting of
/// their own (<c>system_one_decide</c>, <c>mcp_prompt_get</c>, <c>memory_recall</c>) and the RAG tools
/// are in the catalogue, Orkeon's own, and counted as no writer, as the engine counts them.
/// </para>
/// <para>
/// The crew is read as <see cref="CrewFileSystemScopes"/> reads it, and nothing is said of a crew the
/// engine refuses at load, so that the load error is the one thing the user reads: an unknown process, a
/// task naming an agent the crew lacks (whatever the process), a deliverable with an unknown source, a
/// decision task that names an agent, asks no question or sits under a process that runs no decision
/// (hierarchical, consensual, autonomous). Nor is anything said of a <c>.ork.ts</c> crew, which the CLI
/// judges itself.
/// </para>
/// </summary>
public static class CrewDeliverableWriters
{
    /// <summary>
    /// The tools Orkeon ships that a run registers, as the Settings screen lists them: a name outside
    /// them may write a file for all Studio can tell.
    /// </summary>
    private static readonly HashSet<string> KnownTools =
        new(Tools.ToolCatalog.All.Select(tool => tool.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>The tasks of the crew <paramref name="target"/> runs whose deliverable may not be written, in the crew's order.</summary>
    public static IReadOnlyList<CrewDeliverableWithoutWriter> Of(RunTarget? target) =>
        CrewFileSystemScopes.Read<CrewDeliverableWithoutWriter>(target, Collect);

    /// <summary>The same, of a single-file crew's text.</summary>
    public static IReadOnlyList<CrewDeliverableWithoutWriter> OfCrewFile(string yaml) =>
        CrewFileSystemScopes.ReadCrewFile<CrewDeliverableWithoutWriter>(yaml, Collect);

    /// <summary>
    /// Whether a belt can write <paramref name="path"/>: a tool Studio does not know, or a built-in
    /// writer of that file as the engine judges it.
    /// </summary>
    /// <param name="belt">The names of the tools the agent holds for the task.</param>
    /// <param name="path">The deliverable's virtual path.</param>
    public static bool CanWrite(IReadOnlyCollection<string> belt, string path) =>
        belt.Any(tool => !KnownTools.Contains(tool)) || DeliverableToolCheck.CanWrite(belt, path);

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

        var byKey = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, node) in agents)
        {
            declared.Add(key);
            if (node is YamlMappingNode agent)
                byKey.TryAdd(key, agent);
        }

        var bindsDeclaredAgent = process == ProcessType.Sequential || process == ProcessType.Parallel || process == ProcessType.Graph;
        var taskList = tasks.ToList();
        if (taskList.Exists(entry => entry.Value is YamlMappingNode task && RefusedAtLoad(task, declared, bindsDeclaredAgent)))
            return result;

        var manager = settings is null
            ? null
            : CrewFileSystemScopes.Text(
                CrewFileSystemScopes.Value(settings, "managerAgent") ?? CrewFileSystemScopes.Value(settings, "manager_agent"));
        var excludesManager = process?.AcceptsManagerAgent == true;

        foreach (var (key, node) in taskList)
        {
            if (node is not YamlMappingNode task || DeliverableOf(task) is not { } deliverable)
                continue;

            var path = deliverable.Path;

            // A decision runs no agent and writes no file, whatever the deliverable's source; one the
            // engine refuses never gets here.
            if (!CrewFileSystemScopes.IsNull(CrewFileSystemScopes.Value(task, "decision")))
            {
                result.Add(new CrewDeliverableWithoutWriter(key, [], path) { Kind = CrewDeliverableFindingKind.DecisionTask });
                continue;
            }

            if (!deliverable.ToolCall)
                continue;

            var named = CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(task, "agent"));
            List<string> candidates;
            if (bindsDeclaredAgent && named is not null)
            {
                // An agent entry that is no map is the loader's to refuse.
                if (!byKey.ContainsKey(named))
                    continue;
                candidates = [named];
            }
            else
            {
                // The manager agent of a hierarchical or consensual crew coordinates; it runs no task.
                candidates = [.. byKey.Keys.Where(agent => !excludesManager || agent != manager)];
            }

            var taskTools = ToolsOf(task).ToList();
            if (candidates.Count == 0
                || candidates.Exists(agent => CanWrite([.. ToolsOf(byKey[agent]), .. taskTools], path)))
            {
                continue;
            }

            result.Add(new CrewDeliverableWithoutWriter(key, candidates, path));
        }

        return result;
    }

    /// <summary>
    /// Whether the engine refuses the whole crew at load because of <paramref name="task"/>: it names an
    /// agent the crew does not declare (<c>YamlCrewMapper.ResolveAgent</c>, whatever the process), its
    /// deliverable has a path and a source the loader does not know (<c>ParseDeliverableSource</c>), or it
    /// declares a decision the engine refuses: one that names an agent or sits under a process that runs
    /// no decision (<c>DecisionTaskRules.Check</c>), or one that asks no question (<c>TaskDecision.Create</c>).
    /// </summary>
    private static bool RefusedAtLoad(YamlMappingNode task, HashSet<string> declared, bool runsDecisions)
    {
        var named = CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(task, "agent"));
        if (named is not null && !declared.Contains(named))
            return true;

        if (CrewFileSystemScopes.Value(task, "deliverable") is YamlMappingNode deliverable
            && CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(deliverable, "path")) is not null
            && DeliverableOf(task) is null)
        {
            return true;
        }

        var decision = CrewFileSystemScopes.Value(task, "decision");
        if (CrewFileSystemScopes.IsNull(decision))
            return false;

        return named is not null
            || !runsDecisions
            || decision is not YamlMappingNode block
            || CrewFileSystemScopes.Value(block, "questions") is not YamlMappingNode { Children.Count: > 0 };
    }

    /// <summary>
    /// The deliverable block as the loader reads it (<c>YamlCrewMapper.MapDeliverable</c>): its path and
    /// whether the agent writes it with a tool (<c>tool_call</c>, or no source); null without a block or
    /// a path, and for a source the loader refuses.
    /// </summary>
    private static (string Path, bool ToolCall)? DeliverableOf(YamlMappingNode task)
    {
        if (CrewFileSystemScopes.Value(task, "deliverable") is not YamlMappingNode deliverable
            || CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(deliverable, "path")) is not { } path)
        {
            return null;
        }

        return CrewFileSystemScopes.Text(CrewFileSystemScopes.Value(deliverable, "source"))?.ToUpperInvariant() switch
        {
            null or "TOOL_CALL" or "TOOLCALL" => (path, true),
            "NONE" or "FINAL_MESSAGE" or "FINALMESSAGE" or "STRUCTURED_OUTPUT" or "STRUCTUREDOUTPUT" => (path, false),
            _ => null,
        };
    }

    /// <summary>The names of a <c>tools:</c> list; empty when absent, as the loader reads it.</summary>
    private static IEnumerable<string> ToolsOf(YamlMappingNode owner) =>
        CrewFileSystemScopes.Value(owner, "tools") is YamlSequenceNode tools
            ? tools.Children.Select(CrewFileSystemScopes.Text).OfType<string>()
            : [];
}
