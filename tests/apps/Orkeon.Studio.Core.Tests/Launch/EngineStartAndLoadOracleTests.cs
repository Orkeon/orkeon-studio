using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkeon.Application.Interfaces;
using Orkeon.Domain.FileSystem;
using Orkeon.Hosting;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Core.Validation;

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
                configureLogging: (_, logging) => logging.SetMinimumLevel(LogLevel.None),
                // As the orkeon tool builds every host (CliLlmProviderModules.AddTo): with its provider module.
                configureServices: (_, services) => Orkeon.Interop.AgentCli.DependencyInjection.AgentCliServiceCollectionExtensions.AddOrkeonAgentCli(services)), null);
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
    /// the keys no section carries; a name two profiles share; the summary's profile the registry does
    /// not hold; and last the fail-closed judge.
    /// </summary>
    private static List<string> StudioRefusals(string settings, Func<string, string?>? environment = null)
    {
        var document = AppSettingsDocument.Parse(settings);
        var status = document.Decision.Resolve(environment ?? MachineEnvironment);
        if (status.RefusalStage == DecisionRefusalStage.File)
            return [status.Refusal!];

        string[] Decision(params DecisionRefusalStage[] stages) =>
            status.Refusal is { } refusal && stages.Contains(status.RefusalStage) ? [refusal] : [];
        // The walk's sentences (SettingsValidation.Walk): a key, a value not finite, written without
        // one (#450), single where a list or a section is expected (#454), an enumeration written as a
        // number or a list (#464).
        static bool OfTheWalk(string problem) =>
            problem.Contains(" is not a setting: ", StringComparison.Ordinal)
            || problem.Contains(" is not a finite number: write a number within the type's range.", StringComparison.Ordinal)
            || problem.Contains(" is a number where a name is expected: ", StringComparison.Ordinal)
            || problem.Contains(" is a list where a single name is expected: ", StringComparison.Ordinal)
            || problem.Contains(" takes names: write one or more of ", StringComparison.Ordinal)
            || problem.Contains(" is written without a value, which would bind it to ", StringComparison.Ordinal)
            || problem.Contains(" is a single value where a list is expected: ", StringComparison.Ordinal)
            || problem.Contains(" is a single value where a section of keys is expected: ", StringComparison.Ordinal);
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
            .. Decision(DecisionRefusalStage.Profiles),
            // RagLlm.EnsureProfileIsKnown, once the registry is built: the summary's profile.
            .. (document.Compaction.UnknownProfile() is { } unknown ? [unknown.Problem] : Array.Empty<string>()),
            .. Decision(DecisionRefusalStage.Judge),
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
        // A key that is no setting, on the caller's side and at any depth; the engine's computed keys are none since Orkeon #449.
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
        // An enumeration is written by its name (Orkeon #464): a number, even one it defines, and a comma
        // list where it takes one name are refused, and the value is never echoed (#465).
        { "a fail mode written as a number", "\"Guardian\": { \"Llm\": { \"FailMode\": \"1\" } }" },
        { "a fail mode written as a negative number", "\"Guardian\": { \"Llm\": { \"FailMode\": \"-1\" } }" },
        { "a fail mode written as a list", "\"Guardian\": { \"Llm\": { \"FailMode\": \"Closed, Open\" } }" },
        { "a fail mode in another letter case", "\"Guardian\": { \"Llm\": { \"FailMode\": \"open\" } }" },
        { "a fail mode no member has", "\"Guardian\": { \"Llm\": { \"FailMode\": \"Ajar\" } }" },
        { "a misspelt key of the guardian", "\"Guardian\": { \"Enable\": false }" },
        { "a misspelt key of the judge", "\"Guardian\": { \"Llm\": { \"FailMod\": \"Open\" } }" },
        { "a misspelt key of the default policy", "\"Guardian\": { \"DefaultPolicy\": { \"InputGuard\": false } }" },
        { "the guardian as a file may write it", "\"Guardian\": { \"Enabled\": true, \"DefaultPolicy\": { \"OutputGuardEnabled\": false, \"MaxDelegationDepth\": 3 }, \"Llm\": { \"Enabled\": false, \"Phases\": [\"Input\"], \"Risks\": { \"leak\": \"a secret leaves\" }, \"BlockThreshold\": 0.9, \"WarnThreshold\": 0.4, \"FailMode\": \"Open\", \"MaxContentChars\": 4000 } }" },
        { "a judge's size refused and the circuit's switch no boolean", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }, \"Decision\": { \"Enabled\": \"x\" }" },
        { "a judge's size refused and a ladder of one rung", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }, \"Decision\": { \"Route\": { \"Ladder\": [\"default\"] } }" },
        { "a judge's size refused and a count out of bounds", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }, \"Bridge\": { \"MaxSessions\": 0 }" },
        { "a judge's size refused and a share out of bounds", "\"Guardian\": { \"Llm\": { \"MaxContentChars\": 0 } }, \"Compaction\": { \"TriggerShare\": 2 }" },
        { "a fail-closed judge with no provider and a size refused", "\"Guardian\": { \"Llm\": { \"Enabled\": true, \"MaxContentChars\": 0 } }" },
        // A provider deduced from its section whose key is nowhere: the judge refuses the circuit off for it (no-key).
        { "a fail-closed judge, a provider without its key", KeylessProvider + ", \"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        { "a fail-closed judge, a provider whose key variable is unset", "\"Liquid\": { \"ApiKeyEnvVar\": \"STUDIO_ORACLE_UNSET_KEY\" }, \"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        { "a fail-open judge, a provider without its key", KeylessProvider + ", \"Guardian\": { \"Llm\": { \"Enabled\": true, \"FailMode\": \"Open\" } }" },
        // The registry refuses the reserved name, whatever its letter case, before a name two profiles share.
        { "a decision profile named default", "\"Decision\": { \"Profiles\": { \"Default\": { \"Target\": \"default\", \"Stages\": [ " + Guard + " ] } } }" },
        { "a decision profile named default, a fail-closed judge", "\"Decision\": { \"Profiles\": { \"default\": { \"Target\": \"default\", \"Stages\": [ " + Guard + " ] } } }, \"Guardian\": { \"Llm\": { \"Enabled\": true } }" },
        // The provider's options fail apart: the host throws the first, which Studio says first.
        { "two failures of the provider in use", "\"TypeSafe\": { \"ApiKey\": \"studio-oracle-tests\", \"TimeoutSeconds\": 0, \"MaxRetries\": -1 }" },
        // The summary's profile is looked up among the registry's names: the model profiles, then the decision profiles.
        { "a summary's profile no profile holds", "\"Compaction\": { \"Profile\": \"nope\" }" },
        { "a summary's profile that is a decision profile", "\"Compaction\": { \"Profile\": \"p\" }, " + Profile("\"Target\": \"default\", \"Stages\": [ " + Guard + " ]") },
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
    /// The <c>Provider</c> of <c>Llm</c> and of a profile, as a hand-written file may hold it (Orkeon #457),
    /// and whether the host starts on it.
    /// </summary>
    public static TheoryData<string, string, bool> ProviderSettings
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            const string Local = ", \"BaseUrl\": \"http://localhost:11434/v1\"";
            (string What, string Keys, bool Starts)[] cases =
            [
                ("a vendor of the catalogue", "\"Provider\": \"openai\", \"Model\": \"gpt-4o\"", false),
                ("a vendor in another letter case, with blanks", "\"Provider\": \" Anthropic \", \"Model\": \"claude-x\"", false),
                ("a vendor's alias", "\"Provider\": \"azure\", \"Model\": \"gpt-4o\"", false),
                ("the custom endpoint", "\"Provider\": \"custom\", \"Model\": \"m\"", false),
                ("the Docker Model Runner", "\"Provider\": \"docker-model-runner\", \"Model\": \"m\"", false),
                // The orkeon tool's provider module (ADR-020), whatever the case of its key.
                ("the agent-cli module", "\"Provider\": \"agent-cli\", \"Model\": \"claude-x\"", true),
                ("the agent-cli module in capitals, with blanks", "\"Provider\": \" Agent-CLI \", \"Model\": \"claude-x\"", true),
                ("the agent-cli module with a responses file", "\"Provider\": \"agent-cli\", \"Model\": \"m\", \"Responses\": \"/crew/responses.jsonl\"", false),
                ("a name no module answers", "\"Provider\": \"acme\", \"Model\": \"m\"", false),
                ("a name no module answers, with a responses file", "\"Provider\": \"acme\", \"Model\": \"m\", \"Responses\": \"/crew/responses.jsonl\"", false),
                ("a vendor with a responses file", "\"Provider\": \"openai\", \"Model\": \"m\", \"Responses\": \"/crew/responses.jsonl\"", false),
                ("the mock without its file", "\"Provider\": \"mock\", \"Model\": \"m\"", false),
                ("the mock with a blank file", "\"Provider\": \"mock\", \"Model\": \"m\", \"Responses\": \" \"", false),
                ("the mock in capitals with its file", "\"Provider\": \"MOCK\", \"Model\": \"m\", \"Responses\": \"/crew/responses.jsonl\"", true),
                // Matched untrimmed first, the padded mock is read as a module's key: its file is refused.
                ("the mock padded with blanks, with its file", "\"Provider\": \" mock \", \"Model\": \"m\", \"Responses\": \"/crew/responses.jsonl\"", false),
                ("the mock padded with blanks, without a file", "\"Provider\": \" mock \", \"Model\": \"m\"" + Local, true),
                ("a blank provider", "\"Provider\": \" \", \"Model\": \"m\"" + Local, true),
                ("a responses file without a provider", "\"Model\": \"m\", \"Responses\": \"/crew/responses.jsonl\"" + Local, true),
                // The JSON configuration gives a switch as True, a number as written.
                ("a provider written as a switch", "\"Provider\": true, \"Model\": \"m\"", false),
                ("a provider written as a number", "\"Provider\": 1.50, \"Model\": \"m\"", false),
            ];
            foreach (var (what, keys, starts) in cases)
            {
                data.Add("under Llm: " + what, $$"""{ "Llm": { {{keys}} } }""", starts);
                data.Add("under a profile: " + what, $$"""
                    { "Llm": { "Provider": "mock", "Model": "mock", "Responses": "/crew/responses.jsonl", "Profiles": { "p": { {{keys}} } } } }
                    """, starts);
            }

            // The configuration binds a section whatever the letter case of its keys: Studio judges it all the same.
            data.Add("under llm, in lowercase: a vendor of the catalogue", """{ "llm": { "Provider": "openai", "Model": "gpt-4o" } }""", false);
            data.Add("under a profile of LLM, in capitals: the mock without its file", """
                { "LLM": { "Provider": "mock", "Model": "mock", "Responses": "/crew/responses.jsonl", "PROFILES": { "p": { "provider": "mock", "Model": "m" } } } }
                """, false);
            data.Add("under llm, in lowercase: the mock with its file", """{ "llm": { "provider": "mock", "model": "mock", "responses": "/crew/responses.jsonl" } }""", true);
            return data;
        }
    }

    /// <summary>
    /// The host refuses to start on a <c>Provider</c> that is neither <c>mock</c> with its file nor the
    /// key of a provider module, and Studio's settings check says so in its sentence (Orkeon #457), as a
    /// launch of the file does (<see cref="LlmProviderRules.Check(AppSettingsDocument)"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(ProviderSettings))]
    public void The_host_refuses_a_provider_as_Studio_says(string what, string settings, bool starts)
    {
        var (host, refusal) = Start(settings);
        host?.Dispose();
        var document = AppSettingsDocument.Parse(settings);
        var studio = new AppSettingsValidator().Validate(document)
            .Where(message => message.Code == ValidationCodes.LlmProvider)
            .ToList();

        Assert.True((refusal is null) == (studio.Count == 0), $"{what}: the engine says «{refusal}», Studio says «{string.Join(" | ", studio.Select(message => message.Text))}».");
        // The rows are not vacuous: each starts or not as it says.
        Assert.Equal(starts, refusal is null);
        Assert.Equal(studio.Select(message => message.Text), LlmProviderRules.Check(document).Select(message => message.Text));
        if (refusal is not null)
        {
            Assert.Contains(studio[0].Text, refusal, StringComparison.Ordinal);
            // What blocks a launch says first what the host raises.
            Assert.Contains(LlmProviderRules.StartRefusals(document)[0].Text, refusal, StringComparison.Ordinal);
            Assert.All(studio, message => Assert.Equal(ValidationSeverity.Error, message.Severity));
        }
    }

    /// <summary>
    /// The host of a run, built with the agent-cli module as the tool builds it, refuses in
    /// <c>Llm:AgentCli</c> what Studio says first, in Studio's sentence, whatever provider the section
    /// names; and starts on a section Studio accepts.
    /// </summary>
    [Theory]
    [InlineData("""{ "Command": "claude", "MaxProcesses": 2 }""")]
    [InlineData("""{ "Engine": "codex" }""")]
    [InlineData("""{ "Command": "claude.cmd" }""")]
    [InlineData("""{ "MaxProcesses": 0 }""")]
    [InlineData("""{ "MaxToolResultCharacters": 999, "AutocompactTokens": 0 }""")]
    [InlineData("""{ "MaxProcesses": "many" }""")]
    [InlineData("""{ "Comand": "claude" }""")]
    [InlineData("""{ "MaxProcesses": null }""")]
    [InlineData("\"claude\"")]
    public void The_host_refuses_in_the_agent_cli_section_what_Studio_says(string agentCli)
    {
        var settings = $$"""{ "Llm": { "Provider": "mock", "Model": "mock", "Responses": "/crew/responses.jsonl", "AgentCli": {{agentCli}} } }""";

        var (host, refusal) = Start(settings);
        host?.Dispose();
        var studio = AppSettingsDocument.Parse(settings).AgentCli.Problems();

        Assert.True((refusal is null) == (studio.Count == 0), $"The engine says «{refusal}», Studio says «{string.Join(" | ", studio.Select(problem => problem.Problem))}».");
        if (refusal is not null)
            Assert.Contains(studio[0].Problem, refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The model settings whose refusals come in an order: a refusal met where a section is read stops
    /// the host there (the profiles before the <c>Llm</c> section, a reserved name before a provider), the
    /// agent-cli section is validated next, and a name no module answers is said last.
    /// </summary>
    [Theory]
    [InlineData("""{ "Llm": { "Provider": "openai", "Model": "m", "AgentCli": { "MaxProcesses": 0 } } }""", 1)]
    [InlineData("""{ "Llm": { "Provider": "acme", "Model": "m", "AgentCli": { "MaxProcesses": 0 } } }""", 2)]
    [InlineData("""{ "Llm": { "Provider": "openai", "Model": "m", "Profiles": { "b": { "Provider": "mock", "Model": "m" } } } }""", 1)]
    [InlineData("""{ "Llm": { "Provider": "mock", "Model": "mock", "Responses": "/crew/responses.jsonl", "Profiles": { "Default": { "Provider": "openai", "Model": "m" } } } }""", 1)]
    [InlineData("""{ "Llm": { "Provider": "mock", "Model": "mock", "Responses": "/crew/responses.jsonl", "Profiles": { "auto": { "Provider": "acme", "Model": "m" } } } }""", 1)]
    [InlineData("""{ "Llm": { "Provider": "acme", "Model": "m", "Profiles": { "p": { "Provider": "zeta", "Model": "m" } } } }""", 2)]
    [InlineData("""{ "llm": { "provider": "mock", "model": "mock", "responses": "/crew/responses.jsonl", "agentcli": "claude" } }""", 1)]
    public void The_host_raises_first_the_model_setting_Studio_says_first(string settings, int count)
    {
        var (host, refusal) = Start(settings);
        host?.Dispose();
        var studio = LlmProviderRules.StartRefusals(AppSettingsDocument.Parse(settings));

        Assert.NotNull(refusal);
        Assert.Equal(count, studio.Count);
        Assert.Contains(studio[0].Text, refusal, StringComparison.Ordinal);
    }

    private const string SecretLike = "sk-test-0123456789abcdef";

    /// <summary>The same refusals with the value a key cannot read: one that looks like an API key, written by mistake.</summary>
    public static TheoryData<string, string> SecretUnderTypedKeys => new()
    {
        { "under a boolean key", $"\"Compaction\": {{ \"Enabled\": \"{SecretLike}\" }}" },
        { "under a number key", $"\"Compaction\": {{ \"KeepRecentTurns\": \"{SecretLike}\" }}" },
        { "under a number key of a list's item", $"\"Decision\": {{ \"Profiles\": {{ \"p\": {{ \"Target\": \"default\", \"Stages\": [ {{ \"Kind\": \"guard\", \"Risks\": {{ \"x\": \"y\" }}, \"BlockThreshold\": \"{SecretLike}\" }} ] }} }} }}" },
        { "as the judged phases", $"\"Guardian\": {{ \"Llm\": {{ \"Phases\": \"{SecretLike}\" }} }}" },
        { "under an enumeration, beside a name", $"\"Guardian\": {{ \"Llm\": {{ \"FailMode\": \"Closed, {SecretLike}\" }} }}" },
        { "under the circuit's switch", $"\"Decision\": {{ \"Enabled\": \"{SecretLike}\" }}" },
        { "under the strict tools", $"\"CrewFactory\": {{ \"StrictTools\": \"{SecretLike}\" }}" },
    };

    /// <summary>
    /// No refusal repeats a value its key cannot read (Orkeon #465): it is not of the key's type, so it
    /// may be anything, an API key included, and the refusal reaches the screen and the logs. Studio's
    /// validation holds to it as the host's start does.
    /// </summary>
    [Theory]
    [MemberData(nameof(SecretUnderTypedKeys))]
    public void No_refusal_repeats_a_value_its_key_cannot_read(string what, string orkeon)
    {
        var settings = Settings(orkeon);

        var (host, refusal) = Start(settings);
        host?.Dispose();
        var studio = StudioRefusals(settings);
        var messages = new AppSettingsValidator().Validate(AppSettingsDocument.Parse(settings));

        Assert.False(refusal is null, $"{what}: the engine starts");
        Assert.NotEmpty(studio);
        Assert.DoesNotContain(SecretLike, refusal, StringComparison.Ordinal);
        Assert.All(studio, problem => Assert.DoesNotContain(SecretLike, problem, StringComparison.Ordinal));
        Assert.All(messages, message => Assert.DoesNotContain(SecretLike, message.Text, StringComparison.Ordinal));
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

    /// <summary>A Liquid provider deduced from its section, whose key is in neither the file nor its variable: the engine turns the circuit off (<c>no-key</c>, Orkeon #451).</summary>
    private const string KeylessProvider = "\"Liquid\": {}";

    /// <summary>What <c>Orkeon:Liquid:ApiKeyEnvVar</c> holds in each row of <see cref="The_host_names_the_missing_key_as_Studio_does"/>; null leaves it unset.</summary>
    public static TheoryData<string, string?> ApiKeyVariables => new()
    {
        { "unset", null },
        { "the provider's own default, written out", " LIQUID_API_KEY " },
        { "a custom variable", "STUDIO_ORACLE_UNSET_KEY" },
        { "a key pasted in place of the variable", "sk_live_0123456789abcdef" },
    };

    /// <summary>
    /// The remedy of a missing key, the packed engine's (<c>DecisionApiKey.Remedy</c>, read in the
    /// <c>Detail</c> of the circuit the host turns off, Orkeon #451/#467) against Studio's
    /// (<see cref="DecisionStatus.MissingKeyVariable"/> or <see cref="DecisionStatus.MissingKeyVariableSetting"/>):
    /// the variable is named when it is the provider's own, unset or written out; any other value is
    /// named by its setting alone, and never repeated.
    /// </summary>
    [Theory]
    [MemberData(nameof(ApiKeyVariables))]
    public void The_host_names_the_missing_key_as_Studio_does(string what, string? written)
    {
        var liquid = written is null ? "\"Liquid\": {}" : $"\"Liquid\": {{ \"ApiKeyEnvVar\": \"{written}\" }}";
        var settings = Settings(liquid);
        var (host, refusal) = Start(settings);
        Assert.True(host is not null, $"{what}: the host refused to start: {refusal}");
        Orkeon.Application.Crew.Execution.DecisionCircuit circuit;
        using (host)
            circuit = Orkeon.Application.Crew.Execution.DecisionCircuit.OfHost(host.Services);

        var status = AppSettingsDocument.Parse(settings).Decision.Resolve(MachineEnvironment);
        Assert.True(status.KeyMissing, what);
        Assert.False(circuit.Enabled, what);
        Assert.Equal(Orkeon.Application.Crew.Execution.DecisionCircuitCause.NoKey, circuit.Cause);
        var remedy = status.MissingKeyVariable is { } variable
            ? $"set the environment variable {variable}, or Orkeon:Liquid:ApiKey"
            : $"set the variable named by {status.MissingKeyVariableSetting}, or Orkeon:Liquid:ApiKey";
        Assert.Equal("no Liquid API key: " + remedy, circuit.Detail);
        if (written is not null && written.Trim() != "LIQUID_API_KEY")
            Assert.DoesNotContain(written, circuit.Detail, StringComparison.Ordinal);
    }

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
            // Orkeon #476: the validator and the forge ask a decision task for no expected output; the
            // factory used to build it with one and crashed on the blank (exit 2). Studio never blocks it.
            "a decision task that declares no expected output", Provider, "",
            Crew(
                "agents:\n  analyst: { role: Analyst, goal: read }\n  judge: { role: Judge, goal: judge }",
                OneTask + RelayedPick.Replace("\n    expected_output: an answer", "", StringComparison.Ordinal),
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
            "a holder of the tool, a provider without its key, strict tools", KeylessProvider, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, tools: [system_one_decide] }", OneTask)
        },
        {
            "a holder of the tool, a provider whose variable, named by its setting, is empty, strict tools",
            "\"Liquid\": { \"ApiKeyEnvVar\": \"STUDIO_ORACLE_UNSET_KEY\" }", "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, tools: [system_one_decide] }", OneTask)
        },
        {
            "a holder of the tool in a crew that states required, a provider without its key", KeylessProvider, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, tools: [system_one_decide] }", OneTask, "decision: { mode: required }\n")
        },
        {
            "a crew that states required, a provider with its key", Provider, "",
            Crew(Agents, OneTask, "decision: { mode: required }\n")
        },
        {
            "a crew that states required, a provider without its key", KeylessProvider, "",
            Crew(Agents, OneTask, "decision: { mode: required }\n")
        },
        {
            "a relayed decision task in a crew that states optional, a provider without its key", KeylessProvider, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read }\n  judge: { role: Judge, goal: judge }", OneTask + RelayedPick, "decision: { mode: optional }\n")
        },
        {
            "a fail-closed guarded profile, no provider", FailClosedProfile, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: guarded } }", OneTask)
        },
        {
            "a fail-closed guarded profile, a provider without its key", KeylessProvider + ", " + FailClosedProfile, "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, llm: { profile: guarded } }", OneTask)
        },
        {
            "a holder of the tool, a provider without its key, lenient tools", KeylessProvider + ", \"CrewFactory\": { \"StrictTools\": false }", "",
            Crew("agents:\n  analyst: { role: Analyst, goal: read, tools: [system_one_decide] }", OneTask)
        },
    };

    private static readonly string FailClosedProfile =
        DecisionProfile.Replace("\"Target\"", "\"FailClosed\": true, \"Target\"", StringComparison.Ordinal);

    /// <summary>
    /// The engine loads the crew exactly when Studio does not block the launch for the load: a tool
    /// withheld under strict tools, a profile to pick with no ladder or on a rung the host is not
    /// offered, a profile to pick or a decision profile under the mode none. A crew that states
    /// <c>required</c> loads, the tool withheld with a warning, and so does a crew on a fail-closed
    /// guarded profile with the circuit off: Studio blocks them for the run the engine then refuses or
    /// blocks, which is no load. Of a crew it loads, the engine refuses the run before its first task
    /// (<c>DecisionUnavailableException.Refusing</c>, exit code 69) exactly when Studio says it does
    /// (<see cref="DecisionPreflightFinding.RefusedByMode"/>): a provider without its key turns the
    /// circuit off as the other causes do (<c>no-key</c>, Orkeon #451).
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
        Orkeon.Application.Crew.Execution.DecisionUnavailableException? runRefusal = null;
        bool? toolWithheld = null;
        bool? profileBlocks = null;
        using (host)
        {
            var scope = host.Services.CreateAsyncScope();
            await using var _ = scope.ConfigureAwait(false);
            var loader = scope.ServiceProvider.GetRequiredService<ICrewDefinitionLoader>();
            var factory = scope.ServiceProvider.GetRequiredService<ICrewFactory>();
            try
            {
                var definition = await loader.LoadFromStringAsync(yaml, TestContext.Current.CancellationToken);
                var crew = await factory.CreateFromConfigAsync(definition, TestContext.Current.CancellationToken);
                var circuit = Orkeon.Application.Crew.Execution.DecisionCircuit.Of(scope.ServiceProvider);
                runRefusal = Orkeon.Application.Crew.Execution.DecisionUnavailableException.Refusing(crew, circuit);

                // The tool, withheld or offered to the agents the crew loaded.
                if (yaml.Contains("system_one_decide", StringComparison.Ordinal))
                {
                    var agents = await scope.ServiceProvider.GetRequiredService<Orkeon.Domain.Agent.IAgentRepository>()
                        .GetByIdsAsync(crew.Agents, TestContext.Current.CancellationToken);
                    Assert.NotEmpty(agents);
                    toolWithheld = !agents.Any(agent => agent.Tools.Any(tool => tool.Name == "system_one_decide"));
                }

                // A call through the guarded profile: blocked by the profile built with the circuit off. With the
                // circuit on its guard would ask the provider, which no row reaches: it does not block for want of it.
                if (yaml.Contains("profile: guarded", StringComparison.Ordinal))
                {
                    if (circuit.Enabled)
                    {
                        profileBlocks = false;
                    }
                    else
                    {
                        var profile = scope.ServiceProvider.GetRequiredService<Orkeon.Application.Interfaces.Ports.ILlmProfileRegistry>().Resolve("guarded");
                        var response = await profile.Provider.ChatAsync(
                            [Orkeon.Domain.SharedKernel.ValueObjects.LlmMessage.User("hello")],
                            cancellationToken: TestContext.Current.CancellationToken);
                        profileBlocks = response.ErrorType == Orkeon.Infrastructure.LLMs.Decision.DecisionLlmProvider.BlockedErrorType;
                    }
                }
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
        if (loadRefusal is null)
        {
            Assert.True(
                (runRefusal is not null) == finding is { RefusedByMode: true, BlocksLaunch: true },
                $"{what}: the engine's run says «{runRefusal?.Message ?? "runs"}», Studio says «{finding?.Kind.ToString() ?? "nothing"}» (blocks: {finding?.BlocksLaunch}).");
        }

        // The tool withheld, and every call through a fail-closed guarded profile blocked, exactly when Studio
        // says the run goes without the decision models: whatever turns the circuit off, the missing key included.
        if (toolWithheld is { } withheld)
        {
            Assert.True(
                withheld == finding is { Kind: DecisionPreflightKind.ToolWithheld or DecisionPreflightKind.RequiredWithoutModel },
                $"{what}: the engine {(withheld ? "withholds" : "offers")} the tool, Studio says «{finding?.Kind.ToString() ?? "nothing"}».");
        }

        if (profileBlocks is { } blocks)
        {
            Assert.True(
                blocks == finding is { Kind: DecisionPreflightKind.ProfileBlockedWithoutModel },
                $"{what}: the engine {(blocks ? "blocks" : "lets through")} a call through the profile, Studio says «{finding?.Kind.ToString() ?? "nothing"}».");
        }

        // A provider without its key: the circuit the engine resolves is off for it, and Studio says so.
        if (what.Contains("without its key", StringComparison.Ordinal) || what.Contains("named by its setting, is empty", StringComparison.Ordinal))
            Assert.True(status.KeyMissing, what);
    }
}
