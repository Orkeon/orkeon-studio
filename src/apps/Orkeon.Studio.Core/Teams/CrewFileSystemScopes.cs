using Orkeon.Compliance.Vfs;
using Orkeon.Domain.FileSystem;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.Targets;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Orkeon.Studio.Core.Teams;

/// <summary>
/// What one agent of a YAML crew declares of the file system: its <c>filesystem:</c> scope (#349) and
/// its working root.
/// </summary>
/// <param name="Key">The agent's key in the crew (its file stem in an <c>agents/</c> folder), which the runtime names in a shape refusal.</param>
/// <param name="Name">The name the runtime gives the agent at start (its <c>role</c>, else its key), which it names in a scope refusal.</param>
/// <param name="Entries">The declared entries; empty when the agent declares no scope.</param>
/// <param name="WorkingRoot">The agent's <c>workingRoot</c>, or <see langword="null"/>.</param>
/// <param name="Refusal">Why the runtime refuses the declaration as written, whatever the mounts; <see langword="null"/> when it is well formed.</param>
/// <param name="StartsForSure">
/// Whether the run starts the agent whatever its course, as the engine's process strategies decide
/// it: a refusal of its scope then stops the run. False where the crew alone does not tell (an agent
/// a manager, a planner or the round-robin may pick, one whose task a condition may skip), and for
/// the <c>managerAgent:</c>, whose refusal never stops a run.
/// </param>
public sealed record CrewAgentFileSystem(
    string Key,
    string Name,
    IReadOnlyList<FileSystemScopeEntry> Entries,
    string? WorkingRoot,
    string? Refusal = null,
    bool StartsForSure = false);

/// <summary>
/// The file-system declarations of the agents of a YAML crew (#349), read so the launchers can tell,
/// before the run, that the runtime will refuse an agent at start: a scope entry under no mount of
/// the run, above the mount's rights, or a working root outside the scope (<c>FileSystemScopePreflight</c>).
/// <para>
/// The crew is read where the engine's loader (<c>YamlCrewDefinitionLoader</c>) reads it, from the
/// run target's run path: a single file whose <c>agents:</c> map names each agent; a per-entity
/// folder (<c>agents/</c> or <c>tasks/</c>) whose <c>agents/*.yaml</c> and <c>*.yml</c> files (the
/// extension in any case) are one agent each, keyed by file stem, beside <c>config.yaml</c> (else
/// <c>crew.yaml</c>), which holds settings only; or the flat triplet, whose <c>agents.yaml</c> and <c>tasks.yaml</c> are the agent
/// and task maps themselves. The keys are the loader's (<c>filesystem</c>, <c>role</c>,
/// <c>workingRoot</c> or <c>working_root</c>), and the entries' shape is judged with its rules
/// (<c>YamlCrewMapper.MapFileSystemScope</c>).
/// </para>
/// <para>
/// It never throws, and declares nothing where the engine refuses the crew at load with its own
/// message: a file that cannot be read or parsed, a missing settings file, a mixed layout, an entity
/// written twice, a <c>&lt;&lt;</c> merge key. A <c>.ork.ts</c> crew is not read.
/// </para>
/// </summary>
[SuppressVfsCompliance(
    "EXCEPTION-BOOTSTRAP: Studio is a host application; a team's crew is user-owned storage on the " +
    "physical disk, read as the team catalog reads it.")]
public static class CrewFileSystemScopes
{
    private const string AgentsFolder = "agents";
    private const string TasksFolder = "tasks";

    /// <summary>The settings file of a per-entity crew, in the loader's order.</summary>
    private static readonly string[] SettingsFiles = ["config.yaml", "crew.yaml"];

    /// <summary>
    /// The agents of the crew <paramref name="target"/> runs that declare a scope or a working root,
    /// in the crew's order. Empty without a target, for a script, when no agent declares any, or when
    /// the engine refuses the crew at load.
    /// </summary>
    public static IReadOnlyList<CrewAgentFileSystem> Of(RunTarget? target)
    {
        if (target is null || target.Dialect != RunTargetDialect.Yaml || string.IsNullOrWhiteSpace(target.RunPath))
            return [];

        try
        {
            return target.Kind == RunTargetKind.MultiFileCrewDirectory
                ? OfCrewDirectory(target.RunPath)
                : OfCrewFile(File.ReadAllText(target.RunPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>The agents of a single-file crew's <c>agents:</c> map that declare a scope or a working root.</summary>
    public static IReadOnlyList<CrewAgentFileSystem> OfCrewFile(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (!TryRoot(yaml, out var root) || root is not YamlMappingNode crew)
            return [];

        return Collect(Entries(Value(crew, "agents") as YamlMappingNode), Entries(Value(crew, "tasks") as YamlMappingNode), crew);
    }

    /// <summary>The agents of a crew folder, per-entity or flat, as the loader picks the layout.</summary>
    private static List<CrewAgentFileSystem> OfCrewDirectory(string root)
    {
        var agentsFolder = Path.Combine(root, AgentsFolder);
        var tasksFolder = Path.Combine(root, TasksFolder);
        var perEntity = Directory.Exists(agentsFolder) || Directory.Exists(tasksFolder);

        if (!perEntity)
        {
            // The flat triplet: agents.yaml and tasks.yaml are the maps themselves.
            if (!TryReadRoot(Path.Combine(root, "crew.yaml"), out var flatSettings)
                || !TryReadRoot(Path.Combine(root, AgentsFolder + ".yaml"), out var agentMap)
                || !TryReadRoot(Path.Combine(root, TasksFolder + ".yaml"), out var taskMap))
            {
                return [];
            }

            return Collect(
                Entries(agentMap as YamlMappingNode),
                Entries(taskMap as YamlMappingNode),
                flatSettings as YamlMappingNode);
        }

        // A flat file beside its per-entity folder is refused at load.
        if ((Directory.Exists(agentsFolder) && File.Exists(Path.Combine(root, AgentsFolder + ".yaml")))
            || (Directory.Exists(tasksFolder) && File.Exists(Path.Combine(root, TasksFolder + ".yaml"))))
        {
            return [];
        }

        // Settings only: an agents: map written there is not read by the loader.
        var settingsFile = SettingsFiles.Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists);
        if (settingsFile is null || !TryReadRoot(settingsFile, out var settings)
            || EntityFiles(agentsFolder) is not { } agentFiles || EntityFiles(tasksFolder) is not { } taskFiles)
        {
            return [];
        }

        var agents = new List<KeyValuePair<string, YamlNode?>>(agentFiles.Count);
        foreach (var (stem, path) in agentFiles)
        {
            if (!TryReadRoot(path, out var agent))
                return [];
            agents.Add(new(stem, agent));
        }

        var tasks = new List<KeyValuePair<string, YamlNode?>>(taskFiles.Count);
        foreach (var (stem, path) in taskFiles)
        {
            if (!TryReadRoot(path, out var task))
                return [];
            tasks.Add(new(stem, task));
        }

        return Collect(agents, tasks, settings as YamlMappingNode);
    }

    /// <summary>The extensions of an entity file, which the loader matches whatever their case.</summary>
    private static readonly string[] EntityExtensions = [".yaml", ".yml"];

    /// <summary>
    /// The engine's own enumeration of an entity folder (<c>FileSystemService.EnumerateFilesAsync</c>):
    /// the folder alone, a symbolic link left out, an entry the disk withholds passed over.
    /// </summary>
    private static readonly EnumerationOptions EntityEnumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// The <c>*.yaml</c> and <c>*.yml</c> files of an entity folder, sorted ordinally, with their stems;
    /// empty when the folder is absent, null when a stem is written twice (under both extensions, or
    /// under two spellings of one). The extension is matched whatever its case, on every platform, as
    /// the loader matches it: <c>spare.YAML</c> is the agent <c>spare</c> under Linux too. The stem
    /// keeps its case.
    /// </summary>
    private static List<(string Stem, string Path)>? EntityFiles(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        // Every file, filtered here: a search pattern follows the platform's case rule, the loader does not.
        var paths = Directory.EnumerateFiles(folder, "*", EntityEnumeration)
            .Where(path => EntityExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();
        var files = paths.Select(path => (Stem: Path.GetFileNameWithoutExtension(path), Path: path)).ToList();
        return files.DistinctBy(file => file.Stem, StringComparer.Ordinal).Count() == files.Count ? files : null;
    }

    /// <summary>The entries of an <c>agents:</c> or a <c>tasks:</c> map, in its order.</summary>
    private static IEnumerable<KeyValuePair<string, YamlNode?>> Entries(YamlMappingNode? map) =>
        map is null
            ? []
            : map.Children
                .Where(child => child.Key is YamlScalarNode { Value.Length: > 0 })
                .Select(child => new KeyValuePair<string, YamlNode?>(((YamlScalarNode)child.Key).Value!, child.Value));

    /// <summary>
    /// The declarations of <paramref name="agents"/>, each told whether the run starts it for sure
    /// (<see cref="StartsOf"/>).
    /// </summary>
    private static List<CrewAgentFileSystem> Collect(
        IEnumerable<KeyValuePair<string, YamlNode?>> agents, IEnumerable<KeyValuePair<string, YamlNode?>> tasks, YamlMappingNode? settings)
    {
        var all = agents.ToList();
        var starts = StartsOf(
            all.Select(agent => agent.Key).ToList(),
            tasks.Where(task => task.Value is YamlMappingNode).Select(task => (task.Key, Task: (YamlMappingNode)task.Value!)).ToList(),
            settings);

        var result = new List<CrewAgentFileSystem>();
        foreach (var (key, node) in all)
        {
            if (node is YamlMappingNode agent && Read(key, agent) is { } declared)
                result.Add(declared with { StartsForSure = starts.Contains(key) });
        }

        return result;
    }

    /// <summary>
    /// The agents the run starts for sure, by key, as the engine's process strategies choose who runs a task
    /// (Orkeon.Infrastructure, <c>Crew/Strategies</c> and <c>Consensus</c>). Where the crew alone does
    /// not tell, the agent is left out: a refusal of its scope is then a warning, never a wrongful block.
    /// <list type="bullet">
    /// <item>Sequential (the default), parallel, graph: a task runs on the agent its <c>agent:</c>
    /// names, else on the round-robin's pick, which is known only for a one-agent crew
    /// (<c>TaskAgentSelector</c>, <c>TaskAgentSelection</c>). Only a task that runs for sure counts
    /// (<see cref="RunsAnAgentForSure"/>); under a graph that describes its nodes or edges, only the
    /// start node's task (<see cref="StartTaskOf"/>), the others hanging on the routing.</item>
    /// <item>Hierarchical, autonomous: the manager or the planner hands out every task, whatever its
    /// <c>agent:</c>. With one worker there is no one else to pick: the worker runs, or the run fails
    /// on an assignment to nobody (<c>HierarchicalProcessStrategy</c>, <c>AutonomousProcessStrategy</c>).</item>
    /// <item>Consensual: every agent but the manager answers the first task, and a refused one fails
    /// the run: the answers are awaited together, outside the guard that turns one agent's failure
    /// into a lost voice (<c>ConsensualProcessStrategy.ExecuteWithAllAgentsAsync</c>).</item>
    /// <item>The <c>managerAgent:</c> never counts, its refusal never stopping a run. Two processes
    /// take one (<c>ProcessType.AcceptsManagerAgent</c>): the hierarchical manager runs no task; the
    /// consensual one may cast a deciding ballot under a host fallback setting, where a refusal is
    /// caught as an abstention. The four others (sequential, parallel, graph, autonomous) refuse a
    /// crew that names one at load (<c>CrewDefinitionValidator</c>): what is said of it then is moot.</item>
    /// </list>
    /// </summary>
    private static HashSet<string> StartsOf(
        List<string> agents, List<(string Key, YamlMappingNode Task)> tasks, YamlMappingNode? settings)
    {
        var starts = new HashSet<string>(StringComparer.Ordinal);

        // Absent is sequential; the value is trimmed and read whatever its case, and an unknown one is
        // refused at load (YamlCrewMapper.ParseProcessType). A crew without a task starts no agent.
        var process = ProcessType.Sequential;
        if (settings is not null && Text(Value(settings, "process")) is { } written && !ProcessType.TryFrom(written, out process))
            return starts;
        if (tasks.Count == 0)
            return starts;

        var manager = settings is null ? null : Text(Value(settings, "managerAgent") ?? Value(settings, "manager_agent"));
        var workers = agents.Where(agent => agent != manager).ToList();

        if (process == ProcessType.Consensual)
        {
            starts.UnionWith(workers);
        }
        else if (process == ProcessType.Hierarchical || process == ProcessType.Autonomous)
        {
            // Hierarchical: the workers are the agents but the manager. Autonomous has no manager (one
            // named is refused at load): the workers are all the agents.
            if (workers.Count == 1)
                starts.Add(workers[0]);
        }
        else
        {
            // Sequential, parallel, graph: no manager there (a managerAgent: is refused at load), so
            // every agent works.
            var byKey = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
            foreach (var (key, task) in tasks)
                byKey.TryAdd(key, task);

            var topology = process == ProcessType.Graph ? Topology(settings) : null;
            IEnumerable<YamlMappingNode> sure = topology is null
                ? tasks.Select(task => task.Task).Where(task => RunsAnAgentForSure(task, byKey))
                : StartTaskOf(topology, byKey) is { } start && RunsAnAgentForSure(start, byKey) && IsNull(Value(start, "dependencies"))
                    ? [start]
                    : [];

            foreach (var task in sure)
            {
                if ((Text(Value(task, "agent")) ?? (agents.Count == 1 ? agents[0] : null)) is { } runner)
                    starts.Add(runner);
            }
        }

        return starts;
    }

    /// <summary>
    /// Whether the run reaches <paramref name="task"/> and gives it to an agent whatever the answers
    /// of the crew's decisions: it is no decision task (the decision runner answers those, no agent
    /// does) and no condition can skip it (<see cref="MayBeSkipped"/>). A task an earlier failure
    /// blocks still counts: the run it belongs to has failed already.
    /// </summary>
    private static bool RunsAnAgentForSure(YamlMappingNode task, Dictionary<string, YamlMappingNode> tasks) =>
        IsNull(Value(task, "decision")) && !MayBeSkipped(task, tasks, []);

    /// <summary>
    /// Whether a condition may skip the task, as <c>ConditionGate</c> decides it: its own <c>when:</c>,
    /// or the join rule, a task whose every dependency was condition-skipped being skipped too. The
    /// dependencies are the engine's (<c>YamlCrewMapper.MapTasks</c>): the task's <c>dependencies:</c>
    /// and, for a decision task, its <c>decision.state:</c> (the decision a <c>when:</c> names is a
    /// third, but a conditioned task is skippable already). A dependency that names no task is refused
    /// at load; a cycle is refused by the parallel process only, the sequential one keeping the
    /// declared order: both read as "may be skipped".
    /// <paramref name="seen"/> holds the tasks already walked: one met again was found skippable, or
    /// closes a cycle (a task found safe ends the whole walk, so it is never met twice).
    /// </summary>
    private static bool MayBeSkipped(YamlMappingNode task, Dictionary<string, YamlMappingNode> tasks, HashSet<YamlMappingNode> seen)
    {
        if (!IsNull(Value(task, "when")) || !seen.Add(task))
            return true;

        var all = new List<YamlNode>();
        var state = Value(task, "decision") is YamlMappingNode decision ? Value(decision, "state") : null;
        foreach (var dependencies in new[] { Value(task, "dependencies"), state })
        {
            if (IsNull(dependencies))
                continue;
            if (dependencies is not YamlSequenceNode list)
                return true;
            all.AddRange(list.Children);
        }

        return all.Count > 0 && all.TrueForAll(dependency =>
            Text(dependency) is not { } key || !tasks.TryGetValue(key, out var named) || MayBeSkipped(named, tasks, seen));
    }

    /// <summary>
    /// The <c>graph:</c> block when it describes its nodes or its edges; null when the graph runs the
    /// tasks in a line, each gated like a sequential one (<c>GraphProcessStrategy.BuildCrewGraph</c>).
    /// </summary>
    private static YamlMappingNode? Topology(YamlMappingNode? settings) =>
        settings is not null && Value(settings, "graph") is YamlMappingNode graph
        && (!IsNull(Value(graph, "nodes")) || Value(graph, "edges") is YamlSequenceNode { Children.Count: > 0 })
            ? graph
            : null;

    /// <summary>
    /// The task of the node a described graph starts at (<c>GraphProcessStrategy.BuildDescribedGraph</c>):
    /// the node <c>start:</c> names, else the first of <c>nodes:</c>; without <c>nodes:</c> each task is
    /// its own node, and only a written <c>start:</c> is taken: the engine then starts at the first
    /// task of its own map, an order this reading does not assume. Null when it is not that plain.
    /// </summary>
    private static YamlMappingNode? StartTaskOf(YamlMappingNode graph, Dictionary<string, YamlMappingNode> tasks)
    {
        var start = Text(Value(graph, "start"));
        var taskKey = start;
        if (!IsNull(Value(graph, "nodes")))
        {
            if (Value(graph, "nodes") is not YamlSequenceNode nodes)
                return null;

            var declared = nodes.Children.OfType<YamlMappingNode>().ToList();
            var node = start is null ? declared.FirstOrDefault() : declared.Find(candidate => Text(Value(candidate, "id")) == start);
            taskKey = node is null ? null : Text(Value(node, "task"));
        }

        return taskKey is not null && tasks.TryGetValue(taskKey, out var task) ? task : null;
    }

    /// <summary>
    /// The declaration of the one agent of an <c>agents/</c> file, keyed by its file stem; null when
    /// it declares neither a scope nor a working root.
    /// </summary>
    public static CrewAgentFileSystem? OfAgentFile(string agentKey, string yaml)
    {
        ArgumentNullException.ThrowIfNull(agentKey);
        ArgumentNullException.ThrowIfNull(yaml);

        return TryRoot(yaml, out var root) && root is YamlMappingNode agent ? Read(agentKey, agent) : null;
    }

    private static CrewAgentFileSystem? Read(string key, YamlMappingNode agent)
    {
        // The runtime names an agent by its role, its key when it has none.
        var name = Text(Value(agent, "role")) ?? key;
        var root = Text(Value(agent, "workingRoot") ?? Value(agent, "working_root"));
        var written = Value(agent, "filesystem");

        if (IsNull(written))
            return root is null ? null : new CrewAgentFileSystem(key, name, [], root);

        if (written is not YamlSequenceNode list)
        {
            return Refused(key, name, root,
                $"expected a list, each item a virtual path or a map with path and access; got '{Describe(written!)}'. Write each path as an item: '- /workspace/docs'.");
        }

        if (list.Children.Count == 0)
            return Refused(key, name, root, "the list is empty; declare at least one path, or remove the key to leave the mounts alone to decide.");

        var entries = new List<FileSystemScopeEntry>(list.Children.Count);
        foreach (var item in list.Children)
        {
            switch (item)
            {
                case YamlScalarNode scalar when !IsNull(scalar) && !string.IsNullOrWhiteSpace(scalar.Value):
                    entries.Add(new FileSystemScopeEntry(scalar.Value!.Trim()));
                    break;
                case YamlMappingNode map:
                    if (ReadEntry(map, out var entry, out var why))
                        entries.Add(entry!);
                    else
                        return Refused(key, name, root, why!);
                    break;
                default:
                    return Refused(key, name, root, $"each item is a virtual path or a map with path and access; got '{Describe(item)}'.");
            }
        }

        return new CrewAgentFileSystem(key, name, entries, root);
    }

    private static bool ReadEntry(YamlMappingNode map, out FileSystemScopeEntry? entry, out string? why)
    {
        entry = null;
        string? path = null;
        FileAccessRights? access = null;
        foreach (var (keyNode, valueNode) in map.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value;
            switch (key)
            {
                case "path":
                    path = (valueNode as YamlScalarNode)?.Value;
                    break;
                case "access":
                    try
                    {
                        access = FileSystemScopeEntry.ParseAccess((valueNode as YamlScalarNode)?.Value ?? string.Empty);
                    }
                    catch (FormatException ex)
                    {
                        why = ex.Message;
                        return false;
                    }

                    break;
                default:
                    why = $"unknown key '{key}' in an entry; an entry has path and access.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            why = "an entry has no path.";
            return false;
        }

        why = null;
        entry = new FileSystemScopeEntry(path.Trim(), access);
        return true;
    }

    /// <summary>A YAML value as the loader's refusal quotes it: a scalar as written, a map as its keys.</summary>
    private static string Describe(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value ?? string.Empty,
        YamlMappingNode map => "{ " + string.Join(", ", map.Children.Keys.Select(k => (k as YamlScalarNode)?.Value)) + " }",
        _ => node.ToString(),
    };

    private static CrewAgentFileSystem Refused(string key, string name, string? root, string why) =>
        new(key, name, [], root, $"Agent '{key}' filesystem: {why}");

    /// <summary>Reads <paramref name="path"/> and parses it; false when it is absent or not a crew document the loader takes.</summary>
    private static bool TryReadRoot(string path, out YamlNode? root)
    {
        root = null;
        return File.Exists(path) && TryRoot(File.ReadAllText(path), out root);
    }

    /// <summary>
    /// The document's root node (null for an empty one); false when the text is no YAML the parser
    /// reads, or holds a <c>&lt;&lt;</c> merge key, which the loader refuses (<c>CrewYamlKeyCheck</c>).
    /// </summary>
    private static bool TryRoot(string yaml, out YamlNode? root)
    {
        root = null;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0)
                return true;

            var document = stream.Documents[0];
            if (document.AllNodes.OfType<YamlMappingNode>().Any(map => map.Children.Keys.Any(IsMergeKey)))
                return false;

            root = document.RootNode;
            return true;
        }
        catch (YamlException)
        {
            return false;
        }
    }

    private static bool IsMergeKey(YamlNode key) =>
        key is YamlScalarNode { Value: "<<", Style: ScalarStyle.Plain or ScalarStyle.Any };

    /// <summary>The value at <paramref name="key"/>, compared as the loader compares it (exactly).</summary>
    private static YamlNode? Value(YamlMappingNode map, string key)
    {
        foreach (var (keyNode, value) in map.Children)
        {
            if (keyNode is YamlScalarNode { Value: { } name } && name == key)
                return value;
        }

        return null;
    }

    /// <summary>A scalar's trimmed text; null when absent, null or blank.</summary>
    private static string? Text(YamlNode? node) =>
        node is YamlScalarNode scalar && !IsNull(scalar) && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value!.Trim()
            : null;

    /// <summary>
    /// Whether the node reads as YAML null: absent, an empty plain value (<c>filesystem:</c>), <c>~</c>
    /// or <c>null</c> — no declaration, as the loader reads it.
    /// </summary>
    private static bool IsNull(YamlNode? node) =>
        node is null
        || node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } scalar
            && (string.IsNullOrEmpty(scalar.Value) || scalar.Value is "~" or "null" or "Null" or "NULL");
}
