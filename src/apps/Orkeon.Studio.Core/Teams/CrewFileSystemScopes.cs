using Orkeon.Compliance.Vfs;
using Orkeon.Domain.FileSystem;
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
public sealed record CrewAgentFileSystem(
    string Key,
    string Name,
    IReadOnlyList<FileSystemScopeEntry> Entries,
    string? WorkingRoot,
    string? Refusal = null);

/// <summary>
/// The file-system declarations of the agents of a YAML crew (#349), read so the launchers can tell,
/// before the run, that the runtime will refuse it at start: a scope entry under no mount of the run,
/// above the mount's rights, or a working root outside the scope (<c>FileSystemScopePreflight</c>).
/// <para>
/// A reading on the shape the engine's crew loader takes — a single file whose <c>agents:</c> map names
/// each agent, or an <c>agents/</c> folder of one agent per file — and the same keys
/// (<c>filesystem</c>, <c>role</c>, <c>workingRoot</c> or <c>working_root</c>). The entries' shape is
/// judged with the loader's rules (<c>YamlCrewMapper.MapFileSystemScope</c>). It never throws: a file
/// that cannot be read or parsed declares nothing here, and the engine says what is wrong with it. A
/// <c>.ork.ts</c> crew is not read.
/// </para>
/// </summary>
[SuppressVfsCompliance(
    "EXCEPTION-BOOTSTRAP: Studio is a host application; a team's crew is user-owned storage on the " +
    "physical disk, read as the team catalog reads it.")]
public static class CrewFileSystemScopes
{
    /// <summary>
    /// The agents of the crew behind <paramref name="targetPath"/> that declare a scope or a working
    /// root, in the files' order. Empty when there is none, when the crew is a script, or when nothing
    /// can be read.
    /// </summary>
    public static IReadOnlyList<CrewAgentFileSystem> In(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return [];

        try
        {
            var agents = new List<CrewAgentFileSystem>();
            foreach (var file in CrewLlmDeclarations.YamlFilesOf(targetPath))
            {
                var folder = Path.GetFileName(Path.GetDirectoryName(file));
                // A task file names its agent, it declares none.
                if (string.Equals(folder, "tasks", StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = File.ReadAllText(file);
                var found = string.Equals(folder, "agents", StringComparison.OrdinalIgnoreCase)
                    ? OfAgentFile(Path.GetFileNameWithoutExtension(file), text) is { } agent ? [agent] : []
                    : OfCrewFile(text);
                foreach (var declared in found)
                {
                    // One agent per key: the agents/ file the loader reads wins over a crew file's copy.
                    agents.RemoveAll(existing => existing.Key == declared.Key);
                    agents.Add(declared);
                }
            }

            return agents;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>The agents of a crew file's <c>agents:</c> map that declare a scope or a working root.</summary>
    public static IReadOnlyList<CrewAgentFileSystem> OfCrewFile(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (Root(yaml) is not YamlMappingNode root || Value(root, "agents") is not YamlMappingNode agents)
            return [];

        var result = new List<CrewAgentFileSystem>();
        foreach (var (key, node) in agents.Children)
        {
            if (key is YamlScalarNode { Value: { Length: > 0 } agentKey } && node is YamlMappingNode agent
                && Read(agentKey, agent) is { } declared)
            {
                result.Add(declared);
            }
        }

        return result;
    }

    /// <summary>
    /// The declaration of the one agent of an <c>agents/</c> file, keyed by its file stem; null when
    /// it declares neither a scope nor a working root.
    /// </summary>
    public static CrewAgentFileSystem? OfAgentFile(string agentKey, string yaml)
    {
        ArgumentNullException.ThrowIfNull(agentKey);
        ArgumentNullException.ThrowIfNull(yaml);

        return Root(yaml) is YamlMappingNode agent ? Read(agentKey, agent) : null;
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

    /// <summary>The document's root node; null when the text is no YAML the parser reads.</summary>
    private static YamlNode? Root(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.Count > 0 ? stream.Documents[0].RootNode : null;
        }
        catch (YamlException)
        {
            return null;
        }
    }

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
