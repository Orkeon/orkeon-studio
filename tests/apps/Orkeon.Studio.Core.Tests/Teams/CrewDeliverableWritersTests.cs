using Orkeon.Studio.Core.Targets;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Teams;

/// <summary>
/// Orkeon #392: a task whose agent must write the deliverable with <c>file_write</c>, and does not
/// hold it nor another way to deliver, is found before the run, and only where the crew tells who
/// runs the task.
/// </summary>
public sealed class CrewDeliverableWritersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkeon-crew-deliverable-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public void A_task_that_must_write_a_file_with_a_reading_agent_is_named_with_its_agent_and_its_file()
    {
        // The crew of the report: the reader holds reading tools only, the writer holds file_write.
        var missing = CrewDeliverableWriters.OfCrewFile("""
            agents:
              analyst:
                role: Analyst
                tools: [directory_read, file_read, grep]
              writer:
                role: Writer
                tools: [file_read, file_write]
            tasks:
              read:
                agent: analyst
                deliverable:
                  path: /output/notes.md
              report:
                agent: writer
                deliverable:
                  path: /output/report.md
                  source: tool_call
            """);

        Assert.Equal([new CrewDeliverableWithoutWriter("read", "analyst", "/output/notes.md")], missing);
    }

    [Theory]
    [InlineData("tools: [file_write]", "")]
    [InlineData("tools: [FILE_WRITE]", "")]
    [InlineData("tools: [file_read]", "tools: [file_write]")]
    // Another tool that creates a file at a path the agent gives.
    [InlineData("tools: [docx_writer]", "")]
    [InlineData("tools: [xlsx_writer]", "")]
    [InlineData("tools: [image_generation]", "")]
    [InlineData("tools: [firefox]", "")]
    [InlineData("tools: [shell_command]", "")]
    // A tool Studio's catalogue does not know (an MCP server's) may write too.
    [InlineData("tools: [file_read, acme_write_file]", "")]
    public void File_write_or_another_way_to_deliver_is_enough(string agentTools, string taskTools)
    {
        Assert.Empty(CrewDeliverableWriters.OfCrewFile($$"""
            agents:
              analyst:
                role: Analyst
                {{agentTools}}
            tasks:
              read:
                agent: analyst
                {{taskTools}}
                deliverable:
                  path: /output/notes.md
            """));
    }

    [Theory]
    // edit_file changes a file that exists and refuses one that does not.
    [InlineData("tools: [file_read, edit_file]")]
    [InlineData("tools: [edit_batch, grep]")]
    [InlineData("tools: []")]
    public void A_tool_that_cannot_create_the_file_is_not_enough(string agentTools)
    {
        Assert.Single(CrewDeliverableWriters.OfCrewFile($$"""
            agents:
              analyst:
                role: Analyst
                {{agentTools}}
            tasks:
              read:
                agent: analyst
                deliverable:
                  path: /output/notes.md
            """));
    }

    [Theory]
    [InlineData("final_message")]
    [InlineData("Final_Message")]
    [InlineData("structured_output")]
    [InlineData("none")]
    public void A_deliverable_the_framework_persists_or_none_needs_no_tool(string source)
    {
        Assert.Empty(CrewDeliverableWriters.OfCrewFile($$"""
            agents:
              analyst:
                role: Analyst
            tasks:
              read:
                agent: analyst
                deliverable:
                  path: /output/notes.md
                  source: {{source}}
            """));
    }

    [Fact]
    public void An_agent_without_a_tools_list_holds_no_tool_and_a_task_without_a_path_delivers_nothing()
    {
        var missing = CrewDeliverableWriters.OfCrewFile("""
            agents:
              analyst:
                role: Analyst
            tasks:
              read:
                deliverable:
                  path: /output/notes.md
                  source: ToolCall
              idle:
                deliverable:
                  format: markdown
              plain:
                description: no deliverable
            """);

        // A one-agent crew: the round-robin can only pick that agent.
        Assert.Equal([new CrewDeliverableWithoutWriter("read", "analyst", "/output/notes.md")], missing);
    }

    [Theory]
    // Two agents and no agent: line: the round-robin picks.
    [InlineData("process: sequential", "")]
    // Two workers: the manager or the planner hands the task out whatever its agent: line.
    [InlineData("process: hierarchical", "agent: analyst")]
    [InlineData("process: hierarchical\nmanager_agent: boss", "agent: analyst")]
    [InlineData("process: autonomous", "agent: analyst")]
    // Every voter answers; the engine is left to judge.
    [InlineData("process: consensual", "agent: analyst")]
    // Refused at load.
    [InlineData("process: nonsense", "agent: analyst")]
    // An agent the crew does not declare is refused at load.
    [InlineData("process: sequential", "agent: ghost")]
    // A decision task is answered by the decision runner.
    [InlineData("process: sequential", "agent: analyst\n    decision: { questions: {} }")]
    public void Nothing_is_said_where_the_crew_does_not_tell_who_writes(string process, string task)
    {
        Assert.Empty(CrewDeliverableWriters.OfCrewFile($$"""
            {{process}}
            agents:
              analyst:
                role: Analyst
              writer:
                role: Writer
                tools: [file_write]
              boss:
                role: Boss
            tasks:
              read:
                {{task}}
                deliverable:
                  path: /output/notes.md
            """));
    }

    [Theory]
    [InlineData("process: parallel")]
    [InlineData("process: graph")]
    [InlineData("process: Sequential")]
    public void The_agent_a_task_names_runs_it_under_a_parallel_or_a_graph_process_too(string process)
    {
        var missing = CrewDeliverableWriters.OfCrewFile($$"""
            {{process}}
            agents:
              analyst:
                role: Analyst
              writer:
                role: Writer
                tools: [file_write]
            tasks:
              read:
                agent: analyst
                deliverable:
                  path: /output/notes.md
            """);

        Assert.Equal("analyst", Assert.Single(missing).Agent);
    }

    [Theory]
    [InlineData("managerAgent")]
    [InlineData("manager_agent")]
    public void A_hierarchical_crew_with_one_worker_is_judged_on_that_worker(string managerKey)
    {
        var missing = CrewDeliverableWriters.OfCrewFile($$"""
            process: hierarchical
            {{managerKey}}: boss
            agents:
              boss:
                role: Boss
                tools: [file_write]
              analyst:
                role: Analyst
            tasks:
              read:
                agent: boss
                deliverable:
                  path: /output/notes.md
            """);

        Assert.Equal("analyst", Assert.Single(missing).Agent);
    }

    [Fact]
    public void A_crew_folder_is_read_as_a_crew_file_is()
    {
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        Directory.CreateDirectory(Path.Combine(_root, "tasks"));
        File.WriteAllText(Path.Combine(_root, "crew.yaml"), "name: report\n");
        File.WriteAllText(Path.Combine(_root, "agents", "analyst.yaml"), "role: Analyst\ntools:\n  - file_read\n");
        File.WriteAllText(Path.Combine(_root, "tasks", "read.yaml"), "agent: analyst\ndeliverable:\n  path: /output/notes.md\n");

        var target = new RunTarget { Kind = RunTargetKind.MultiFileCrewDirectory, SelectedPath = _root, RunPath = _root };
        Assert.Equal([new CrewDeliverableWithoutWriter("read", "analyst", "/output/notes.md")], CrewDeliverableWriters.Of(target));

        File.WriteAllText(Path.Combine(_root, "agents", "analyst.yaml"), "role: Analyst\ntools:\n  - file_write\n");
        Assert.Empty(CrewDeliverableWriters.Of(target));
        Assert.Empty(CrewDeliverableWriters.Of(null));
        Assert.Empty(CrewDeliverableWriters.OfCrewFile("agents: [not a map"));
    }

    [Fact]
    public void The_flat_triplet_is_read_and_a_script_is_not()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "crew.yaml"), "name: report\n");
        File.WriteAllText(Path.Combine(_root, "agents.yaml"), "analyst:\n  role: Analyst\n");
        File.WriteAllText(Path.Combine(_root, "tasks.yaml"), "read:\n  agent: analyst\n  deliverable:\n    path: /output/notes.md\n");

        var target = new RunTarget { Kind = RunTargetKind.MultiFileCrewDirectory, SelectedPath = _root, RunPath = _root };
        Assert.Equal("read", Assert.Single(CrewDeliverableWriters.Of(target)).Task);

        var script = Path.Combine(_root, "crew.ork.ts");
        File.WriteAllText(script, "export default {};\n");
        Assert.Empty(CrewDeliverableWriters.Of(new RunTarget
        {
            Kind = RunTargetKind.ScriptFile, SelectedPath = script, RunPath = script,
        }));
    }

    [Fact]
    public void The_engine_still_asks_for_file_write_and_still_reads_no_source_as_a_tool_call()
    {
        // Text-level guards on the Orkeon checkout: the warning stands on these two facts.
        const string wayOut =
            " CrewDeliverableWriters warns because the engine loads a crew whose agent does not hold the"
            + " file_write its prompt asks for (Orkeon #392). If the engine now refuses or repairs that case, drop or"
            + " reword the warning (launch tab, Run launcher, catalogues, docs), then update this guard.";

        var composer = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src/core/Orkeon.Application/Crew/Execution/AgentPromptComposer.cs"));
        Assert.True(composer.Contains("using the file_write tool", StringComparison.Ordinal),
            "The prompt no longer tells the agent to write its deliverable with file_write." + wayOut);

        var mapper = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src/core/Orkeon.Infrastructure/Configuration/Yaml/YamlCrewMapper.cs"));
        Assert.True(mapper.Contains("\"tool_call\" or \"toolcall\" =>", StringComparison.Ordinal),
            "The deliverable sources are no longer read as before." + wayOut);

        // edit_file is left out of the tools an agent may deliver with: it refuses a missing file.
        var editFile = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src/tools/Orkeon.Tools.FileSystem/EditFileTool.cs"));
        Assert.True(editFile.Contains("FileNotFoundException", StringComparison.Ordinal),
            "edit_file no longer refuses a file that does not exist: it may now create a deliverable,"
            + " and belongs with the tools CrewDeliverableWriters trusts (OtherCreatingTools).");
    }
}
