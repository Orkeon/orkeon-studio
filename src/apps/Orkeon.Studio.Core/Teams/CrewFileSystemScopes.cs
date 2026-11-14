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
/// Whether the run starts the agent whatever its course: the <c>managerAgent:</c>, or the agent a task
/// names under a process that gives each task its own agent (sequential, parallel, graph). An agent a
/// manager or a planner may pick, or none, is started only if it is picked.
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
/// folder (<c>agents/</c> or <c>tasks/</c>) whose <c>agents/*.yaml</c> and <c>*.yml</c> files are
/// one agent each, keyed by file stem, beside <c>config.yaml</c> (else <c>crew.yaml</c>), which holds
/// settings only; or the flat triplet, whose <c>agents.yaml</c> and <c>tasks.yaml</c> are the agent
/// and task maps themselves. The keys are the loader's (<c>filesystem</c>, <c>role</c>,
/// <c>workingRoot</c> or <c>working_root</c>), and the entries' shape is judged with its rules
/// (<c>YamlCrewMapper.MapFileSystemScope</c>).
/// </para>
/// <para>
/// It never throws, and declares nothing where the engine refuses the crew at load with its own
/// message: a file that cannot be read or parsed, a missing settings file, a mixed layout, an entity
/// written under both extensions, a <c>&lt;&lt;</c> merge key. A <c>.ork.ts</c> crew is not read.
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

        var agents = Value(crew, "agents") as YamlMappingNode;
        var tasks = (Value(crew, "tasks") as YamlMappingNode)?.Children.Values ?? [];
        return Collect(Entries(agents), tasks, crew);
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
                (taskMap as YamlMappingNode)?.Children.Values ?? [],
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

        var tasks = new List<YamlNode>(taskFiles.Count);
        foreach (var (_, path) in taskFiles)
        {
            if (!TryReadRoot(path, out var task))
                return [];
            if (task is not null)
                tasks.Add(task);
        }

        return Collect(agents, tasks, settings as YamlMappingNode);
    }

    /// <summary>
    /// The <c>*.yaml</c> and <c>*.yml</c> files of an entity folder, sorted ordinally, with their stems;
    /// empty when the folder is absent, null when a stem is written under both extensions.
    /// </summary>
    private static List<(string Stem, string Path)>? EntityFiles(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        var paths = Directory.EnumerateFiles(folder, "*.yaml")
            .Concat(Directory.EnumerateFiles(folder, "*.yml"))
            .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        var files = paths.Select(path => (Stem: Path.GetFileNameWithoutExtension(path), Path: path)).ToList();
        return files.DistinctBy(file => file.Stem, StringComparer.Ordinal).Count() == files.Count ? files : null;
    }

    /// <summary>The entries of an <c>agents:</c> map, in its order.</summary>
    private static IEnumerable<KeyValuePair<string, YamlNode?>> Entries(YamlMappingNode? agents) =>
        agents is null
            ? []
            : agents.Children
                .Where(child => child.Key is YamlScalarNode { Value.Length: > 0 })
                .Select(child => new KeyValuePair<string, YamlNode?>(((YamlScalarNode)child.Key).Value!, child.Value));

    /// <summary>
    /// The declarations of <paramref name="agents"/>, each told whether the run starts it for sure:
    /// the crew's manager, or an agent a task names under a process that honours the task's agent.
    /// </summary>
    private static List<CrewAgentFileSystem> Collect(
        IEnumerable<KeyValuePair<string, YamlNode?>> agents, IEnumerable<YamlNode> tasks, YamlMappingNode? settings)
    {
        var starting = new HashSet<string>(StringComparer.Ordinal);
        if (settings is not null && Text(Value(settings, "managerAgent") ?? Value(settings, "manager_agent")) is { } manager)
            starting.Add(manager);

        if (HonoursTaskAgents(settings is null ? null : Text(Value(settings, "process"))))
        {
            foreach (var task in tasks.OfType<YamlMappingNode>())
            {
                if (Text(Value(task, "agent")) is { } assigned)
                    starting.Add(assigned);
            }
        }

        var result = new List<CrewAgentFileSystem>();
        foreach (var (key, node) in agents)
        {
            if (node is YamlMappingNode agent && Read(key, agent) is { } declared)
                result.Add(declared with { StartsForSure = starting.Contains(key) });
        }

        return result;
    }

    /// <summary>
    /// Whether the process gives each task the agent it names (sequential, the default, parallel and
    /// graph); a hierarchical manager or an autonomous planner picks its own, a consensual crew deliberates.
    /// </summary>
    private static bool HonoursTaskAgents(string? process)
    {
        if (process is null)
            return true;

        return ProcessType.TryFrom(process, out var type)
            && (type == ProcessType.Sequential || type == ProcessType.Parallel || type == ProcessType.Graph);
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
