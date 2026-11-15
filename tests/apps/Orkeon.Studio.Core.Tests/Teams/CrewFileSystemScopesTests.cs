using Orkeon.Domain.FileSystem;
using Orkeon.Studio.Core.Targets;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Tests.Teams;

/// <summary>
/// #349: the <c>filesystem:</c> scope and working root of each agent of a YAML crew, read with the
/// engine loader's shape rules so the launchers can predict the refusal at start — a reading that
/// never throws.
/// </summary>
public sealed class CrewFileSystemScopesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkeon-crew-fs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public void A_crew_file_gives_each_declaring_agent_its_entries_and_its_runtime_name()
    {
        var agents = CrewFileSystemScopes.OfCrewFile("""
            agents:
              researcher:
                role: Researcher
                filesystem:
                  - /workspace/docs
                  - path: /output
                    access: rw
              writer:
                goal: write
                working_root: /output/drafts
                filesystem: [{ path: /output, access: RWND }]
              idle:
                role: Idle
            """);

        Assert.Equal(2, agents.Count);
        var researcher = agents[0];
        Assert.Equal("researcher", researcher.Key);
        Assert.Equal("Researcher", researcher.Name);
        Assert.Null(researcher.Refusal);
        Assert.Equal(
            [new FileSystemScopeEntry("/workspace/docs"), new FileSystemScopeEntry("/output", FileAccessRights.ReadWrite)],
            researcher.Entries);

        var writer = agents[1];
        Assert.Equal("writer", writer.Name);
        Assert.Equal("/output/drafts", writer.WorkingRoot);
        Assert.Equal([new FileSystemScopeEntry("/output", FileAccessRights.ReadWriteNoDelete)], writer.Entries);
    }

    [Theory]
    [InlineData("filesystem:", null)]
    [InlineData("filesystem: ~", null)]
    [InlineData("filesystem: null", null)]
    [InlineData("workingRoot: /workspace", "/workspace")]
    public void A_null_scope_declares_nothing_and_a_working_root_alone_is_kept(string line, string? root)
    {
        var agent = CrewFileSystemScopes.OfAgentFile("a", "role: A\n" + line + "\n");

        Assert.Equal(root, agent?.WorkingRoot);
        Assert.True(agent is null || (agent.Entries.Count == 0 && agent.Refusal is null));
    }

    [Theory]
    [InlineData("filesystem: /workspace", "expected a list")]
    [InlineData("filesystem: []", "the list is empty")]
    [InlineData("filesystem:\n  - path: /workspace\n    mode: rw", "unknown key 'mode'")]
    [InlineData("filesystem:\n  - path: /workspace\n    access: write", "Unknown access value: 'write'")]
    [InlineData("filesystem:\n  - access: ro", "an entry has no path")]
    [InlineData("filesystem:\n  - [/a, /b]", "each item is a virtual path or a map")]
    public void A_scope_the_loader_refuses_is_refused_by_agent_key(string scope, string reason)
    {
        var agent = CrewFileSystemScopes.OfAgentFile("analyst", "role: Analyst\n" + scope + "\n");

        Assert.NotNull(agent);
        Assert.Empty(agent.Entries);
        Assert.StartsWith("Agent 'analyst' filesystem: ", agent.Refusal);
        Assert.Contains(reason, agent.Refusal);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static RunTarget Target(RunTargetKind kind, string runPath) =>
        new() { Kind = kind, SelectedPath = runPath, RunPath = runPath };

    private IReadOnlyList<CrewAgentFileSystem> Folder() =>
        CrewFileSystemScopes.Of(Target(RunTargetKind.MultiFileCrewDirectory, _root));

    [Fact]
    public void A_folder_crew_keys_its_agents_by_file_stem_and_reads_their_tasks()
    {
        Write("crew.yaml", "name: team\n");
        Write("agents/analyst.yaml", "role: Analyst\nfilesystem: [/workspace]\n");
        Write("agents/plain.yaml", "role: Plain\n");
        Write("agents/spare.yaml", "role: Spare\nfilesystem: [/spare]\n");
        Write("tasks/t.yaml", "agent: analyst\nfilesystem: [/nowhere]\n");

        var agents = Folder();

        Assert.Equal(2, agents.Count);
        Assert.Equal(("analyst", "Analyst", true), (agents[0].Key, agents[0].Name, agents[0].StartsForSure));
        Assert.Equal([new FileSystemScopeEntry("/workspace")], agents[0].Entries);
        // No task names it: only the course of the run may start it.
        Assert.Equal(("spare", false), (agents[1].Key, agents[1].StartsForSure));
    }

    [Fact]
    public void A_crew_file_starts_the_agents_its_tasks_name()
    {
        var path = Write("team.yaml", """
            agents:
              writer:
                role: Writer
                filesystem: [/output]
              spare:
                role: Spare
                filesystem: [/spare]
            tasks:
              draft:
                agent: writer
            """);

        var agents = CrewFileSystemScopes.Of(Target(RunTargetKind.YamlFile, path));

        Assert.Equal([("writer", true), ("spare", false)], agents.Select(a => (a.Key, a.StartsForSure)));
    }

    [Fact]
    public void A_manager_starts_for_sure_and_its_crew_picks_the_rest()
    {
        var agents = CrewFileSystemScopes.OfCrewFile("""
            process: hierarchical
            managerAgent: boss
            agents:
              boss:
                role: Boss
                filesystem: [/workspace]
              writer:
                role: Writer
                filesystem: [/output]
            tasks:
              draft:
                agent: writer
            """);

        Assert.Equal([("boss", true), ("writer", false)], agents.Select(a => (a.Key, a.StartsForSure)));
    }

    [Fact]
    public void A_flat_crew_reads_its_agents_map()
    {
        Write("crew.yaml", "name: team\n");
        Write("agents.yaml", "writer:\n  role: Writer\n  filesystem: [/output]\n");
        Write("tasks.yaml", "draft:\n  agent: writer\n");

        var agent = Assert.Single(Folder());

        Assert.Equal(("writer", "Writer", true), (agent.Key, agent.Name, agent.StartsForSure));
    }

    [Fact]
    public void A_per_entity_crew_ignores_an_agents_map_in_its_settings()
    {
        Write("config.yaml", "name: team\nagents:\n  ghost:\n    role: Ghost\n    filesystem: [/ghost]\n");
        Write("agents/writer.yaml", "role: Writer\nfilesystem: [/output]\n");

        Assert.Equal(["writer"], Folder().Select(a => a.Key));
    }

    [Fact]
    public void A_crew_file_in_a_folder_named_agents_is_read_as_a_crew_file()
    {
        var path = Write("agents/crew.yaml", "agents:\n  writer:\n    role: Writer\n    filesystem: [/output]\n");

        var agent = Assert.Single(CrewFileSystemScopes.Of(Target(RunTargetKind.SingleFileCrewDirectory, path)));

        Assert.Equal("writer", agent.Key);
    }

    [Fact]
    public void A_crew_the_loader_refuses_declares_nothing()
    {
        // A merge key: refused at load, before any scope is read.
        Assert.Empty(CrewFileSystemScopes.OfCrewFile("""
            base: &base
              filesystem: [/output]
            agents:
              writer:
                <<: *base
                role: Writer
            """));

        // A stem written under both extensions.
        Write("crew.yaml", "name: team\n");
        Write("agents/writer.yaml", "role: Writer\nfilesystem: [/output]\n");
        Write("agents/writer.yml", "role: Writer\n");
        Assert.Empty(Folder());
        File.Delete(Path.Combine(_root, "agents", "writer.yml"));
        Assert.Single(Folder());

        // A flat file beside its per-entity folder.
        Write("agents.yaml", "other:\n  role: Other\n");
        Assert.Empty(Folder());
    }

    [Fact]
    public void Nothing_readable_declares_nothing()
    {
        var broken = Write("crew.yaml", "agents:\n  a: [unclosed\n");
        var script = Write("crew.ork.ts", "export default {};\n");

        Assert.Empty(CrewFileSystemScopes.Of(Target(RunTargetKind.YamlFile, broken)));
        Assert.Empty(CrewFileSystemScopes.Of(null));
        Assert.Empty(CrewFileSystemScopes.Of(Target(RunTargetKind.ScriptFile, script)));
        Assert.Empty(CrewFileSystemScopes.Of(Target(RunTargetKind.YamlFile, Path.Combine(_root, "missing.yaml"))));
        Assert.Empty(CrewFileSystemScopes.OfCrewFile("agents: [a, b]"));
        // A folder crew with no settings file is refused at load.
        Write("sub/agents/writer.yaml", "role: Writer\nfilesystem: [/output]\n");
        Assert.Empty(CrewFileSystemScopes.Of(Target(RunTargetKind.MultiFileCrewDirectory, Path.Combine(_root, "sub"))));
    }
}
