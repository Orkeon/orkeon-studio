using Orkeon.Domain.FileSystem;
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

    [Fact]
    public void A_folder_crew_keys_its_agents_by_file_stem_and_skips_its_tasks()
    {
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        Directory.CreateDirectory(Path.Combine(_root, "tasks"));
        File.WriteAllText(Path.Combine(_root, "crew.yaml"), "name: team\n");
        File.WriteAllText(Path.Combine(_root, "agents", "analyst.yaml"), "role: Analyst\nfilesystem: [/workspace]\n");
        File.WriteAllText(Path.Combine(_root, "agents", "plain.yaml"), "role: Plain\n");
        File.WriteAllText(Path.Combine(_root, "tasks", "t.yaml"), "agent: analyst\nfilesystem: [/nowhere]\n");

        var agents = CrewFileSystemScopes.In(_root);

        var agent = Assert.Single(agents);
        Assert.Equal(("analyst", "Analyst"), (agent.Key, agent.Name));
        Assert.Equal([new FileSystemScopeEntry("/workspace")], agent.Entries);
    }

    [Fact]
    public void Nothing_readable_declares_nothing()
    {
        Directory.CreateDirectory(_root);
        var broken = Path.Combine(_root, "crew.yaml");
        File.WriteAllText(broken, "agents:\n  a: [unclosed\n");

        Assert.Empty(CrewFileSystemScopes.In(broken));
        Assert.Empty(CrewFileSystemScopes.In(null));
        Assert.Empty(CrewFileSystemScopes.In(Path.Combine(_root, "missing")));
        Assert.Empty(CrewFileSystemScopes.OfCrewFile("agents: [a, b]"));
    }
}
