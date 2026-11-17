using System.Text.RegularExpressions;
using Orkeon.Application.Crew;
using Orkeon.Studio.Core.Targets;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Core.Tools;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Teams;

/// <summary>
/// Orkeon #392: a task whose deliverable may never be written is found before the run as the engine's
/// load check (<see cref="DeliverableToolCheck"/>, Orkeon 1.2) finds it: no agent that may run the task
/// holds a tool that writes that file, or the task is a decision.
/// </summary>
public sealed class CrewDeliverableWritersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkeon-crew-deliverable-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    /// <summary>A one-agent crew whose task <c>read</c> must write <paramref name="path"/>.</summary>
    private static IReadOnlyList<CrewDeliverableWithoutWriter> OneAgent(string agentTools, string path, string taskTools = "") =>
        CrewDeliverableWriters.OfCrewFile($$"""
            agents:
              analyst:
                role: Analyst
                {{agentTools}}
            tasks:
              read:
                agent: analyst
                {{taskTools}}
                deliverable:
                  path: {{path}}
            """);

    private static void AssertFinding(
        CrewDeliverableWithoutWriter finding, string task, string[] agents, string path,
        CrewDeliverableFindingKind kind = CrewDeliverableFindingKind.NoWriter)
    {
        Assert.Equal(task, finding.Task);
        Assert.Equal(agents, finding.Agents);
        Assert.Equal(path, finding.Path);
        Assert.Equal(kind, finding.Kind);
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

        var finding = Assert.Single(missing);
        AssertFinding(finding, "read", ["analyst"], "/output/notes.md");
        Assert.Equal("file_write", finding.PreferredWriter);
        Assert.False(finding.IsBinary);
    }

    [Theory]
    [InlineData("tools: [file_write]", "/output/notes.md", "")]
    [InlineData("tools: [FILE_WRITE]", "/output/notes.md", "")]
    [InlineData("tools: [file_read]", "/output/notes.md", "tools: [file_write]")]
    [InlineData("tools: [file_write]", "/output/data.csv", "")]
    [InlineData("tools: [file_write]", "/output/page.html", "")]
    // The tool of the file's format.
    [InlineData("tools: [docx_writer]", "/output/report.docx", "")]
    [InlineData("tools: [file_read]", "/output/report.docx", "tools: [docx_writer]")]
    [InlineData("tools: [xlsx_writer]", "/output/figures.xlsx", "")]
    [InlineData("tools: [image_generation]", "/output/cover.jpg", "")]
    [InlineData("tools: [firefox]", "/output/shot.png", "")]
    // A tool Studio's catalogue does not know (an MCP server's) may write anything, a binary file too.
    [InlineData("tools: [file_read, acme_write_file]", "/output/notes.md", "")]
    [InlineData("tools: [acme_pdf]", "/output/report.pdf", "")]
    public void A_tool_that_writes_the_file_is_enough(string agentTools, string path, string taskTools)
    {
        Assert.Empty(OneAgent(agentTools, path, taskTools));
    }

    [Theory]
    // The editors change a file that exists; shell_command's contract names no path.
    [InlineData("tools: [file_read, edit_file]", "/output/notes.md")]
    [InlineData("tools: [edit_batch, grep]", "/output/notes.md")]
    [InlineData("tools: [structural_rewrite]", "/output/notes.md")]
    [InlineData("tools: [shell_command]", "/output/notes.md")]
    [InlineData("tools: [drive_download, email_save_attachment]", "/output/notes.md")]
    [InlineData("tools: []", "/output/notes.md")]
    // file_write writes text: never a binary file.
    [InlineData("tools: [file_write]", "/output/report.docx")]
    // A format writer writes its own format only.
    [InlineData("tools: [docx_writer]", "/output/notes.md")]
    [InlineData("tools: [xlsx_writer]", "/output/report.docx")]
    [InlineData("tools: [firefox]", "/output/shot.jpg")]
    public void A_tool_that_cannot_write_that_file_is_not_enough(string agentTools, string path)
    {
        AssertFinding(Assert.Single(OneAgent(agentTools, path)), "read", ["analyst"], path);
    }

    [Theory]
    [InlineData("/output/notes.md", "file_write", false)]
    [InlineData("/output/report.docx", "docx_writer", true)]
    [InlineData("/output/figures.xlsx", "xlsx_writer", true)]
    [InlineData("/output/cover.jpg", "image_generation", true)]
    // No built-in tool writes a PDF: the message names no tool.
    [InlineData("/output/report.pdf", null, true)]
    public void A_finding_names_the_tool_of_the_files_format_and_whether_it_is_binary(string path, string? writer, bool binary)
    {
        var finding = Assert.Single(OneAgent("tools: [file_read, shell_command]", path));

        Assert.Equal(writer, finding.PreferredWriter);
        Assert.Equal(binary, finding.IsBinary);
    }

    [Theory]
    [InlineData("final_message")]
    [InlineData("Final_Message")]
    [InlineData("structured_output")]
    [InlineData("none")]
    // Refused at load.
    [InlineData("nonsense")]
    public void A_deliverable_the_framework_persists_none_or_one_refused_at_load_needs_no_tool(string source)
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
        AssertFinding(Assert.Single(missing), "read", ["analyst"], "/output/notes.md");
    }

    [Theory]
    // No agent: line: the round-robin may pick any agent.
    [InlineData("process: sequential", "")]
    [InlineData("process: parallel", "")]
    // A manager, a vote or a planner hands the task out whatever its agent: line.
    [InlineData("process: hierarchical", "agent: analyst")]
    [InlineData("process: consensual", "agent: analyst")]
    [InlineData("process: autonomous", "agent: analyst")]
    public void A_task_several_agents_may_run_is_said_only_when_none_of_them_can_write(string process, string task)
    {
        const string Crew = """
            {{process}}
            agents:
              analyst:
                role: Analyst
              writer:
                role: Writer
                tools: [file_write]
            tasks:
              read:
                {{task}}
                deliverable:
                  path: /output/notes.md
            """;
        Assert.Empty(CrewDeliverableWriters.OfCrewFile(Crew.Replace("{{process}}", process).Replace("{{task}}", task)));

        var none = CrewDeliverableWriters.OfCrewFile(Crew
            .Replace("{{process}}", process).Replace("{{task}}", task).Replace("[file_write]", "[shell_command]"));
        AssertFinding(Assert.Single(none), "read", ["analyst", "writer"], "/output/notes.md");
    }

    [Theory]
    [InlineData("process: hierarchical\nmanagerAgent: boss")]
    [InlineData("process: hierarchical\nmanager_agent: boss")]
    [InlineData("process: consensual\nmanagerAgent: boss")]
    public void The_manager_of_a_hierarchical_or_consensual_crew_runs_no_task(string process)
    {
        // The manager holds file_write, the workers do not: the task is said, and the manager is not named.
        var missing = CrewDeliverableWriters.OfCrewFile($$"""
            {{process}}
            agents:
              boss:
                role: Boss
                tools: [file_write]
              analyst:
                role: Analyst
              writer:
                role: Writer
            tasks:
              read:
                agent: boss
                deliverable:
                  path: /output/notes.md
            """);

        AssertFinding(Assert.Single(missing), "read", ["analyst", "writer"], "/output/notes.md");
    }

    [Fact]
    public void Every_agent_of_an_autonomous_crew_may_run_a_task_its_manager_key_included()
    {
        Assert.Empty(CrewDeliverableWriters.OfCrewFile("""
            process: autonomous
            managerAgent: boss
            agents:
              boss:
                role: Boss
                tools: [file_write]
              analyst:
                role: Analyst
            tasks:
              read:
                agent: analyst
                deliverable:
                  path: /output/notes.md
            """));
    }

    [Theory]
    [InlineData("process: parallel")]
    [InlineData("process: graph")]
    [InlineData("process: Sequential")]
    public void The_agent_a_task_names_runs_it_under_a_sequential_parallel_or_graph_process(string process)
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

        AssertFinding(Assert.Single(missing), "read", ["analyst"], "/output/notes.md");
    }

    [Theory]
    // Refused at load.
    [InlineData("process: nonsense", "agent: analyst")]
    // An agent the crew does not declare is refused at load.
    [InlineData("process: sequential", "agent: ghost")]
    public void Nothing_is_said_of_a_crew_the_engine_refuses_at_load(string process, string task)
    {
        Assert.Empty(CrewDeliverableWriters.OfCrewFile($$"""
            {{process}}
            agents:
              analyst:
                role: Analyst
            tasks:
              read:
                {{task}}
                deliverable:
                  path: /output/notes.md
            """));
    }

    [Theory]
    [InlineData("")]
    [InlineData("source: final_message")]
    [InlineData("source: none")]
    public void A_decision_task_that_declares_a_deliverable_is_a_finding_of_its_own(string source)
    {
        // A decision runs no agent and writes no file, whatever the source and the agents' tools.
        var missing = CrewDeliverableWriters.OfCrewFile($$"""
            agents:
              writer:
                role: Writer
                tools: [file_write]
            tasks:
              pick:
                agent: writer
                decision: { questions: {} }
                deliverable:
                  path: /output/choice.md
                  {{source}}
            """);

        var finding = Assert.Single(missing);
        AssertFinding(finding, "pick", [], "/output/choice.md", CrewDeliverableFindingKind.DecisionTask);
        Assert.Null(finding.PreferredWriter);
    }

    [Theory]
    [InlineData("/output/notes.md")]
    [InlineData("/output/data.csv")]
    [InlineData("/output/page.html")]
    [InlineData("/output/figure.svg")]
    [InlineData("/output/report.docx")]
    [InlineData("/output/REPORT.DOCX")]
    [InlineData("/output/figures.xlsx")]
    [InlineData("/output/report.pdf")]
    [InlineData("/output/shot.png")]
    [InlineData("/output/cover.jpg")]
    [InlineData("/output/cover.webp")]
    [InlineData("/output/anim.gif")]
    [InlineData("/output/.docx")]
    [InlineData("/output/README")]
    public void Studios_verdict_on_a_built_in_belt_is_the_engines(string path)
    {
        string[][] belts =
        [
            [], ["file_write"], ["docx_writer"], ["xlsx_writer"], ["image_generation"], ["firefox"],
            ["shell_command", "edit_file", "edit_batch", "structural_rewrite", "drive_download", "email_save_attachment"],
            ["file_read", "file_write", "docx_writer"],
        ];
        foreach (var belt in belts)
            Assert.Equal(DeliverableToolCheck.CanWrite(belt, path), CrewDeliverableWriters.CanWrite(belt, path));

        // A tool Studio does not know may write anything.
        Assert.True(CrewDeliverableWriters.CanWrite(["file_read", "acme_write_file"], path));
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
        AssertFinding(Assert.Single(CrewDeliverableWriters.Of(target)), "read", ["analyst"], "/output/notes.md");

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

        // A .ork.ts crew is the CLI's to judge: an Orkeon 1.2 CLI warns of it at load.
        var script = Path.Combine(_root, "crew.ork.ts");
        File.WriteAllText(script, "export default {};\n");
        Assert.Empty(CrewDeliverableWriters.Of(new RunTarget
        {
            Kind = RunTargetKind.ScriptFile, SelectedPath = script, RunPath = script,
        }));
    }

    private const string WayOut =
        " CrewDeliverableWriters warns before the launch because the engine loads such a crew and only warns of it"
        + " (Orkeon #392). If the engine now refuses or repairs that case, block or drop the warning (launch tab, Run"
        + " launcher, catalogues, docs), then update this guard.";

    [Fact]
    public void The_engine_still_warns_rather_than_refuses_at_load()
    {
        var factory = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src/core/Orkeon.Infrastructure/Configuration/CrewFactory.cs"));
        Assert.True(
            Regex.IsMatch(
                factory,
                @"if \(!_warnOnDeliverableTools\)\s*return;\s*var findings = Orkeon\.Application\.Crew\.DeliverableToolCheck\.Check\("
                + @"[^;]*;\s*foreach \(var finding in findings\)\s*LogDeliverableWithoutWriter\(",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)),
            "The crew factory no longer logs DeliverableToolCheck's findings as warnings under WarnOnDeliverableTools." + WayOut);

        var validate = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src/hosting/Orkeon.Hosting/RunnerExecution.Diagnostics.cs"));
        Assert.True(validate.Contains("$\"WARNING: {finding.Message}\"", StringComparison.Ordinal),
            "orkeon run --validate no longer prints the findings as WARNING: lines." + WayOut);
    }

    [Fact]
    public void The_engine_still_reads_no_source_as_a_tool_call()
    {
        var mapper = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src/core/Orkeon.Infrastructure/Configuration/Yaml/YamlCrewMapper.cs"));
        Assert.True(
            Regex.IsMatch(
                mapper,
                @"if \(string\.IsNullOrWhiteSpace\(raw\)\)\s*return [\w.]*DeliverableSource\.ToolCall;",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)),
            "A deliverable without a source no longer reads as tool_call: CrewDeliverableWriters.DeliverableOf reads it so.");
        Assert.True(mapper.Contains("\"tool_call\" or \"toolcall\" =>", StringComparison.Ordinal),
            "The deliverable sources are no longer spelled as CrewDeliverableWriters.DeliverableOf reads them.");
    }

    [Fact]
    public void The_engines_writers_and_finding_kinds_are_the_ones_Studio_knows()
    {
        Assert.Equal("file_write", DeliverableToolCheck.FileWriteToolName);
        Assert.Equal(
            ["docx_writer", "xlsx_writer", "image_generation", "firefox", "file_write"],
            DeliverableToolCheck.FileWriterToolNames);

        // A writer missing from Studio's catalogue would count as an opaque tool, which writes anything.
        var known = ToolCatalog.All.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(DeliverableToolCheck.FileWriterToolNames, writer => Assert.Contains(writer, known));

        // A new kind of finding is one the launchers do not word yet.
        Assert.Equal(Enum.GetNames<DeliverableFindingKind>(), Enum.GetNames<CrewDeliverableFindingKind>());
    }
}
