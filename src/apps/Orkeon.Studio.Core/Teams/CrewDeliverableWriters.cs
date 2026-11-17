using Microsoft.Extensions.Logging.Abstractions;
using Orkeon.Compliance.Vfs;
using Orkeon.Domain.Configuration;
using Orkeon.Domain.Exceptions;
using Orkeon.Domain.FileSystem;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Domain.Task.ValueObjects;
using Orkeon.Infrastructure.Configuration;
using Orkeon.Infrastructure.FileSystem;
using Orkeon.Infrastructure.Security;
using Orkeon.Infrastructure.Serialization;
using Orkeon.Studio.Core.Targets;
using YamlDotNet.Core;
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
/// engine's own load check (<c>DeliverableToolCheck</c>, Orkeon 1.2) judges them: a task that declares
/// a <c>deliverable:</c> its agent writes with a tool (<c>source: tool_call</c>, also what an absent
/// source means) while none of the agents that may run it holds a tool that writes that file, and a
/// decision task that declares a deliverable at all. The engine loads such a crew and counts the task a
/// success without the file; an Orkeon 1.2 CLI warns of it at load. The launchers warn before the run;
/// they never block, the run still ending on a result.
/// <para>
/// The crew is read by the engine's own loader, in process (<c>YamlCrewDefinitionLoader</c> over a
/// read-only mount of the crew's folder, as <c>orkeon run</c> reads it): its keys, its spellings, its
/// <c>anchors:</c> and its values are the engine's. Nothing is said of a crew that loader refuses, nor of
/// one the validator the crew factory runs next refuses (<c>CrewDefinitionValidator</c>), so that the load
/// error is the one thing the user reads. What the factory judges only against the run's own host is not
/// judged here, and its error is what the launch reports: a tool name the run does not register (the
/// strict tool check), an <c>llm.profile</c> the settings do not offer, the ingestion of a <c>rag:</c>
/// block. Nor is anything said of a <c>.ork.ts</c> crew, which the CLI judges itself.
/// </para>
/// <para>
/// A belt (the agent's <c>tools:</c> and the task's own) can write the file when it holds a tool name
/// Studio's catalogue does not carry (an MCP server's, a plugin's, a script's: it may write, for all
/// Studio can tell), or a built-in tool that writes that file (<c>DeliverableToolCheck.CanWrite</c>, read
/// from the engine, not copied): the tool of the file's format (<c>docx_writer</c> a <c>.docx</c>,
/// <c>xlsx_writer</c> a <c>.xlsx</c>, <c>image_generation</c> an image, <c>firefox</c> a <c>.png</c>), and
/// <c>file_write</c> for a text file only. The editors (<c>edit_file</c>, <c>edit_batch</c>,
/// <c>structural_rewrite</c>), the downloaders and <c>shell_command</c> do not count. The engine judges a
/// tool by its assembly, which the YAML does not tell: a plugin whose assembly is named <c>Orkeon.*</c>
/// is judged by its name there, as no writer, where Studio counts it as one that may write.
/// </para>
/// <para>
/// The agents that may run a task follow the process, as the engine's: under a sequential, parallel or
/// graph process, the agent the task's <c>agent:</c> names, else every agent (the round-robin picks);
/// under a hierarchical or consensual one, every agent but the manager agent; under an autonomous one,
/// every agent. The task is named only when none of them can write the file. A <c>permissionMode:</c>
/// that denies a tool the agent holds is judged by neither: the engine's check reads the tools the agent
/// holds, as Studio does. The tools a run registers only under a setting of their own
/// (<c>system_one_decide</c>, <c>mcp_prompt_get</c>, <c>memory_recall</c>) and the RAG tools are in the
/// catalogue, Orkeon's own, and counted as no writer, as the engine counts them.
/// </para>
/// </summary>
public static class CrewDeliverableWriters
{
    /// <summary>Where the crew's folder is mounted for the engine's loader, as <c>orkeon run</c> mounts it.</summary>
    private const string CrewMount = "/crew";

    /// <summary>
    /// The tools Orkeon ships that a run registers, as the Settings screen lists them: a name outside
    /// them may write a file for all Studio can tell.
    /// </summary>
    private static readonly HashSet<string> KnownTools =
        new(Tools.ToolCatalog.All.Select(tool => tool.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tasks of the crew <paramref name="target"/> runs whose deliverable may not be written, in the
    /// crew's order. Empty without a target, for a script, and for a crew the engine refuses at load.
    /// </summary>
    [SuppressVfsCompliance(
        "EXCEPTION-BOOTSTRAP: Studio is a host application; the crew's folder is user-owned storage on the " +
        "physical disk, mounted for the engine's loader as orkeon run mounts it.")]
    public static IReadOnlyList<CrewDeliverableWithoutWriter> Of(RunTarget? target)
    {
        if (target is null || target.Dialect != RunTargetDialect.Yaml || string.IsNullOrWhiteSpace(target.RunPath))
            return [];

        var directory = target.Kind == RunTargetKind.MultiFileCrewDirectory;
        var folder = directory ? target.RunPath : Path.GetDirectoryName(Path.GetFullPath(target.RunPath));
        if (string.IsNullOrEmpty(folder))
            return [];

        // The engine's file system is async; nothing here awaits the caller's context.
        return Task.Run(async () =>
        {
            try
            {
                using var registry = new FileSystemRegistry(
                    [FileSystemMount.Parse($"{FileSystemMount.Quote(folder)}:{CrewMount}:ro")]);
                var loader = Loader(registry, folder);
                var config = directory
                    ? await loader.LoadFromDirectoryAsync(CrewMount).ConfigureAwait(false)
                    : await loader.LoadFromFileAsync($"{CrewMount}/{Path.GetFileName(target.RunPath)}").ConfigureAwait(false);
                return Judge(loader, config);
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                return [];
            }
        }).GetAwaiter().GetResult();
    }

    /// <summary>The same, of a single-file crew's text.</summary>
    public static IReadOnlyList<CrewDeliverableWithoutWriter> OfCrewFile(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            using var registry = new FileSystemRegistry([]);
            var loader = Loader(registry, workspace: null);
            // A string is loaded at once: the task is complete when it returns.
            return Judge(loader, loader.LoadFromStringAsync(yaml).GetAwaiter().GetResult());
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Whether a belt can write <paramref name="path"/>: a tool Studio does not know, or a built-in
    /// writer of that file as the engine judges it.
    /// </summary>
    /// <param name="belt">The names of the tools the agent holds for the task.</param>
    /// <param name="path">The deliverable's virtual path.</param>
    public static bool CanWrite(IReadOnlyCollection<string> belt, string path) =>
        belt.Any(tool => !KnownTools.Contains(tool)) || DeliverableToolCheck.CanWrite(belt, path);

    /// <summary>
    /// The engine's crew loader over <paramref name="registry"/>, its paths checked by the engine's own
    /// validator against the crew's folder (<paramref name="workspace"/>), the one root it reads.
    /// </summary>
    private static YamlCrewDefinitionLoader Loader(FileSystemRegistry registry, string? workspace) =>
        new(
            new YamlDotNetSerializer(),
            new FileSystemService(
                registry,
                new PathValidator(new PathSecurityOptions { DefaultWorkspaceRoot = workspace }, NullLogger<PathValidator>.Instance),
                NullLogger<FileSystemService>.Instance),
            NullLogger<YamlCrewDefinitionLoader>.Instance);

    /// <summary>
    /// Whether <paramref name="ex"/> is how the engine's loader or its file system refuses a crew: what the
    /// launch would report as the load error.
    /// </summary>
    private static bool IsRefusal(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or FormatException or InvalidCastException
            or NotSupportedException or IOException or UnauthorizedAccessException or YamlException
            or FileAccessDeniedException or DomainException;

    /// <summary>
    /// The findings of a loaded crew, as <c>DeliverableToolCheck.Check</c> finds them over the crew the
    /// factory builds from it; none when the factory's validator refuses the crew.
    /// </summary>
    private static List<CrewDeliverableWithoutWriter> Judge(YamlCrewDefinitionLoader loader, CrewConfiguration crew)
    {
        var result = new List<CrewDeliverableWithoutWriter>();
        if (!loader.Validate(crew).IsValid)
            return result;

        foreach (var task in crew.Tasks)
        {
            if (task.Deliverable is not { } deliverable || string.IsNullOrWhiteSpace(deliverable.Path))
                continue;

            var path = deliverable.Path;

            // A decision runs no agent and writes no file, whatever the deliverable's source.
            if (task.Decision is not null)
            {
                result.Add(new CrewDeliverableWithoutWriter(NameOf(task), [], path) { Kind = CrewDeliverableFindingKind.DecisionTask });
                continue;
            }

            if (deliverable.Source != DeliverableSource.ToolCall)
                continue;

            var candidates = CandidatesOf(task, crew);
            if (candidates.Count == 0
                || candidates.Exists(agent => CanWrite([.. agent.Tools, .. task.Tools], path)))
            {
                continue;
            }

            result.Add(new CrewDeliverableWithoutWriter(NameOf(task), [.. candidates.Select(KeyOf)], path));
        }

        return result;
    }

    /// <summary>
    /// The agents that may run <paramref name="task"/>, in the crew's order, as the engine's
    /// <c>DeliverableToolCheck.CandidatesOf</c> picks them.
    /// </summary>
    private static List<AgentConfiguration> CandidatesOf(TaskConfiguration task, CrewConfiguration crew)
    {
        var process = crew.Process;
        if (process == ProcessType.Sequential || process == ProcessType.Parallel || process == ProcessType.Graph)
        {
            return task.AssignedAgentId is { } assigned && crew.Agents.FirstOrDefault(agent => agent.Id == assigned) is { } declared
                ? [declared]
                : [.. crew.Agents];
        }

        // The manager agent of a hierarchical or consensual crew coordinates; it runs no task.
        return process.AcceptsManagerAgent && crew.ManagerAgentId is { } manager
            ? [.. crew.Agents.Where(agent => agent.Id != manager)]
            : [.. crew.Agents];
    }

    /// <summary>An agent by its key, what the user edits; its role when it has none.</summary>
    private static string KeyOf(AgentConfiguration agent) =>
        string.IsNullOrWhiteSpace(agent.Key) ? agent.Role : agent.Key;

    /// <summary>A task by its key, else the start of its description, as the engine names it (<c>NameOf</c>).</summary>
    private static string NameOf(TaskConfiguration task)
    {
        if (!string.IsNullOrWhiteSpace(task.Key))
            return task.Key;

        var description = task.Description;
        return description.Length <= 60 ? description : string.Concat(description.AsSpan(0, 57), "...");
    }
}
