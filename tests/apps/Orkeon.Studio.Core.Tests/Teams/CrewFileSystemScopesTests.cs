using Microsoft.Extensions.Logging.Abstractions;
using Orkeon.Domain.FileSystem;
using Orkeon.Infrastructure.Configuration;
using Orkeon.Infrastructure.FileSystem;
using Orkeon.Infrastructure.Security;
using Orkeon.Infrastructure.Serialization;
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
    public async Task An_entity_file_counts_whatever_the_case_of_its_extension()
    {
        // The loader matches *.yaml and *.yml ignoring case on every platform: spare.YAML is an agent,
        // so the writer is not the crew's only one and the round-robin may pick the other.
        Write("crew.yaml", "name: team\ngoal: publish\n");
        Write("agents/writer.yaml", "role: Writer\ngoal: write\nbackstory: writes\nfilesystem: [/output]\n");
        var spare = Write("agents/spare.YAML", "role: Spare\ngoal: spare\nbackstory: spares\nfilesystem: [/spare]\n");
        Write("tasks/draft.YML", "description: draft\nexpected_output: a draft\n");

        Assert.Equal([("spare", false), ("writer", false)], Starts(Folder()));
        Assert.Equal(["Spare", "Writer"], await EngineAgentRoles());

        // Alone, the writer runs the task that names nobody; the task file counts under .YML too.
        File.Delete(spare);
        Assert.Equal([("writer", true)], Starts(Folder()));
        Assert.Equal(["Writer"], await EngineAgentRoles());
    }

    [Fact]
    public async Task A_stem_written_under_two_spellings_of_the_extensions_is_refused_at_load()
    {
        Write("crew.yaml", "name: team\ngoal: publish\n");
        Write("agents/writer.yaml", "role: Writer\ngoal: write\nbackstory: writes\nfilesystem: [/output]\n");
        Write("agents/writer.YML", "role: Writer\ngoal: write\nbackstory: writes\n");
        Write("tasks/draft.yaml", "description: draft\nexpected_output: a draft\n");

        Assert.Empty(Folder());
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(EngineAgentRoles);
        Assert.Contains("under both extensions", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The roles of the agents the engine's own loader reads from the crew folder, mounted as it is for a run.</summary>
    private async Task<List<string>> EngineAgentRoles()
    {
        using var registry = new FileSystemRegistry([FileSystemMount.Parse($"{_root}:/crew:ro")]);
        var crew = await EngineLoader(registry, _root).LoadFromDirectoryAsync("/crew", TestContext.Current.CancellationToken);
        return crew.Agents!.Select(agent => agent.Role).ToList();
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

        Assert.Equal([("writer", true), ("spare", false)], Starts(agents));
    }

    private static IEnumerable<(string Key, bool StartsForSure)> Starts(IEnumerable<CrewAgentFileSystem> agents) =>
        agents.Select(agent => (agent.Key, agent.StartsForSure));

    /// <summary>A crew file: its settings lines, agents that each declare a scope, and its tasks block.</summary>
    private static IEnumerable<(string Key, bool StartsForSure)> StartsOf(string settings, string[] agents, string tasks)
    {
        var yaml = settings
            + "agents:\n" + string.Concat(agents.Select(agent => $"  {agent}:\n    role: {agent}\n    filesystem: [/output]\n"))
            + tasks;
        return Starts(CrewFileSystemScopes.OfCrewFile(yaml));
    }

    private const string OneTaskForWriter = "tasks:\n  draft:\n    agent: writer\n";
    private const string OneTaskForNobody = "tasks:\n  draft:\n    description: draft\n";

    [Theory]
    [InlineData("")]
    [InlineData("process: sequential\n")]
    [InlineData("process: parallel\n")]
    [InlineData("process: graph\n")]
    [InlineData("process: '  Parallel  '\n")]
    [InlineData("process: SEQUENTIAL\n")]
    public void A_process_that_honours_the_tasks_agent_starts_it(string settings)
    {
        Assert.Equal(
            [("writer", true), ("spare", false)],
            StartsOf(settings, ["writer", "spare"], OneTaskForWriter));
    }

    [Fact]
    public void An_unknown_process_starts_nobody_for_the_crew_does_not_load()
    {
        Assert.Equal([("writer", false)], StartsOf("process: graf\n", ["writer"], OneTaskForWriter));
    }

    [Fact]
    public void A_task_a_condition_may_skip_does_not_start_its_agent()
    {
        var tasks = """
            tasks:
              triage:
                decision:
                  questions:
                    urgent:
                      noul: It is urgent
              escalate:
                agent: writer
                dependencies: [triage]
                when:
                  triage.urgent: yes
              report:
                agent: editor
                dependencies: [escalate]
              archive:
                agent: clerk
                dependencies: [triage, escalate]

            """;

        // escalate hangs on the decision; report joins skipped dependencies only, so it is skipped with
        // them (ConditionGate); archive also depends on the decision task, which always runs.
        Assert.Equal(
            [("writer", false), ("editor", false), ("clerk", true)],
            StartsOf("", ["writer", "editor", "clerk"], tasks));
    }

    [Fact]
    public void A_decision_task_starts_no_agent()
    {
        // The decision runner answers it: the round-robin gives it nobody, even in a one-agent crew.
        var tasks = "tasks:\n  triage:\n    decision:\n      questions:\n        urgent:\n          noul: It is urgent\n";

        Assert.Equal([("writer", false)], StartsOf("", ["writer"], tasks));
    }

    [Theory]
    [InlineData("")]
    [InlineData("process: parallel\n")]
    [InlineData("process: graph\n")]
    public void The_only_agent_of_a_crew_runs_the_tasks_that_name_nobody(string settings)
    {
        // The round-robin over one agent always picks it (TaskAgentSelection).
        Assert.Equal([("writer", true)], StartsOf(settings, ["writer"], OneTaskForNobody));
        // Among several, the pick is the selector's: nobody for sure.
        Assert.Equal(
            [("writer", false), ("spare", false)],
            StartsOf(settings, ["writer", "spare"], OneTaskForNobody));
    }

    [Fact]
    public void A_crew_without_a_task_starts_nobody()
    {
        Assert.Equal([("writer", false)], StartsOf("", ["writer"], ""));
        Assert.Equal([("writer", false)], StartsOf("process: consensual\n", ["writer"], ""));
        Assert.Equal([("writer", false)], StartsOf("process: autonomous\n", ["writer"], ""));
    }

    [Fact]
    public async Task A_described_graph_starts_only_the_agent_of_its_start_node()
    {
        (string, bool)[] reviewFirst = [("writer", false), ("editor", true)];
        (string, bool)[] draftFirst = [("writer", true), ("editor", false)];
        (string, bool)[] nobody = [("writer", false), ("editor", false)];
        const string Nodes = "  nodes:\n    - id: a\n      task: draft\n    - id: b\n      task: review\n";

        // Each graph is one the engine loads (GraphTopologyValidator): every node has a way out, is
        // reached from START and reaches END. start: names the node; without it the first node starts.
        Assert.Equal(reviewFirst, await StartsOfLoadedGraph("  start: b\n" + Nodes + Edges(("b", "a"), ("a", "END"))));
        Assert.Equal(draftFirst, await StartsOfLoadedGraph(Nodes + Edges(("a", "b"), ("b", "END"))));
        // Edges alone: each task is its own node, and a written start: tells which is first.
        Assert.Equal(reviewFirst, await StartsOfLoadedGraph("  start: review\n" + Edges(("review", "draft"), ("draft", "END"))));
        // Without start: the engine starts at the first task of its own map, an order Studio does
        // not assume: nobody for sure, a refusal there being a warning.
        Assert.Equal(nobody, await StartsOfLoadedGraph(Edges(("draft", "review"), ("review", "END"))));
        // A graph block that describes neither nodes nor edges runs the tasks in a line.
        Assert.Equal([("writer", true), ("editor", true)], await StartsOfLoadedGraph("  maxVisits: 3\n"));
    }

    private static string Edges(params (string From, string To)[] edges) =>
        "  edges:\n" + string.Concat(edges.Select(edge => $"    - from: {edge.From}\n      to: {edge.To}\n"));

    /// <summary>
    /// What Studio says of a graph crew of two agents (writer drafts, editor reviews) whose
    /// <c>graph:</c> block is <paramref name="graph"/>, once the engine's own loader has taken the
    /// crew: a fixture the engine refuses at load would prove nothing of a run.
    /// </summary>
    private static async Task<IEnumerable<(string Key, bool StartsForSure)>> StartsOfLoadedGraph(string graph)
    {
        var yaml = "name: team\ngoal: publish\nprocess: graph\ngraph:\n" + graph + """
            agents:
              writer:
                role: writer
                goal: write
                backstory: writes
                filesystem: [/output]
              editor:
                role: editor
                goal: edit
                backstory: edits
                filesystem: [/output]
            tasks:
              draft:
                description: draft
                expected_output: a draft
                agent: writer
              review:
                description: review
                expected_output: a review
                agent: editor

            """;

        using var registry = new FileSystemRegistry([]);
        var loader = EngineLoader(registry);
        var crew = await loader.LoadFromStringAsync(yaml, TestContext.Current.CancellationToken);
        Assert.Empty(loader.Validate(crew).Errors);

        return Starts(CrewFileSystemScopes.OfCrewFile(yaml));
    }

    /// <summary>The engine's crew loader over <paramref name="registry"/>'s mounts, its workspace at <paramref name="workspace"/>.</summary>
    private static YamlCrewDefinitionLoader EngineLoader(FileSystemRegistry registry, string? workspace = null) =>
        new(
            new YamlDotNetSerializer(),
            new FileSystemService(
                registry,
                new PathValidator(new PathSecurityOptions { DefaultWorkspaceRoot = workspace }, NullLogger<PathValidator>.Instance),
                NullLogger<FileSystemService>.Instance),
            NullLogger<YamlCrewDefinitionLoader>.Instance);

    [Fact]
    public void A_manager_never_starts_and_its_only_worker_always_does()
    {
        // HierarchicalProcessStrategy removes the manager from the workers and never runs it as an
        // agent; the task's agent: is ignored, but one worker leaves the manager no choice.
        Assert.Equal(
            [("boss", false), ("writer", true)],
            StartsOf("process: hierarchical\nmanagerAgent: boss\n", ["boss", "writer"], OneTaskForNobody));
        Assert.Equal(
            [("boss", false), ("writer", true)],
            StartsOf("process: Hierarchical\nmanager_agent: boss\n", ["boss", "writer"], OneTaskForNobody));
    }

    [Fact]
    public void A_manager_with_several_workers_picks_whom_it_wants_whatever_the_task_names()
    {
        Assert.Equal(
            [("boss", false), ("writer", false), ("spare", false)],
            StartsOf("process: hierarchical\nmanagerAgent: boss\n", ["boss", "writer", "spare"], OneTaskForWriter));
    }

    [Fact]
    public void An_autonomous_crew_starts_its_only_agent_and_ignores_the_tasks_agent_among_several()
    {
        Assert.Equal([("writer", true)], StartsOf("process: autonomous\n", ["writer"], OneTaskForNobody));
        Assert.Equal(
            [("writer", false), ("spare", false)],
            StartsOf("process: autonomous\n", ["writer", "spare"], OneTaskForWriter));
    }

    [Fact]
    public void A_consensual_crew_starts_every_agent_but_its_manager()
    {
        // Every voter answers the first task, whatever its agent:, and a refused one fails the run
        // (ConsensualProcessStrategy); the manager at most casts a deciding ballot, a refusal there
        // being an abstention.
        Assert.Equal(
            [("boss", false), ("writer", true), ("editor", true)],
            StartsOf("process: consensual\nmanagerAgent: boss\n", ["boss", "writer", "editor"], OneTaskForWriter));
        Assert.Equal(
            [("writer", true), ("editor", true)],
            StartsOf("process: Consensual\n", ["writer", "editor"], OneTaskForNobody));
    }

    [Fact]
    public void A_voter_that_declares_nothing_leaves_the_other_voters_started()
    {
        var agents = CrewFileSystemScopes.OfCrewFile("""
            process: consensual
            agents:
              writer:
                role: Writer
                filesystem: [/output]
              free:
                role: Free
            tasks:
              draft:
                description: draft
            """);

        Assert.Equal([("writer", true)], Starts(agents));
    }

    [Fact]
    public void A_task_behind_a_decision_whose_state_may_be_skipped_does_not_start_its_agent()
    {
        // The engine adds a decision's state: to its dependencies (YamlCrewMapper.MapTasks): gate is
        // skipped with check, its only one, and publish with gate (ConditionGate's join rule).
        var tasks = """
            tasks:
              triage:
                decision:
                  questions:
                    urgent:
                      noul: It is urgent
              check:
                agent: writer
                dependencies: [triage]
                when:
                  triage.urgent: yes
              gate:
                decision:
                  state: [check]
                  questions:
                    ready:
                      noul: It is ready
              publish:
                agent: editor
                dependencies: [gate]
              archive:
                agent: clerk
                dependencies: [gate, triage]

            """;

        Assert.Equal(
            [("writer", false), ("editor", false), ("clerk", true)],
            StartsOf("", ["writer", "editor", "clerk"], tasks));

        // A state: that is no list is refused at load: nobody for sure behind it.
        Assert.Equal(
            [("writer", false)],
            StartsOf("", ["writer"], "tasks:\n  gate:\n    decision:\n      state: draft\n  publish:\n    agent: writer\n    dependencies: [gate]\n"));
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
