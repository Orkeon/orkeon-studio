using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkeon.Application.Interfaces;
using Orkeon.Domain.FileSystem;
using Orkeon.Hosting;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Tests.Launch;

/// <summary>
/// The engine as the oracle of what Studio says before a launch. The runner host, the one
/// <c>orkeon run</c> builds, is built over each settings file below: it starts or refuses, and loads a
/// crew or refuses it. Studio, which opens no host, must say the same from the file alone: the status
/// of the decision settings and the problems of the sections it restates for the start, and
/// <see cref="DecisionPreflight"/> for the load. A rule the engine changes fails here.
/// </summary>
/// <remarks>
/// The host reads the machine's environment, so Studio's side reads it too: both see the same keys.
/// What only a run decides (a crew that states <c>required</c>, refused before its first task; a
/// decision task with no answer) is not a load, and is left to the other suites.
/// </remarks>
public sealed class EngineStartAndLoadOracleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkeon-start-oracle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private const string Provider = "\"TypeSafe\": { \"ApiKey\": \"studio-oracle-tests\" }";

    private static readonly Func<string, string?> MachineEnvironment = Environment.GetEnvironmentVariable;

    /// <summary>The settings file: the engine's mock model, and <paramref name="orkeon"/> as the <c>Orkeon</c> section.</summary>
    private static string Settings(string orkeon, string llmProfiles = "") =>
        $$"""
        {
          "Llm": { "Provider": "mock", "Model": "mock", "Responses": "/crew/responses.jsonl"{{llmProfiles}} },
          "Orkeon": { {{orkeon}} }
        }
        """;

    /// <summary>The runner host over <paramref name="settings"/>; the refusal of its start when it has one.</summary>
    private (IHost? Host, string? Refusal) Start(string settings)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "responses.jsonl"), "");
        var path = Path.Combine(_root, $"appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, settings);
        try
        {
            return (RunnerHost.Build(
                path,
                new RunnerMountPlan { CliMounts = [$"{FileSystemMount.Quote(_root)}:/crew:ro"], AllowExternalMounts = true },
                configureLogging: (_, logging) => logging.SetMinimumLevel(LogLevel.None)), null);
        }
        catch (RunnerSettingsException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>What Studio says the start refuses in <paramref name="settings"/>, the first refusal first.</summary>
    private static List<string> StudioRefusals(string settings)
    {
        var document = AppSettingsDocument.Parse(settings);
        var refusals = new List<string>();
        if (document.Decision.Resolve(MachineEnvironment).Refusal is { } decision)
            refusals.Add(decision);
        refusals.AddRange(document.Compaction.Problems()
            .Concat(document.Skills.Problems())
            .Concat(document.ToolBridge.Problems())
            .Concat(document.BridgeServer.Problems())
            .Select(problem => problem.Problem));
        return refusals;
    }

    public static TheoryData<string, string> StartSettings => new()
    {
        { "nothing set", "" },
        { "a provider", Provider },
        { "a fail-closed judge, no provider", "\"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        { "a fail-closed judge, the switch off", Provider + ", \"Decision\": { \"Enabled\": false }, \"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        { "a fail-closed judge, the circuit on", Provider + ", \"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        { "a fail-open judge, no provider", "\"Guardian\": { \"Llm\": { \"Enabled\": true, \"FailMode\": \"Open\" } }" },
        { "the guardian off", "\"Guardian\": { \"Enabled\": false, \"Llm\": { \"Enabled\": true } }" },
        { "a ladder of one rung", "\"Decision\": { \"Route\": { \"Ladder\": [\"default\"] } }" },
        { "auto as a rung", "\"Decision\": { \"Route\": { \"Ladder\": [\"default\", \"auto\"] } }" },
        { "levels out of order", "\"Decision\": { \"Effort\": { \"Levels\": [\"high\", \"low\"] } }" },
        { "an unknown level", "\"Decision\": { \"Effort\": { \"Levels\": [\"low\", \"extreme\"] } }" },
        { "levels in order", "\"Decision\": { \"Effort\": { \"Levels\": [\"low\", \"xhigh\"] } }" },
        { "a compaction share out of bounds", "\"Compaction\": { \"TriggerShare\": 2 }" },
        { "auto as the summary's profile", "\"Compaction\": { \"Profile\": \"auto\" }" },
        { "a pruning head of zero", "\"Compaction\": { \"Prune\": { \"HeadChars\": 0 } }" },
        { "a misspelt compaction key", "\"Compaction\": { \"TrigerShare\": 0.5 }" },
        { "a skills library that is no virtual path", "\"Skills\": { \"Library\": \"skills\" }" },
        { "a misspelt skills key", "\"Skills\": { \"Libary\": \"/x\" }" },
        { "the context sections as the cards write them", "\"Compaction\": { \"Enabled\": false, \"TriggerShare\": 0.6, \"KeepRecentTurns\": 4, \"Prune\": { \"Enabled\": true, \"FillShare\": 0.5 } }, \"Skills\": { \"MaxSkillChars\": 2000 }" },
        { "a serving bridge with no session", "\"Bridge\": { \"MaxSessions\": 0 }" },
        { "a misspelt bridge key", "\"Bridge\": { \"Port\": 7410 }" },
        { "a caller's address over http", "\"Tools\": { \"Bridge\": { \"Address\": \"http://desk.example:7410/\" } }" },
        { "a serving bridge as the card writes it", "\"Bridge\": { \"Allow\": [\"*\"], \"Deny\": [\"shell_command\"] }" },
    };

    /// <summary>
    /// The host starts exactly when Studio finds nothing to refuse, and its refusal is the sentence
    /// Studio shows (the engine may say more after it: where the keys are listed, a closest name).
    /// </summary>
    [Theory]
    [MemberData(nameof(StartSettings))]
    public void The_host_refuses_to_start_exactly_what_Studio_says_it_refuses(string what, string orkeon)
    {
        var settings = Settings(orkeon);

        var (host, refusal) = Start(settings);
        host?.Dispose();
        var studio = StudioRefusals(settings);

        Assert.True((refusal is null) == (studio.Count == 0), $"{what}: the engine says «{refusal}», Studio says «{string.Join(" | ", studio)}».");
        if (refusal is not null)
            Assert.Contains(studio[0], refusal, StringComparison.Ordinal);
    }

    private const string Agents = """
        agents:
          analyst: { role: Analyst, goal: read }
        """;

    private const string OneTask = """
        tasks:
          read: { description: read, expected_output: notes, agent: analyst }
        """;

    private static string Crew(string agents, string tasks, string mode = "") =>
        $"name: oracle\ngoal: compare the verdicts\nprocess: sequential\n{mode}{agents}\n{tasks}\n";

    private const string DecisionProfile =
        "\"Decision\": { \"Profiles\": { \"guarded\": { \"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" } } ] } } }";

    private const string Strong = ", \"Profiles\": { \"strong\": { \"Provider\": \"mock\", \"Model\": \"mock\", \"Responses\": \"/crew/responses.jsonl\" } }";

    public static TheoryData<string, string, string, string> Loads => new()
    {
        {
            "a holder of the tool, no provider, strict tools", "", "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, tools: [system_one_decide] }", OneTask)
        },
        {
            "a holder of the tool, a provider", Provider, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, tools: [system_one_decide] }", OneTask)
        },
        {
            "a holder of the tool, no provider, lenient tools", "\"CrewFactory\": { \"StrictTools\": false }", "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, tools: [system_one_decide] }", OneTask)
        },
        {
            "a holder of the tool in a crew that states required, no provider", "", "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, tools: [system_one_decide] }", OneTask, "decision: { mode: required }\n")
        },
        {
            "an agent on profile auto, no ladder", Provider, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: auto } }", OneTask)
        },
        {
            "an agent on profile auto, a ladder of two profiles", Provider + ", \"Decision\": { \"Route\": { \"Ladder\": [\"default\", \"strong\"] } }", Strong,
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: auto } }", OneTask)
        },
        {
            "a relay on profile auto with no task of its own, no ladder", Provider, "",
            Crew(
                "agents:\n  analyst: { role: Analyst, goal: read }\n  judge: { role: Judge, goal: judge, llm: { profile: auto } }",
                OneTask + "\n  pick:\n    description: is it ready\n    expected_output: an answer\n    dependencies: [read]\n    decision:\n      questions:\n        go: { noul: the work is ready }\n      without: { agent: judge }",
                "decision: { mode: optional }\n")
        },
        {
            "a decision profile named by a crew that states none", Provider + ", " + DecisionProfile, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: guarded } }", OneTask, "decision: { mode: none }\n")
        },
        {
            "a decision profile named by a crew that states no mode", Provider + ", " + DecisionProfile, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: guarded } }", OneTask)
        },
        {
            "a crew that asks nothing, no provider", "", "",
            Crew(Agents, OneTask)
        },
    };

    /// <summary>
    /// The engine loads the crew exactly when Studio does not block the launch for the load: a tool
    /// withheld under strict tools, a profile to pick with no ladder, a decision profile under the mode
    /// none. A crew that states <c>required</c> loads, the tool withheld with a warning: Studio blocks
    /// it for the run the engine then refuses, which is no load.
    /// </summary>
    [Theory]
    [MemberData(nameof(Loads))]
    public async Task The_engine_loads_a_crew_exactly_when_Studio_does_not_block_it_for_its_load(
        string what, string orkeon, string llmProfiles, string yaml)
    {
        var settings = Settings(orkeon, llmProfiles);
        var (host, refusal) = Start(settings);
        Assert.True(host is not null, $"{what}: the host refused to start: {refusal}");
        string? loadRefusal = null;
        using (host)
        {
            var scope = host.Services.CreateAsyncScope();
            await using var _ = scope.ConfigureAwait(false);
            var loader = scope.ServiceProvider.GetRequiredService<ICrewDefinitionLoader>();
            var factory = scope.ServiceProvider.GetRequiredService<ICrewFactory>();
            try
            {
                var definition = await loader.LoadFromStringAsync(yaml, TestContext.Current.CancellationToken);
                await factory.CreateFromConfigAsync(definition, TestContext.Current.CancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Orkeon.Domain.Exceptions.DomainException)
            {
                loadRefusal = ex.Message;
            }
        }

        var status = AppSettingsDocument.Parse(settings).Decision.Resolve(MachineEnvironment);
        var finding = DecisionPreflight.Check(CrewDecisionUsage.OfCrewFile(yaml), status, settingsKnown: true, noDecision: false);
        var blocksForTheLoad = finding is { BlocksLaunch: true, RefusedByMode: false };

        Assert.True(
            (loadRefusal is not null) == blocksForTheLoad,
            $"{what}: the engine says «{loadRefusal ?? "loads"}», Studio says «{finding?.Kind.ToString() ?? "nothing"}» (blocks: {finding?.BlocksLaunch}).");
    }
}
