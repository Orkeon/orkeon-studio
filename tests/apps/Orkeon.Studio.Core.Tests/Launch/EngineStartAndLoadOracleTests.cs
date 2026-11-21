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
/// of the decision settings (the decision profiles and the guardian among them) and the problems of
/// the sections it restates for the start, and
/// <see cref="DecisionPreflight"/> for the load. A rule the engine changes fails here.
/// </summary>
/// <remarks>
/// The host reads the machine's environment: every variable as a setting, the <c>ORKEON_</c> ones above
/// the file, and the providers' keys. A key there would give every "no provider" row a provider, and a
/// setting there would change what a row writes: the suite refuses to run on such a machine, naming
/// the variable, rather than pass on nothing or fail on a row that is right.
/// What only a run decides (a crew that states <c>required</c>, refused before its first task; a
/// decision task with no answer) is not a load, and is left to the other suites.
/// </remarks>
public sealed class EngineStartAndLoadOracleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkeon-start-oracle-" + Guid.NewGuid().ToString("N"));

    public EngineStartAndLoadOracleTests()
    {
        var read = VariablesTheHostWouldRead(Environment.GetEnvironmentVariables().Keys.Cast<string>());
        Assert.True(
            read.Count == 0,
            $"The environment of this machine sets {string.Join(", ", read)}: the engine's host reads them as a decision provider's key "
            + "or as settings, above or under the file of each row. Unset them to run this suite.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// The variables, among <paramref name="names"/>, the host of a row would read: the key of a decision
    /// provider (the variable each provider reads by default), a setting of a section these rows write
    /// (<c>Orkeon__…</c>, <c>Llm__…</c>), and any setting under the prefix (<c>ORKEON_…__…</c>). The
    /// configuration compares its keys whatever their letter case.
    /// </summary>
    private static List<string> VariablesTheHostWouldRead(IEnumerable<string> names)
    {
        var keys = DecisionSection.Providers.Select(provider => provider.DefaultApiKeyVariable).ToList();
        return
        [
            .. names
                .Where(name => keys.Contains(name, StringComparer.Ordinal)
                    || name.StartsWith("Orkeon__", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Llm__", StringComparison.OrdinalIgnoreCase)
                    || (name.StartsWith(DecisionSection.EnvironmentPrefix, StringComparison.OrdinalIgnoreCase)
                        && name.Contains("__", StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal),
        ];
    }

    [Fact]
    public void The_variables_the_host_would_read_are_told_from_the_others()
    {
        Assert.Equal(
            ["KEV_API_KEY", "LIQUID_API_KEY", "ORKEON_Llm__Profiles__x__Model", "ORKEON_Orkeon__Decision__Enabled", "Orkeon__Decision__Provider", "TYPESAFE_API_KEY", "llm__Model", "orkeon__typesafe__model"],
            VariablesTheHostWouldRead(
            [
                "PATH", "HOME", "ORKEON_BRIDGE_TOKEN", "ORKEON_CLI_DIR", "DEEPSEEK_API_KEY", "Orkeon__Decision__Provider",
                "orkeon__typesafe__model", "ORKEON_Orkeon__Decision__Enabled", "ORKEON_Llm__Profiles__x__Model", "llm__Model",
                "TYPESAFE_API_KEY", "LIQUID_API_KEY", "KEV_API_KEY",
            ]));
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

    /// <summary>
    /// What Studio says the start refuses in <paramref name="settings"/>, in the order the host meets
    /// it, so the first is the one the host raises: the file it does not load, and nothing else then;
    /// what is refused while the host is composed (the decision profiles among it); the values and the
    /// rules of each section, in the order the host declares them (the compaction, the skills, the
    /// ladder and the levels, the guardian and the circuit's switch, the caller's bridge, the serving
    /// one, the provider in use);
    /// the keys no section carries; a name two profiles share; and last the fail-closed judge.
    /// </summary>
    private static List<string> StudioRefusals(string settings, Func<string, string?>? environment = null)
    {
        var document = AppSettingsDocument.Parse(settings);
        var status = document.Decision.Resolve(environment ?? MachineEnvironment);
        if (status.RefusalStage == DecisionRefusalStage.File)
            return [status.Refusal!];

        string[] Decision(params DecisionRefusalStage[] stages) =>
            status.Refusal is { } refusal && stages.Contains(status.RefusalStage) ? [refusal] : [];
        static bool OfTheWalk(string problem) =>
            problem.Contains(" is not a setting: ", StringComparison.Ordinal)
            || problem.EndsWith(", which is not a finite number.", StringComparison.Ordinal);
        List<string> context = [.. document.Compaction.Problems().Concat(document.Skills.Problems()).Select(problem => problem.Problem)];
        List<string> bridge = [.. document.ToolBridge.Problems().Concat(document.BridgeServer.Problems()).Select(problem => problem.Problem)];
        // The guardian's values and rules are the status's own refusal; its keys are walked with the others.
        List<string> guardian = [.. document.Guardian.Problems().Select(problem => problem.Problem)];
        List<string> decisionKeys = [.. document.Decision.UnknownKeys().Select(problem => problem.Problem)];
        return
        [
            .. Decision(DecisionRefusalStage.Composition),
            .. context.Where(problem => !OfTheWalk(problem)),
            .. Decision(DecisionRefusalStage.Picks, DecisionRefusalStage.Switches),
            .. bridge.Where(problem => !OfTheWalk(problem)),
            .. Decision(DecisionRefusalStage.Provider),
            .. context.Where(OfTheWalk),
            .. guardian.Where(OfTheWalk),
            .. decisionKeys,
            .. bridge.Where(OfTheWalk),
            .. Decision(DecisionRefusalStage.Profiles, DecisionRefusalStage.Judge),
        ];
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
        // A key that is no setting, on the caller's side and at any depth; the engine's computed keys are settings to it.
        { "a misspelt caller key", "\"Tools\": { \"Bridge\": { \"Adress\": \"https://desk.example:7410/\" } }" },
        { "the caller's computed keys", "\"Tools\": { \"Bridge\": { \"IsEnabled\": true, \"IsDockerHostOverHttp\": false } }" },
        { "a key under a share", "\"Compaction\": { \"TriggerShare\": { \"x\": 1 } }" },
        { "a key under the skills library", "\"Skills\": { \"Library\": { \"a\": 1 } }" },
        { "a list in a list of names", "\"Bridge\": { \"Allow\": [[\"a*\"]] }" },
        { "an object in a list of names", "\"Bridge\": { \"Allow\": [{ \"a\": 1 }] }" },
        { "a key under a denied name", "\"Bridge\": { \"Deny\": { \"x\": { \"y\": 1 } } }" },
        { "a key under a count", "\"Bridge\": { \"MaxSessions\": { \"a\": 1 } }" },
        { "a misspelt pruning key beside a misspelt compaction key", "\"Compaction\": { \"Tx\": 1, \"Prune\": { \"Hx\": 1 } }" },
        { "a share that is no finite number", "\"Compaction\": { \"Prune\": { \"FillShare\": \"NaN\" } }" },
        // A value the binder cannot convert: a text, a boolean on a number, an empty list.
        { "a count that is no number", "\"Bridge\": { \"MaxSessions\": \"many\" }" },
        { "a boolean on a number", "\"Compaction\": { \"KeepRecentTurns\": true }" },
        { "an empty list on a switch of the compaction", "\"Compaction\": { \"Enabled\": [] }" },
        { "an empty list on a timeout", "\"Bridge\": { \"StopTimeout\": [] }" },
        { "an empty list on the circuit's switch", "\"Decision\": { \"Enabled\": [] }" },
        { "an empty text on the circuit's switch", "\"Decision\": { \"Enabled\": \"\" }" },
        { "an empty object on the circuit's switch", "\"Decision\": { \"Enabled\": {} }" },
        { "a null on the circuit's switch", "\"Decision\": { \"Enabled\": null }" },
        { "a fail-closed judge, the circuit's switch written without a value", Provider + ", \"Decision\": { \"Enabled\": null }, \"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        { "a fail-closed judge, the circuit's switch an empty object", Provider + ", \"Decision\": { \"Enabled\": {} }, \"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        { "a fail-closed judge under a guardian written without a value", "\"Guardian\": { \"Enabled\": null, \"Llm\": { \"Enabled\": true } }" },
        { "a fail-closed judge, strict tools written without a value", "\"CrewFactory\": { \"StrictTools\": null }, \"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        { "a share and a size written without a value", "\"Compaction\": { \"TriggerShare\": null }, \"Skills\": { \"MaxSkillChars\": {} }" },
        { "a count written without a value", "\"Bridge\": { \"MaxSessions\": null }" },
        { "a list on the summary's profile", "\"Compaction\": { \"Profile\": [\"a\"] }" },
        { "an empty list on the guardian's switch", "\"Guardian\": { \"Enabled\": [] }" },
        { "an empty list on strict tools", "\"CrewFactory\": { \"StrictTools\": [] }" },
        { "empty lists of names", "\"Bridge\": { \"Allow\": [], \"Deny\": [] }, \"Decision\": { \"Route\": { \"Ladder\": [] } }" },
        // A file the configuration does not load, and its neighbour that it does.
        { "a key written twice in two letter cases", "\"Compaction\": { \"Enabled\": true, \"enabled\": false }" },
        { "a list beside its first item", "\"Bridge\": { \"Allow\": [\"a\"], \"Allow:0\": \"b\" }" },
        { "a value under two spellings of a section", "\"Compaction\": { \"Enabled\": true }, \"compaction\": { \"Enabled\": true }" },
        { "two spellings of a section with keys of their own", "\"Compaction\": { \"Prune\": { \"Enabled\": true } }, \"compaction\": { \"Enabled\": true }" },
        // Two refusals: the host raises the first it meets.
        { "a fail-closed judge with no provider and a share out of bounds", "\"Guardian\": { \"Llm\": { \"Enabled\": true } }, \"Compaction\": { \"TriggerShare\": 2 }" },
        { "a fail-closed judge with no provider and a misspelt key", "\"Guardian\": { \"Llm\": { \"Enabled\": true } }, \"Skills\": { \"Libary\": \"/x\" }" },
        { "a share out of bounds and a misspelt key", "\"Compaction\": { \"TrigerShare\": 1, \"TriggerShare\": 2 }" },
        { "a misspelt key and a count out of bounds", "\"Compaction\": { \"TrigerShare\": 1 }, \"Bridge\": { \"MaxSessions\": 0 }" },
        { "a count that is no number and a misspelt key", "\"Bridge\": { \"MaxSessions\": \"many\", \"Port\": 1 }" },
        { "a count that is no number and a prefix that is no address", "\"Bridge\": { \"Prefix\": \"not a url\", \"MaxSessions\": \"many\" }" },
        { "a timeout that is no time and an address that is none", "\"Tools\": { \"Bridge\": { \"ConnectTimeout\": \"soon\", \"Address\": \"not a url\", \"Allow\": [\"a\"] } }" },
        { "a ladder of one rung that is auto", "\"Decision\": { \"Route\": { \"Ladder\": [\"auto\"] } }" },
        { "a ladder of one rung and levels out of order", "\"Decision\": { \"Effort\": { \"Levels\": [\"high\", \"low\"] }, \"Route\": { \"Ladder\": [\"default\"] } }" },
        { "levels out of order and a share out of bounds", "\"Decision\": { \"Effort\": { \"Levels\": [\"high\", \"low\"] } }, \"Skills\": { \"MaxSkillChars\": 0 }" },
        { "a switch that is no boolean and a count out of bounds", "\"Decision\": { \"Enabled\": \"x\" }, \"Bridge\": { \"MaxSessions\": 0 }" },
        { "the guardian's switch and the circuit's, neither a boolean", "\"Decision\": { \"Enabled\": \"x\" }, \"Guardian\": { \"Enabled\": \"z\" }" },
        { "a provider's timeout of zero and a count out of bounds", Provider.Replace("}", ", \"TimeoutSeconds\": 0 }", StringComparison.Ordinal) + ", \"Bridge\": { \"MaxSessions\": 0 }" },
        { "a provider's timeout of zero and a misspelt key", Provider.Replace("}", ", \"TimeoutSeconds\": 0 }", StringComparison.Ordinal) + ", \"Skills\": { \"Libary\": \"/x\" }" },
        { "strict tools that are no boolean and an unknown provider", "\"CrewFactory\": { \"StrictTools\": \"y\" }, \"Decision\": { \"Provider\": \"nope\" }" },
        { "an unknown provider and a share out of bounds", "\"Decision\": { \"Provider\": \"nope\" }, \"Compaction\": { \"TriggerShare\": 2 }" },
        { "a decision profile named auto and a share out of bounds", "\"Decision\": { \"Profiles\": { \"auto\": { \"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" } } ] } } }, \"Compaction\": { \"TriggerShare\": 2 }" },
        // The decision profiles, judged while the host is composed: one row per rule of the engine.
        { "a decision profile that is right", DecisionProfile },
        { "a decision profile with every kind of stage", Profile("\"Target\": \"default\", \"FailClosed\": true, \"Stages\": [ { \"Kind\": \" Guard \", \"Phase\": \"request\", \"Risks\": { \"x\": \"y\" } }, { \"Kind\": \"ROUTE\", \"Routes\": { \"default\": \"anything\" }, \"Fallback\": \"default\", \"MinConfidence\": 1 }, { \"Kind\": \"verify\", \"EscalateTo\": \"default\", \"Steps\": \"ALL\", \"MinProbability\": 0 } ]") },
        { "a decision profile with no target", Profile("\"Stages\": [ " + Guard + " ]") },
        { "a decision profile with a blank target", Profile("\"Target\": \"  \", \"Stages\": [ " + Guard + " ]") },
        { "a decision profile with no stage", Profile("\"Target\": \"default\"") },
        { "a decision profile with an empty list of stages", Profile("\"Target\": \"default\", \"Stages\": []") },
        { "a stage of an unknown kind", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"nope\" } ]") },
        { "a stage with no kind", Profile("\"Target\": \"default\", \"Stages\": [ null ]") },
        { "a guard with no risk", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\" } ]") },
        { "a guard on a phase that is none", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"Phase\": \"later\" } ]") },
        { "a guard on a phase written as its number", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"Phase\": \"1\" } ]") },
        { "a guard on a phase written as a number that is none", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"Phase\": \"7\" } ]") },
        { "a guard's block threshold above one", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"BlockThreshold\": 2 } ]") },
        { "a guard's warn threshold under zero, with decimals", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"WarnThreshold\": -0.5 } ]") },
        { "a guard's threshold that is no number", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"BlockThreshold\": \"NaN\" } ]") },
        { "a route with no profile", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"route\" } ]") },
        { "a route's confidence above one", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"route\", \"Routes\": { \"default\": \"anything\" }, \"MinConfidence\": 3 } ]") },
        { "a route to a decision profile", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"route\", \"Routes\": { \"P\": \"itself\" } } ]") },
        { "a route that falls back on a decision profile", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"route\", \"Routes\": { \"default\": \"anything\" }, \"Fallback\": \" p \" } ]") },
        { "a verify with nowhere to escalate", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"verify\" } ]") },
        { "a verify's probability above one", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"verify\", \"EscalateTo\": \"default\", \"MinProbability\": 1.5 } ]") },
        { "a verify on steps that are none", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"verify\", \"EscalateTo\": \"default\", \"Steps\": \"some\" } ]") },
        { "a decision profile whose target is a decision profile", Profile("\"Target\": \"p\", \"Stages\": [ " + Guard + " ]") },
        { "two things wrong in one stage and a second stage wrong", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Phase\": \"later\", \"WarnThreshold\": 4 }, { \"Kind\": \"nope\" } ]") },
        // The binder leaves out a stage with a value it cannot convert, and reads as off a switch that is no boolean.
        { "a single stage with a threshold that is no number", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"BlockThreshold\": \"x\" } ]") },
        { "a stage with a threshold that is no number beside a stage that is right", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"BlockThreshold\": \"x\" }, " + Guard + " ]") },
        { "a decision profile whose FailClosed is no boolean", Profile("\"Target\": \"default\", \"FailClosed\": \"x\", \"Stages\": [ " + Guard + " ]") },
        { "a decision profile that is a text", "\"Decision\": { \"Profiles\": { \"p\": \"x\" } }" },
        { "a decision profile written without a value", "\"Decision\": { \"Profiles\": { \"p\": null } }" },
        // Where the host meets a profile it refuses: after the choice of a provider, before every section.
        { "a decision profile with no stage and an unknown provider", Profile("\"Target\": \"default\"").Replace("\"Profiles\"", "\"Provider\": \"nope\", \"Profiles\"", StringComparison.Ordinal) },
        { "a decision profile with no stage and strict tools that are no boolean", Profile("\"Target\": \"default\"") + ", \"CrewFactory\": { \"StrictTools\": \"y\" }" },
        { "a decision profile with no stage and a share out of bounds", Profile("\"Target\": \"default\"") + ", \"Compaction\": { \"TriggerShare\": 2 }" },
        { "a decision profile with no stage and a ladder of one rung", Profile("\"Target\": \"default\"").Replace("\"Profiles\"", "\"Route\": { \"Ladder\": [\"default\"] }, \"Profiles\"", StringComparison.Ordinal) },
        // The keys of the decision sections that are no setting.
        { "a misspelt key of a decision profile", Profile("\"Target\": \"default\", \"FailClose\": true, \"Stages\": [ " + Guard + " ]") },
        { "a misspelt key of a stage", Profile("\"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" }, \"BlockTreshold\": 0.4 } ]") },
        { "a misspelt key of the decision section", "\"Decision\": { \"Enable\": false }" },
        { "a misspelt key of the ladder's section", "\"Decision\": { \"Route\": { \"Lader\": [\"default\", \"default\"] } }" },
        { "a misspelt key of the provider in use", Provider.Replace("}", ", \"Modle\": \"x\" }", StringComparison.Ordinal) },
        { "a misspelt key of a provider left aside", Provider + ", \"Liquid\": { \"Modle\": \"x\" }" },
        // The guardian: its values, its three rules, its keys.
        { "a judge's threshold above one", "\"Guardian\": { \"Llm\": { \"BlockThreshold\": 2 } }" },
        { "a judge's warn threshold under zero", "\"Guardian\": { \"Llm\": { \"WarnThreshold\": -0.1 } }" },
        { "a judge's threshold that is no number", "\"Guardian\": { \"Llm\": { \"BlockThreshold\": \"NaN\" } }" },
        { "a judge that reads no character", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }" },
        { "a judge that reads no character under a guardian off", "\"Guardian\": { \"Enabled\": false, \"Llm\": { \"MaxContentChars\": 0 } }" },
        { "phases a model does not judge", "\"Guardian\": { \"Llm\": { \"Phases\": [\"ToolExecution\", \"Input\", \"nope\"] } }" },
        { "phases a model judges, in any letter case or by number", "\"Guardian\": { \"Llm\": { \"Phases\": [\"input\", \"OUTPUT\", \"1\", \"\"] } }" },
        { "a phase written as a number that is none", "\"Guardian\": { \"Llm\": { \"Phases\": [\"7\"] } }" },
        { "a phase, a threshold and a size, all refused", "\"Guardian\": { \"Llm\": { \"Phases\": [\"Delegation\"], \"MaxContentChars\": 0, \"WarnThreshold\": -1 } }" },
        { "a threshold and a size, both refused", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0, \"WarnThreshold\": -1 } }" },
        { "a delegation depth that is no number", "\"Guardian\": { \"DefaultPolicy\": { \"MaxDelegationDepth\": \"x\" } }" },
        { "a size refused and a threshold that does not convert", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0, \"WarnThreshold\": \"x\" } }" },
        { "a misspelt key of the guardian", "\"Guardian\": { \"Enable\": false }" },
        { "a misspelt key of the judge", "\"Guardian\": { \"Llm\": { \"FailMod\": \"Open\" } }" },
        { "a misspelt key of the default policy", "\"Guardian\": { \"DefaultPolicy\": { \"InputGuard\": false } }" },
        { "the guardian as a file may write it", "\"Guardian\": { \"Enabled\": true, \"DefaultPolicy\": { \"OutputGuardEnabled\": false, \"MaxDelegationDepth\": 3 }, \"Llm\": { \"Enabled\": false, \"Phases\": [\"Input\"], \"Risks\": { \"leak\": \"a secret leaves\" }, \"BlockThreshold\": 0.9, \"WarnThreshold\": 0.4, \"FailMode\": \"Open\", \"MaxContentChars\": 4000 } }" },
        { "a judge's size refused and the circuit's switch no boolean", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }, \"Decision\": { \"Enabled\": \"x\" }" },
        { "a judge's size refused and a ladder of one rung", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }, \"Decision\": { \"Route\": { \"Ladder\": [\"default\"] } }" },
        { "a judge's size refused and a count out of bounds", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }, \"Bridge\": { \"MaxSessions\": 0 }" },
        { "a judge's size refused and a share out of bounds", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }, \"Compaction\": { \"TriggerShare\": 2 }" },
        { "a fail-closed judge with no provider and a size refused", "\"Guardian\": { \"Llm\": { \"Enabled\": true, \"MaxContentChars\": 0 } }" },
    };

    private const string Guard = "{ \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" } }";

    /// <summary>The decision section with one decision profile, <c>p</c>, of the given body.</summary>
    private static string Profile(string body) => "\"Decision\": { \"Profiles\": { \"p\": { " + body + " } } }";

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

    /// <summary>
    /// A decision profile is a profile to the engine: a name it shares with a model profile refuses the
    /// start, after the keys no section carries and before the fail-closed judge.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(", \"Skills\": { \"Libary\": \"/x\" }")]
    [InlineData(", \"Guardian\": { \"Llm\": { \"Enabled\": true } }")]
    public void The_host_refuses_a_name_two_profiles_share_as_Studio_says(string beside)
    {
        var settings = Settings(DecisionProfile.Replace("guarded", "strong", StringComparison.Ordinal) + beside, Strong.Replace("strong", "Strong", StringComparison.Ordinal));

        var (host, refusal) = Start(settings);
        host?.Dispose();
        var studio = StudioRefusals(settings);

        Assert.NotNull(refusal);
        Assert.NotEmpty(studio);
        Assert.Contains(studio[0], refusal, StringComparison.Ordinal);
        Assert.Contains("Two LLM profiles are named 'strong'.", studio);
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

    private const string Ladder = "\"Decision\": { \"Route\": { \"Ladder\": [\"default\", \"strong\"] } }";

    private const string RelayedPick =
        "\n  pick:\n    description: is it ready\n    expected_output: an answer\n    dependencies: [read]\n    decision:\n      questions:\n        go: { noul: the work is ready }\n      without: { agent: judge }";

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
        {
            "an agent on profile auto in a crew that states none, a ladder of two profiles", Provider + ", " + Ladder, Strong,
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: auto } }", OneTask, "decision: { mode: none }\n")
        },
        {
            "an agent on profile auto in a crew that states none, no ladder", Provider, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: auto } }", OneTask, "decision: { mode: none }\n")
        },
        {
            "a task on profile auto in a crew that states none, a ladder of two profiles", Provider + ", " + Ladder, Strong,
            Crew(Agents, "tasks:\n  read: { description: read, expected_output: notes, agent: analyst, llm_override: { profile: auto } }", "decision: { mode: none }\n")
        },
        {
            "an agent on profile auto, a ladder with a rung the host is not offered", Provider + ", " + Ladder, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: auto } }", OneTask)
        },
        {
            "a relay on profile auto that also has a task of its own, no ladder", Provider, "",
            Crew(
                "agents:\n  analyst: { role: Analyst, goal: read }\n  judge: { role: Judge, goal: judge, llm: { profile: auto } }",
                OneTask + RelayedPick + "\n  weigh: { description: weigh, expected_output: a weight, agent: judge, dependencies: [read] }",
                "decision: { mode: optional }\n")
        },
        {
            "a hierarchical manager on profile auto with no task, no ladder", Provider, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read }\n  boss: { role: Boss, goal: lead, llm: { profile: auto } }", OneTask)
                .Replace("process: sequential", "process: hierarchical\nmanager_agent: boss", StringComparison.Ordinal)
        },
        {
            "an agent on profile auto no task is assigned to, neither a manager nor a relay, no ladder", Provider, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read }\n  idle: { role: Idle, goal: wait, llm: { profile: auto } }", OneTask)
        },
        {
            "a fail-closed guarded profile, no provider", DecisionProfile.Replace("\"Target\"", "\"FailClosed\": true, \"Target\"", StringComparison.Ordinal), "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: guarded } }", OneTask)
        },
    };

    /// <summary>
    /// The engine loads the crew exactly when Studio does not block the launch for the load: a tool
    /// withheld under strict tools, a profile to pick with no ladder or on a rung the host is not
    /// offered, a profile to pick or a decision profile under the mode none. A crew that states
    /// <c>required</c> loads, the tool withheld with a warning, and so does a crew on a fail-closed
    /// guarded profile with the circuit off: Studio blocks them for the run the engine then refuses or
    /// blocks, which is no load.
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
        var blocksForTheLoad = finding is
        {
            BlocksLaunch: true, RefusedByMode: false, Kind: not DecisionPreflightKind.ProfileBlockedWithoutModel,
        };
        // What Studio blocks for the run alone is said all the same.
        if (what.StartsWith("a fail-closed guarded profile", StringComparison.Ordinal))
            Assert.True(finding is { Kind: DecisionPreflightKind.ProfileBlockedWithoutModel, BlocksLaunch: true }, what);

        Assert.True(
            (loadRefusal is not null) == blocksForTheLoad,
            $"{what}: the engine says «{loadRefusal ?? "loads"}», Studio says «{finding?.Kind.ToString() ?? "nothing"}» (blocks: {finding?.BlocksLaunch}).");
    }
}
