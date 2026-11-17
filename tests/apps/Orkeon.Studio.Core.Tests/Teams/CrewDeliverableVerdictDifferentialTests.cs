using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkeon.Application.Crew;
using Orkeon.Application.Interfaces;
using Orkeon.Domain.FileSystem;
using Orkeon.Domain.Tools;
using Orkeon.Hosting;
using Orkeon.Infrastructure.MCP;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Core.Tests.Doubles;

namespace Orkeon.Studio.Core.Tests.Teams;

/// <summary>
/// Orkeon #392, differential: each crew below is loaded by the engine as <c>orkeon run --validate</c>
/// loads it — the runner host's services, its real tool registry, the YAML loader and the crew factory
/// — and judged by the engine's <see cref="DeliverableToolCheck"/> over the agents and tasks the
/// factory built; <see cref="CrewDeliverableWriters"/> must find the same tasks, of the same kind, with
/// the same agents (the engine names them by role, Studio by key) and the same paths.
/// </summary>
/// <remarks>
/// One host for the class (<see cref="EngineHost"/>): it registers the tools a run registers. The tools
/// a run registers only under a setting the host does not hold (<c>system_one_decide</c>,
/// <c>mcp_prompt_get</c>, <c>memory_recall</c>) are registered by name (<see cref="StubBaseTool"/>, of
/// an <c>Orkeon.*</c> assembly as the real ones), and an MCP server's tool is a real
/// <see cref="McpToolAdapter"/> over no server: the engine counts it as opaque.
/// </remarks>
public sealed class CrewDeliverableVerdictDifferentialTests(CrewDeliverableVerdictDifferentialTests.EngineHost engine)
    : IClassFixture<CrewDeliverableVerdictDifferentialTests.EngineHost>
{
    /// <summary>The MCP server's tool of the fixtures: a name Studio's catalogue does not carry.</summary>
    private const string McpTool = "acme_render";

    /// <summary>The runner host over an empty mount, with the tools the fixtures name and no run registers here.</summary>
    public sealed class EngineHost : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "orkeon-studio-deliverable-diff-" + Guid.NewGuid().ToString("N"));

        private readonly FakeMcpTransport _transport = new();
#pragma warning disable ORKEXP004 // The MCP client is experimental in Orkeon; it only backs the adapter below.
        private McpClient? _client;
#pragma warning restore ORKEXP004

        public IHost Host { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            var settingsPath = Path.Combine(_root, "appsettings.json");
            // The engine's mock model, over no response: no key, no network, and nothing here calls it.
            // RaggableTree stays on, as a run's default: it registers edit_batch and structural_rewrite.
            await File.WriteAllTextAsync(Path.Combine(_root, "responses.jsonl"), "", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(settingsPath, """
                {
                  "Llm": { "Provider": "mock", "Model": "mock", "Responses": "/crew/responses.jsonl" }
                }
                """, TestContext.Current.CancellationToken);

            Host = RunnerHost.Build(
                settingsPath,
                new RunnerMountPlan
                {
                    CliMounts = [$"{FileSystemMount.Quote(_root)}:/crew:ro"],
                    AllowExternalMounts = true,
                },
                configureLogging: (_, logging) => logging.SetMinimumLevel(LogLevel.None));

            var registry = Host.Services.GetRequiredService<IToolRegistry>();
            foreach (var name in new[] { "system_one_decide", "mcp_prompt_get", "memory_recall" })
            {
                if (await registry.GetToolByNameAsync(name, TestContext.Current.CancellationToken) is null)
                    await registry.RegisterToolAsync(new StubBaseTool(name), TestContext.Current.CancellationToken);
            }

#pragma warning disable ORKEXP004 // The MCP adapter is experimental in Orkeon; it stands for a server's tool.
            _client = new McpClient(_transport);
            var adapter = new McpToolAdapter(
                new McpToolDefinition { Name = McpTool, Description = "Renders a page.", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement },
                _client);
#pragma warning restore ORKEXP004
            await registry.RegisterToolAsync(adapter, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Host?.Dispose();
            if (_client is not null)
                await _client.DisposeAsync();
            await _transport.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// The crews, by what they pin. Every agent's role is its key capitalized, so the engine's roles
    /// map back to Studio's keys.
    /// </summary>
    public static TheoryData<string, string> Crews => new()
    {
        {
            "sequential: a reader, a writer, the task-level tools",
            """
            process: sequential
            agents:
              analyst: { role: Analyst, goal: read, tools: [directory_read, file_read, grep] }
              writer: { role: Writer, goal: write, tools: [file_read, file_write] }
            tasks:
              read: { description: read, expected_output: notes, agent: analyst, deliverable: { path: /output/notes.md } }
              report: { description: write, expected_output: report, agent: writer, deliverable: { path: /output/report.md, source: tool_call } }
              granted: { description: write, expected_output: notes, agent: analyst, tools: [file_write], deliverable: { path: /output/granted.md } }
              wrong: { description: write, expected_output: doc, agent: analyst, tools: [docx_writer], deliverable: { path: /output/wrong.md } }
              said: { description: answer, expected_output: answer, agent: analyst, deliverable: { path: /output/said.md, source: final_message } }
            """
        },
        {
            "binary against text: the tool of the format, never file_write",
            """
            process: sequential
            agents:
              writer: { role: Writer, goal: write, tools: [file_write] }
              office: { role: Office, goal: write, tools: [docx_writer, xlsx_writer] }
              painter: { role: Painter, goal: draw, tools: [image_generation, firefox] }
            tasks:
              docx: { description: x, expected_output: y, agent: writer, deliverable: { path: /output/report.docx } }
              docxok: { description: x, expected_output: y, agent: office, deliverable: { path: /output/report2.docx } }
              xlsx: { description: x, expected_output: y, agent: office, deliverable: { path: /output/figures.xlsx } }
              jpg: { description: x, expected_output: y, agent: painter, deliverable: { path: /output/cover.jpg } }
              png: { description: x, expected_output: y, agent: writer, deliverable: { path: /output/shot.png } }
              pdf: { description: x, expected_output: y, agent: office, deliverable: { path: /output/report.pdf } }
              csv: { description: x, expected_output: y, agent: writer, deliverable: { path: /output/data.csv } }
              svg: { description: x, expected_output: y, agent: painter, deliverable: { path: /output/figure.svg } }
            """
        },
        {
            "sequential round-robin: a task naming no agent",
            """
            agents:
              analyst: { role: Analyst, goal: read, tools: [file_read] }
              writer: { role: Writer, goal: write, tools: [file_write] }
            tasks:
              notes: { description: x, expected_output: y, deliverable: { path: /output/notes.md } }
              doc: { description: x, expected_output: y, deliverable: { path: /output/report.docx } }
            """
        },
        {
            "parallel: the agent a task names",
            """
            process: parallel
            agents:
              analyst: { role: Analyst, goal: read }
              writer: { role: Writer, goal: write, tools: [file_write] }
            tasks:
              read: { description: x, expected_output: y, agent: analyst, deliverable: { path: /output/notes.md } }
              free: { description: x, expected_output: y, deliverable: { path: /output/free.md } }
            """
        },
        {
            "graph: the agent a task names",
            """
            process: graph
            agents:
              analyst: { role: Analyst, goal: read }
              writer: { role: Writer, goal: write, tools: [file_write] }
            tasks:
              read: { description: x, expected_output: y, agent: analyst, deliverable: { path: /output/notes.md } }
              write: { description: x, expected_output: y, agent: writer, deliverable: { path: /output/report.md } }
            """
        },
        {
            "hierarchical: the manager runs no task",
            """
            process: hierarchical
            managerAgent: boss
            agents:
              boss: { role: Boss, goal: lead, tools: [file_write] }
              analyst: { role: Analyst, goal: read }
              writer: { role: Writer, goal: write, tools: [shell_command] }
            tasks:
              read: { description: x, expected_output: y, agent: boss, deliverable: { path: /output/notes.md } }
              told: { description: x, expected_output: y, agent: boss, deliverable: { path: /output/told.md, source: final_message } }
            """
        },
        {
            "consensual: the manager runs no task, any voter may write",
            """
            process: consensual
            managerAgent: boss
            agents:
              boss: { role: Boss, goal: lead, tools: [docx_writer] }
              analyst: { role: Analyst, goal: read, tools: [edit_file] }
              writer: { role: Writer, goal: write, tools: [file_write] }
            tasks:
              read: { description: x, expected_output: y, agent: analyst, deliverable: { path: /output/notes.md } }
              doc: { description: x, expected_output: y, agent: analyst, deliverable: { path: /output/report.docx } }
            """
        },
        {
            "autonomous: every agent",
            """
            process: autonomous
            agents:
              boss: { role: Boss, goal: lead, tools: [file_write] }
              analyst: { role: Analyst, goal: read }
            tasks:
              read: { description: x, expected_output: y, agent: analyst, deliverable: { path: /output/notes.md } }
              doc: { description: x, expected_output: y, agent: analyst, deliverable: { path: /output/report.docx } }
            """
        },
        {
            "decision tasks: a deliverable nothing writes, whatever its source",
            """
            process: sequential
            agents:
              writer: { role: Writer, goal: write, tools: [file_write] }
            tasks:
              draft: { description: x, expected_output: y, agent: writer, deliverable: { path: /output/draft.md } }
              pick: { description: x, expected_output: y, decision: { questions: { go: { noul: the work is ready } } }, deliverable: { path: /output/choice.md } }
              told: { description: x, expected_output: y, decision: { questions: { go: { noul: the work is ready } } }, deliverable: { path: /output/told.md, source: final_message } }
            """
        },
        {
            "Orkeon's own tools: the RAG tools, the conditional ones, the editors, the downloaders",
            """
            agents:
              searcher: { role: Searcher, goal: search, tools: [file_read, rag_search, rag_ingest, rag_eval] }
              decider: { role: Decider, goal: decide, tools: [system_one_decide, mcp_prompt_get, memory_recall] }
              editor: { role: Editor, goal: edit, tools: [edit_file, edit_batch, structural_rewrite, shell_command] }
              fetcher: { role: Fetcher, goal: fetch, tools: [drive_download, email_save_attachment] }
              scribe: { role: Scribe, goal: write, tools: [rag_search, file_write] }
            tasks:
              search: { description: x, expected_output: y, agent: searcher, deliverable: { path: /output/search.md } }
              decide: { description: x, expected_output: y, agent: decider, deliverable: { path: /output/decide.md } }
              edit: { description: x, expected_output: y, agent: editor, deliverable: { path: /output/edit.md } }
              fetch: { description: x, expected_output: y, agent: fetcher, deliverable: { path: /output/fetch.md } }
              write: { description: x, expected_output: y, agent: scribe, deliverable: { path: /output/write.md } }
            """
        },
        {
            "an MCP server's tool may write anything, a binary file too",
            $$"""
            agents:
              renderer: { role: Renderer, goal: render, tools: [file_read, {{McpTool}}] }
              reader: { role: Reader, goal: read, tools: [file_read] }
            tasks:
              pdf: { description: x, expected_output: y, agent: renderer, deliverable: { path: /output/report.pdf } }
              granted: { description: x, expected_output: y, agent: reader, tools: [{{McpTool}}], deliverable: { path: /output/notes.md } }
              bare: { description: x, expected_output: y, agent: reader, deliverable: { path: /output/bare.docx } }
            """
        },
    };

    [Theory]
    [MemberData(nameof(Crews))]
    public async Task Studio_finds_what_the_engine_finds_when_it_loads_the_crew(string what, string yaml)
    {
        var ct = TestContext.Current.CancellationToken;
        var (engineFindings, _) = await EngineFindingsAsync(yaml, ct);
        var studioFindings = CrewDeliverableWriters.OfCrewFile(Named(yaml));

        Assert.True(engineFindings.Count > 0, $"{what}: the engine finds nothing, so this crew compares nothing.");
        Assert.Equal(
            engineFindings.Select(Describe).Order(StringComparer.Ordinal),
            studioFindings.Select(Describe).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Each crew says something and leaves something unsaid: a crew of which both said nothing, or
    /// everything, would pin less than it seems to.
    /// </summary>
    [Theory]
    [MemberData(nameof(Crews))]
    public async Task Every_crew_holds_a_finding_and_a_task_that_is_not_one(string what, string yaml)
    {
        var (findings, tasks) = await EngineFindingsAsync(yaml, TestContext.Current.CancellationToken);

        Assert.True(findings.Count > 0, $"{what}: the engine finds nothing.");
        Assert.True(findings.Count < tasks, $"{what}: the engine finds every task.");
    }

    /// <summary>
    /// Crews the engine refuses at load over their manager agent, each with a task no agent can write
    /// the file of: Studio says nothing, so the load error is the one thing the user reads.
    /// </summary>
    public static TheoryData<string, string> CrewsTheEngineRefuses => new()
    {
        { "managerAgent: boss", "has no manager" },
        { "process: parallel\nmanagerAgent: boss", "has no manager" },
        { "process: autonomous\nmanagerAgent: boss", "has no manager" },
        { "process: hierarchical\nmanagerAgent: ghost", "names no agent of the crew" },
        { "process: consensual\nmanager_agent: ghost", "names no agent of the crew" },
        { "process: hierarchical", "requires a manager agent" },
    };

    [Theory]
    [MemberData(nameof(CrewsTheEngineRefuses))]
    public async Task Studio_says_nothing_of_a_crew_the_engine_refuses_over_its_manager(string head, string reason)
    {
        var yaml = head + """

            agents:
              boss: { role: Boss, goal: lead }
              analyst: { role: Analyst, goal: read }
            tasks:
              read: { description: x, expected_output: y, agent: analyst, deliverable: { path: /output/notes.md } }
            """;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EngineFindingsAsync(yaml, TestContext.Current.CancellationToken));
        Assert.Contains(reason, refusal.Message, StringComparison.Ordinal);
        Assert.Empty(CrewDeliverableWriters.OfCrewFile(Named(yaml)));

        // The same crew under a process that takes its manager loads, and the task is said.
        const string Accepted = "process: hierarchical\nmanagerAgent: boss";
        var accepted = Accepted + yaml[head.Length..];
        Assert.NotEmpty((await EngineFindingsAsync(accepted, TestContext.Current.CancellationToken)).Findings);
        Assert.NotEmpty(CrewDeliverableWriters.OfCrewFile(Named(accepted)));
    }

    /// <summary>The engine's findings, as <c>orkeon run --validate</c> computes them after the load.</summary>
    private async Task<(IReadOnlyList<DeliverableToolFinding> Findings, int Tasks)> EngineFindingsAsync(string yaml, CancellationToken ct)
    {
        var scope = engine.Host.Services.CreateAsyncScope();
        await using var _ = scope.ConfigureAwait(false);
        var loader = scope.ServiceProvider.GetRequiredService<ICrewDefinitionLoader>();
        var factory = scope.ServiceProvider.GetRequiredService<ICrewFactory>();

        var crew = await factory.CreateFromConfigAsync(await loader.LoadFromStringAsync(Named(yaml), ct), ct);

        var agents = await scope.ServiceProvider.GetRequiredService<Orkeon.Domain.Agent.IAgentRepository>()
            .GetByIdsAsync(crew.Agents, ct);
        var tasks = await scope.ServiceProvider.GetRequiredService<Orkeon.Domain.Task.ITaskRepository>()
            .GetByIdsAsync(crew.Tasks, ct);
        return (DeliverableToolCheck.Check(tasks, agents, crew.ProcessType, crew.ManagerAgentId), tasks.Count);
    }

    /// <summary>The crew with the name and the goal the loader requires; Studio reads neither.</summary>
    private static string Named(string yaml) => "name: differential\ngoal: compare the verdicts\n" + yaml;

    // The engine lists the agents in its repository's order, Studio in the crew file's: the set is the verdict.
    private static string Describe(DeliverableToolFinding finding) =>
        $"{finding.Kind}|{finding.Task}|{string.Join(",", finding.Agents.Order(StringComparer.Ordinal))}|{finding.Path}";

    // The fixtures' roles are their keys capitalized.
    private static string Describe(CrewDeliverableWithoutWriter finding) =>
        $"{finding.Kind}|{finding.Task}|{string.Join(",", finding.Agents.Select(key => char.ToUpperInvariant(key[0]) + key[1..]).Order(StringComparer.Ordinal))}|{finding.Path}";
}
