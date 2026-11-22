using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Orkeon.Application.Crew.Execution;
using Orkeon.Application.Crew.Routing;
using Orkeon.Application.Interfaces.Ports;
using Orkeon.Infrastructure.Decision;
using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Infrastructure.LLMs.Decision;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Typed view over the decision models (System One): the circuit's switch and provider name
/// (<c>Orkeon:Decision:Enabled</c>, <c>Orkeon:Decision:Provider</c>), the ladder of profiles and the
/// thinking levels System One picks among (<c>Orkeon:Decision:Route:Ladder</c>,
/// <c>Orkeon:Decision:Effort:Levels</c>) and the section of each provider
/// (<c>Orkeon:TypeSafe</c>, <c>Orkeon:Liquid</c>, <c>Orkeon:Kev</c>); <see cref="Resolve"/> tells what a
/// run of the file gets, and the first thing the host's start refuses in it. Every write lands in
/// place, field by field, so a key Studio does not write (the decision profiles under <c>Orkeon:Decision:Profiles</c>,
/// which <see cref="ProfileProblems"/> judges all the same, a provider's <c>ApiKey</c>) survives an edit. The providers, their sections and their defaults are the
/// engine's own (<see cref="DecisionProviderDescriptor"/>), read from the <c>Orkeon</c> package.
/// </summary>
public sealed class DecisionSection
{
    /// <summary>Configuration path of the circuit's section.</summary>
    public const string SectionPath = DecisionCircuitOptions.SectionName;

    /// <summary>Configuration path of the circuit's switch.</summary>
    public const string EnabledPath = SectionPath + ":Enabled";

    /// <summary>Configuration path of the provider's name.</summary>
    public const string ProviderPath = SectionPath + ":Provider";

    /// <summary>
    /// Configuration path of the runners' strict tool resolution (on by default): on, a crew that names a
    /// tool no registration gives is refused at load; off, the tool is dropped with a log line.
    /// </summary>
    public const string StrictToolsPath = "Orkeon:CrewFactory:StrictTools";

    /// <summary>
    /// Configuration path of the ladder System One picks a profile on for an agent on
    /// <c>llm.profile: auto</c>: two or more profile names, cheapest first.
    /// </summary>
    public const string RouteLadderPath = LlmProfiles.RouteLadderKey;

    /// <summary>
    /// Configuration path of the thinking levels System One picks among for <c>thinking: { effort: auto }</c>,
    /// cheapest first; empty, the engine's three (<see cref="DefaultEffortLevels"/>).
    /// </summary>
    public const string EffortLevelsPath = AgentEffortOptions.LevelsKey;

    /// <summary>Configuration path of the decision profiles, each a name a crew may put in <c>llm.profile</c>.</summary>
    public const string ProfilesPath = SectionPath + ":Profiles";

    /// <summary>The thinking levels the engine picks among when <see cref="EffortLevelsPath"/> lists none.</summary>
    public static IReadOnlyList<string> DefaultEffortLevels => AgentEffortOptions.DefaultLevels;

    /// <summary>Every thinking level the engine knows, cheapest first.</summary>
    public static IReadOnlyList<string> KnownEffortLevels => Orkeon.Domain.SharedKernel.ValueObjects.LlmThinkingConfig.Levels;

    /// <summary>
    /// Configuration path of the guardian's switch (on by default): off, no guard runs, the LLM judge of
    /// <c>Orkeon:Guardian:Llm</c> included.
    /// </summary>
    public const string GuardianEnabledPath = "Orkeon:Guardian:Enabled";

    /// <summary>The prefix of the environment variables a run reads as settings, above the file.</summary>
    public const string EnvironmentPrefix = Orkeon.Constants.Configuration.EnvironmentVariableNames.SettingsPrefix;

    // The fields of a provider's section (SystemOneProviderOptions), probed in the environment.
    private static readonly string[] ProviderFields = ["ApiKey", "ApiKeyEnvVar", "BaseUrl", "Model", "TimeoutSeconds", "MaxRetries"];

    // The engine's defaults (SystemOneProviderOptions), copied so the settings screen can show them
    // as watermarks; EngineDefaultsDriftTests pins each one against the original.

    /// <summary>Default of a provider's <c>TimeoutSeconds</c>.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>Default of a provider's <c>MaxRetries</c>.</summary>
    public const int DefaultMaxRetries = 3;

    private readonly AppSettingsDocument _document;

    internal DecisionSection(AppSettingsDocument document) => _document = document;

    /// <summary>The providers the engine knows, in the order its documentation lists them.</summary>
    public static IReadOnlyList<DecisionProviderDescriptor> Providers => DecisionProviderDescriptor.All;

    /// <summary>
    /// Whether the circuit's section is written, in any letter case: the keys are read and written
    /// where the file spells them (<see cref="AppSettingsDocument.PathAsWritten"/>), as the run binds them.
    /// </summary>
    public bool Exists => _document.GetNode(_document.PathAsWritten(SectionPath)) is not null;

    /// <summary>The switch as written, null when it reads as no boolean; what a run makes of it is <see cref="IsEnabled"/>.</summary>
    public bool? Enabled
    {
        get => _document.GetBoolean(_document.PathAsWritten(EnabledPath));
        set => _document.SetBoolean(_document.PathAsWritten(EnabledPath), value);
    }

    /// <summary>The path of the switch as the file spells it: where its text is read when it reads as no boolean.</summary>
    public string EnabledPathAsWritten => _document.PathAsWritten(EnabledPath);

    /// <summary>
    /// Whether decision tasks ask the decision model, as the binder reads the switch of the file: on
    /// when the key is absent or does not convert (which the start refuses), off when it is written
    /// without a value (a JSON null, an empty object), else what it says.
    /// </summary>
    public bool IsEnabled => SettingsBinding.Bind<DecisionCircuitOptions>(ToConfiguration(_document.Root), SectionPath, []).Enabled;

    /// <summary>The provider's name as written (<c>typesafe</c>, <c>liquid</c>, <c>kev</c>); null deduces it.</summary>
    public string? Provider
    {
        get => _document.GetString(_document.PathAsWritten(ProviderPath));
        set => _document.SetString(_document.PathAsWritten(ProviderPath), string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>
    /// The ladder of profiles the file writes, cheapest first, as the binder reads it whatever its shape
    /// (a list written as an object, an entry that is no text); empty when the file has none. Setting
    /// an empty ladder removes the key; setting the rungs it already reads writes nothing, so a ladder
    /// shown and applied untouched stays as the file writes it.
    /// </summary>
    public IReadOnlyList<string> RouteLadder
    {
        get => Names(SettingsBinding.Bind<AgentRouteOptions>(ToConfiguration(_document.Root), AgentRouteOptions.SectionName, []).Ladder);
        set => SetList(RouteLadderPath, value, RouteLadder);
    }

    /// <summary>
    /// The thinking levels as the file writes them, cheapest first; empty when the file has none, the
    /// engine then picking among <see cref="DefaultEffortLevels"/>. Read and written as
    /// <see cref="RouteLadder"/> is: as the binder reads the key, nothing written when nothing changed.
    /// Setting an empty list removes the key.
    /// </summary>
    public IReadOnlyList<string> EffortLevels
    {
        get => Names(SettingsBinding.Bind<AgentEffortOptions>(ToConfiguration(_document.Root), AgentEffortOptions.SectionName, []).Levels);
        set => SetList(EffortLevelsPath, value, EffortLevels);
    }

    // A list of names as the binder filled it: an entry it read as nothing is an empty name.
    internal static IReadOnlyList<string> Names(IEnumerable<string?> bound) => [.. bound.Select(name => name ?? string.Empty)];

    /// <summary>
    /// The names of a list typed on one line: split on commas, semicolons and line breaks, trimmed,
    /// blanks dropped.
    /// </summary>
    public static IReadOnlyList<string> SplitList(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// How many rungs of the ladder are probed in the environment (<c>…__Route__Ladder__0</c> and on): a
    /// ladder a variable gives beyond them is not seen.
    /// </summary>
    internal const int MaxProbedRungs = 8;

    /// <summary>
    /// What the host's start refuses in a ladder and in a list of levels, by the engine's own rules
    /// (<c>AgentRouteOptions.Problems</c>, <c>AgentEffortOptions.Problems</c>); empty when both are accepted.
    /// </summary>
    public static IReadOnlyList<string> PickProblems(IReadOnlyList<string> ladder, IReadOnlyList<string> levels)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        ArgumentNullException.ThrowIfNull(levels);
        var route = new AgentRouteOptions();
        foreach (var rung in ladder)
            route.Ladder.Add(rung);
        var effort = new AgentEffortOptions();
        foreach (var level in levels)
            effort.Levels.Add(level);
        return [.. AgentRouteOptions.Problems(route), .. AgentEffortOptions.Problems(effort)];
    }

    private void SetList(string path, IReadOnlyList<string> values, IReadOnlyList<string> read)
    {
        ArgumentNullException.ThrowIfNull(values);
        var written = _document.PathAsWritten(path);
        if (values.Count == 0)
            _document.Remove(written);
        else if (!values.SequenceEqual(read, StringComparer.Ordinal))
            _document.SetStringArray(written, values);
    }

    /// <summary>The settings of the provider <paramref name="descriptor"/> names.</summary>
    public DecisionProviderSettings For(DecisionProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new DecisionProviderSettings(_document, descriptor);
    }

    /// <summary>
    /// Which provider a run of these settings uses, as the engine resolves it
    /// (<c>DecisionProviderChoice.Resolve</c>), whether the circuit is on, and whether the runner
    /// resolves tools strictly. The settings are composed as the runner composes them
    /// (<c>RunnerSettings.ComposeSources</c>): the environment variables without a prefix, then this
    /// file, then the <c>ORKEON_</c> variables, which win. The environment is probed through
    /// <paramref name="environment"/> for the names these settings take (<c>Orkeon__Decision__Provider</c>,
    /// <c>ORKEON_Orkeon__TypeSafe__Model</c>...), each spelt as here: a variable the run reads in another
    /// letter case (Linux tells them apart, the configuration does not) is not seen.
    /// </summary>
    /// <param name="environment">
    /// Reads an environment variable: where the providers' keys live, and the settings a run reads
    /// above or under the file. It should see what the spawned CLI inherits.
    /// </param>
    public DecisionStatus Resolve(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var configuration = ToConfiguration(_document.Root, environment);

        // Everything is read first, so a refusal leaves in the status all the file still tells; then the
        // refusals are looked for in the order the host meets them, and the first one is the status's.
        var enabledRefusal = ReadCircuitSwitch(configuration, out var enabled);
        var strictToolsRefusal = ReadStrictTools(configuration, out var strictTools);
        var guardianRefusal = ReadClosedJudge(configuration, out var closedJudge);
        var picksRefusal = ReadPicks(configuration, out var ladder, out var unknownRungs);
        var decisionProfiles = configuration.GetSection(ProfilesPath).GetChildren().Select(profile => profile.Key).ToList();
        var status = new DecisionStatus(enabled, Provider: null, DecisionProviderSelection.Deduced, [], Refusal: null)
        {
            StrictTools = strictTools,
            RouteLadder = ladder,
            UnknownRungs = unknownRungs,
            DecisionProfiles = decisionProfiles,
            BlockingProfiles = BlockingProfilesOf(configuration),
            ClosedJudge = closedJudge,
        };

        // The file itself: the configuration does not load it.
        if (_document.LoadRefusal() is { } load)
            return status with { Refusal = load.Problem, RefusalStage = DecisionRefusalStage.File };

        // While the host is composed, before any section is judged: the runner's switch, the choice of
        // a provider, the decision profiles.
        if (strictToolsRefusal is not null)
            return status with { Refusal = strictToolsRefusal, RefusalStage = DecisionRefusalStage.Composition };

        try
        {
            var choice = DecisionProviderChoice.Resolve(configuration, environment);
            status = status with
            {
                Provider = choice.Provider,
                How = choice.How,
                IgnoredSections = choice.IgnoredSections,
            };
            if (choice.Provider is { } provider && MissingKey(configuration, provider, environment) is { } missing)
                status = status with { MissingKeyVariable = missing.Variable, MissingKeyVariableSetting = missing.Setting };
        }
        catch (InvalidOperationException ex)
        {
            return status with { Refusal = ex.Message, RefusalStage = DecisionRefusalStage.Composition };
        }

        if (ProfileProblemsOf(configuration).Select(problem => problem.Problem).FirstOrDefault() is { } profile)
            return status with { Refusal = profile, RefusalStage = DecisionRefusalStage.Composition };

        // Then the declared sections, in the host's order: the ladder and the levels System One picks
        // on, validated at start whatever the circuit; the guardian; the circuit's switch; the section
        // of the provider in use.
        if (picksRefusal is not null)
            return status with { Refusal = picksRefusal, RefusalStage = DecisionRefusalStage.Picks };
        if ((guardianRefusal ?? enabledRefusal) is { } unconverted)
            return status with { Refusal = unconverted, RefusalStage = DecisionRefusalStage.Switches };
        if (status.Provider is { } chosen && OptionRefusals(configuration, chosen) is [var options, ..])
            return status with { Refusal = options, RefusalStage = DecisionRefusalStage.Provider };

        // A decision profile is registered as a profile, after the model profiles, and the registry judges
        // each registration in turn (LlmProfileRegistry): the reserved name of the default profile, then a
        // name for two, in the spelling of the decision profile.
        var modelProfiles = configuration.GetSection(LlmProfilesSection.SectionPath).GetChildren().Select(profile => profile.Key).ToList();
        foreach (var name in decisionProfiles)
        {
            if (LlmProfiles.IsDefault(name))
                return status with { Refusal = DefaultProfileNameRefusal, RefusalStage = DecisionRefusalStage.Profiles };
            if (modelProfiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                return status with { Refusal = SharedProfileNameRefusal(name), RefusalStage = DecisionRefusalStage.Profiles };
        }

        // Last, a fail-closed LLM judge cannot judge with the circuit off: the host refuses to start, in the
        // circuit's own cause, the settings' switch first, then the missing provider, then the missing key
        // (DecisionCircuit, as the host resolves it).
        return closedJudge && JudgeCircuit(status) is { } off
            ? status with
            {
                Refusal = GuardianLlmOptions.CircuitRefusal(off.OffReason!),
                RefusalStage = DecisionRefusalStage.Judge,
            }
            : status;
    }

    /// <summary>
    /// The engine's sentence for a decision profile under the reserved name of the host's default profile
    /// (<c>LlmProfileRegistry</c>): the start is refused.
    /// </summary>
    internal const string DefaultProfileNameRefusal =
        $"'{LlmProfiles.Default}' is the reserved name of the host's default profile; a profile cannot take it.";

    /// <summary>
    /// The circuit the host resolves for <paramref name="status"/>, when it is off: by the settings'
    /// switch, by the missing provider, by the provider's missing key, in that order; null when it is on.
    /// </summary>
    private static DecisionCircuit? JudgeCircuit(DecisionStatus status)
    {
        if (!status.Enabled)
            return DecisionCircuit.OffBy(DecisionCircuitCause.Settings);
        if (status.Provider is not { } provider)
            return DecisionCircuit.OffBy(DecisionCircuitCause.NoProvider);
        return status.KeyMissing
            ? DecisionCircuit.OffBy(DecisionCircuitCause.NoKey) with { Detail = MissingKeySentence(provider, status.MissingKeyVariable is not null) }
            : null;
    }

    /// <summary>
    /// What the engine says the provider lacks (<c>DecisionApiKey.Missing</c>, internal to it):
    /// <c>no Liquid API key: set the environment variable LIQUID_API_KEY, or Orkeon:Liquid:ApiKey</c>. The
    /// variable is named only when it is the provider's default, else the setting that names it.
    /// </summary>
    internal static string MissingKeySentence(DecisionProviderDescriptor provider, bool defaultVariable)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var remedy = defaultVariable
            ? $"set the environment variable {provider.DefaultApiKeyVariable}, or {provider.Section}:ApiKey"
            : $"set the variable named by {provider.Section}:ApiKeyEnvVar, or {provider.Section}:ApiKey";
        return $"no {provider.DisplayName} API key: {remedy}";
    }

    /// <summary>
    /// What the host refuses in the decision profiles of the file (<c>Orkeon:Decision:Profiles</c>) while
    /// it is composed, by the engine's rules restated in <see cref="DecisionProfileRules"/>: a profile
    /// under the reserved name <c>auto</c>, with no <c>Target</c> or no stage, a stage of an unknown
    /// <c>Kind</c>, a route with no profile, a guard with no risk, a verify with no <c>EscalateTo</c>, a
    /// probability outside [0, 1], a <c>Phase</c> or <c>Steps</c> that is none, a profile that names
    /// another decision profile. Each with the path of its key, in the engine's sentence; the host
    /// stops on the first, and the following ones are what it says once that one is mended. Empty when
    /// every profile is accepted. The profiles are bound as the engine binds them: a stage with a value
    /// the binder cannot convert is left out of its profile, without a word.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> ProfileProblems() => [.. ProfileProblemsOf(ToConfiguration(_document.Root))];

    /// <summary>
    /// The keys of <c>Orkeon:Decision</c> and of each provider's section that are no setting, at any
    /// depth, as the host's start walks them (the keys of a decision profile and of its stages among
    /// them), and a probability that is no finite number; each with its path, in the engine's sentence,
    /// which may say more after it. The host says them after the rules of every section.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> UnknownKeys() => [.. WalkedKeys(ToConfiguration(_document.Root)).Select(found => (found.Path, found.Problem))];

    /// <summary>
    /// The decision profiles' problems (<see cref="ProfileProblems"/>) and the walk of the decision sections
    /// (<see cref="UnknownKeys"/>), merged as the host's start merges a walk with what came before it
    /// (<see cref="SettingsBinding.AddWalked"/>): one sentence per key, a key written without a value
    /// replacing what a rule said of the default the binder then wrote.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var configuration = ToConfiguration(_document.Root);
        var problems = ProfileProblemsOf(configuration).ToList();
        SettingsBinding.AddWalked(problems, WalkedKeys(configuration));
        return problems;
    }

    /// <summary>
    /// What the host's start refuses in the decision settings of the file alone, beyond
    /// <see cref="Problems"/>: the refusal of <see cref="Resolve"/> over no environment, at the key it
    /// names; every failure of the provider's section when that is the refusal. A refusal that the
    /// environment of a run may lift (a fail-closed judge left without a provider or without its key,
    /// which a run may find there) is not <c>Certain</c>. Empty when the file loads no refusal of its own
    /// here, or when the refusal is one of <see cref="Problems"/> or of the file's load.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem, bool Certain)> StartRefusals()
    {
        var status = Resolve(_ => null);
        if (status.Refusal is not { } refusal || status.RefusalStage is DecisionRefusalStage.None or DecisionRefusalStage.File)
            return [];
        if (Problems().Any(problem => problem.Problem == refusal))
            return [];

        switch (status.RefusalStage)
        {
            case DecisionRefusalStage.Provider when status.Provider is { } provider:
                return [.. OptionRefusals(ToConfiguration(_document.Root), provider).Select(failure => (PathNamed(failure, provider.Section), failure, true))];
            case DecisionRefusalStage.Profiles:
                var named = status.DecisionProfiles.FirstOrDefault(name => refusal.Contains($"'{name}'", StringComparison.Ordinal)) ?? LlmProfiles.Default;
                return [($"{ProfilesPath}:{named}", refusal, true)];
            case DecisionRefusalStage.Judge:
                return [(GuardianLlmOptions.SectionName, refusal, !status.Enabled)];
            default:
                return [(PathNamed(refusal, SectionPath), refusal, true)];
        }
    }

    // The key a sentence of the engine names: the path the binder quotes, else the sentence's first word
    // when it is a path of the settings, else the fallback.
    private static string PathNamed(string problem, string fallback)
    {
        const string At = "the configuration value at '";
        var at = problem.IndexOf(At, StringComparison.Ordinal);
        if (at >= 0 && problem.IndexOf('\'', at + At.Length) is var end and > 0)
            return problem[(at + At.Length)..end];
        var first = problem.Split(' ', 2)[0].TrimEnd(':');
        return first.StartsWith("Orkeon:", StringComparison.Ordinal) && first.Contains(':', StringComparison.Ordinal) ? first : fallback;
    }

    private static List<SettingsBinding.Walked> WalkedKeys(IConfiguration configuration) =>
        [
            // The runner's switch, a section of its own the host declares (a key without a value refused, #450).
            .. SettingsBinding.UnknownKeys(configuration, CrewFactoryPath, typeof(CrewFactoryShape)),
            // The readers of the section in the host's order, which the sentence lists the keys in.
            .. SettingsBinding.UnknownKeysOfReaders(
                configuration,
                SectionPath,
                (ConfigurationPath.GetSectionKey(AgentRouteOptions.SectionName), typeof(AgentRouteOptions)),
                (ConfigurationPath.GetSectionKey(AgentEffortOptions.SectionName), typeof(AgentEffortOptions)),
                (null, typeof(DecisionCircuitOptions)),
                (null, typeof(DecisionProfilesOptions))),
            .. Providers.SelectMany(provider => SettingsBinding.UnknownKeys(configuration, provider.Section, typeof(SystemOneProviderOptions))),
        ];

    private const string CrewFactoryPath = "Orkeon:CrewFactory";

    /// <summary>The keys of <c>Orkeon:CrewFactory</c> as the runner's host declares them (<c>CrewFactorySettingsShape</c>, private to it).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Read by reflection: the walk takes its properties, never an instance.")]
    private sealed class CrewFactoryShape
    {
        public bool StrictTools { get; set; } = true;
    }

    private static IEnumerable<(string Path, string Problem)> ProfileProblemsOf(IConfiguration configuration)
    {
        var unconverted = new List<(string Path, string Problem)>();
        var entries = EntryProblems(configuration);
        var profiles = SettingsBinding.Bind<DecisionProfilesOptions>(configuration, DecisionProfilesOptions.SectionName, unconverted);
        return entries.Concat(unconverted).Concat(DecisionProfileRules.Problems(profiles)).Distinct();
    }

    /// <summary>
    /// What the engine refuses in the raw entries of the decision profiles before their rules
    /// (<c>ValidateEntries</c>, Orkeon #454): a profile, its list of stages or a stage written as a
    /// single value, and a value of a profile or of a stage the binder cannot convert, which it would
    /// otherwise drop with its entry without a word. Each profile, then each of its stages, bound on
    /// its own as the engine binds them.
    /// </summary>
    private static List<(string Path, string Problem)> EntryProblems(IConfiguration configuration)
    {
        var profiles = configuration.GetSection(ProfilesPath);
        var problems = new List<(string Path, string Problem)>();
        foreach (var profile in profiles.GetChildren())
        {
            if (SingleValue(profile, "a decision profile is expected: write its Target and Stages") is { } single)
            {
                problems.Add(single);
                continue;
            }

            _ = SettingsBinding.Bind<DecisionProfileOptions>(configuration, profile.Path, problems);
            var stages = profile.GetSection(nameof(DecisionProfileOptions.Stages));
            if (SingleValue(stages, "a list of stages is expected: write each stage as an item of the list") is { } list)
            {
                problems.Add(list);
                continue;
            }

            foreach (var stage in stages.GetChildren())
            {
                if (SingleValue(stage, "a stage is expected: write its Kind and the keys of that kind") is { } one)
                    problems.Add(one);
                else
                    _ = SettingsBinding.Bind<DecisionStageOptions>(configuration, stage.Path, problems);
            }
        }

        return problems;
    }

    // A section that holds a value and no key (an empty JSON array, read as "", aside); the value is not echoed.
    private static (string Path, string Problem)? SingleValue(IConfigurationSection section, string expected) =>
        section.Value is { Length: > 0 } && !section.GetChildren().Any()
            ? (section.Path, $"{section.Path} is a single value where {expected}.")
            : null;

    /// <summary>The engine's sentence for a name a decision profile and a model profile share: the start is refused.</summary>
    internal static string SharedProfileNameRefusal(string name) => $"Two LLM profiles are named '{name}'.";

    /// <summary>
    /// The decision profiles that are fail-closed and carry a guard stage, as the engine binds them
    /// (<c>DecisionProfilesOptions</c>): <c>FailClosed</c> off when absent or unreadable, a stage's
    /// <c>Kind</c> trimmed and read in any letter case.
    /// </summary>
    private static List<string> BlockingProfilesOf(IConfiguration configuration) =>
    [
        .. SettingsBinding.Bind<DecisionProfilesOptions>(configuration, DecisionProfilesOptions.SectionName, []).Profiles
            .Where(profile => profile.Value is { FailClosed: true } options && options.Stages.Any(stage => string.Equals(
                stage?.Kind?.Trim(), DecisionProfileServiceCollectionExtensions.GuardKind, StringComparison.OrdinalIgnoreCase)))
            .Select(profile => profile.Key),
    ];

    /// <summary>
    /// The ladder of profiles and the thinking levels System One picks on, as the engine binds and
    /// validates them at start (<c>AgentRouteOptions.Problems</c>, <c>AgentEffortOptions.Problems</c>);
    /// the first thing the start refuses in them, or null. The ladder is empty when it is refused. The
    /// rungs that name no profile of these settings are listed apart: the engine refuses them when it
    /// loads a crew that asks for the pick, not at start.
    /// </summary>
    private static string? ReadPicks(IConfiguration configuration, out IReadOnlyList<string> ladder, out IReadOnlyList<string> unknownRungs)
    {
        ladder = [];
        unknownRungs = [];
        var unconverted = new List<(string Path, string Problem)>();
        var route = SettingsBinding.Bind<AgentRouteOptions>(configuration, AgentRouteOptions.SectionName, unconverted);
        var effort = SettingsBinding.Bind<AgentEffortOptions>(configuration, AgentEffortOptions.SectionName, unconverted);
        var routeProblems = unconverted.Select(problem => problem.Problem).Concat(AgentRouteOptions.Problems(route)).ToList();
        if (routeProblems.Count == 0)
        {
            ladder = [.. route.Ladder.Select(rung => rung.Trim())];
            var offered = ProfilesOf(configuration);
            unknownRungs = [.. ladder.Where(rung => !LlmProfiles.IsDefault(rung) && !offered.Contains(rung, StringComparer.OrdinalIgnoreCase))];
        }

        return routeProblems.Concat(AgentEffortOptions.Problems(effort)).FirstOrDefault();
    }

    /// <summary>
    /// The names a crew, a ladder or the compaction may put for a profile besides the default, as these
    /// settings define them: the model profiles (<c>Llm:Profiles</c>) and the decision profiles
    /// (<c>Orkeon:Decision:Profiles</c>), which the engine registers as profiles too. A run launched from
    /// Studio is offered more: Studio lays its own model profiles in the run's environment.
    /// </summary>
    public IReadOnlyList<string> ProfileNames() => ProfilesOf(ToConfiguration(_document.Root));

    /// <inheritdoc cref="ProfileNames"/>
    internal static IReadOnlyList<string> ProfilesOf(IConfiguration configuration) =>
    [
        .. configuration.GetSection(LlmProfilesSection.SectionPath).GetChildren()
            .Concat(configuration.GetSection(ProfilesPath).GetChildren())
            .Select(profile => profile.Key),
    ];

    /// <summary>
    /// Whether the host runs a fail-closed LLM judge (<c>Orkeon:Guardian:Llm</c> enabled, its
    /// <c>FailMode</c> left on <c>Closed</c>, the guardian itself on), as the engine binds the guardian's
    /// section, the judge's within it; returns the binder's refusal of the first value it cannot
    /// convert, else the first rule of the section a value breaks (<see cref="GuardianSection.Problems"/>),
    /// or null. A key without a value (a JSON null, an empty object) is the binder's
    /// <c>false</c>, as for every switch it binds: the guardian written so is off.
    /// </summary>
    private static string? ReadClosedJudge(IConfiguration configuration, out bool closed)
    {
        var unconverted = new List<(string Path, string Problem)>();
        var guardian = GuardianSection.Read(configuration, unconverted, out var refused);
        closed = unconverted.Count == 0 && guardian is { Enabled: true, Llm: { Enabled: true, FailMode: GuardianJudgeFailMode.Closed } };
        return unconverted.Concat(refused).Select(problem => problem.Problem).FirstOrDefault();
    }

    /// <summary>
    /// The circuit's switch as the engine binds it (<c>DecisionCircuitOptions</c>): on when the key is
    /// absent, off when it is written without a value (a JSON null, an empty object: the binder gives a
    /// boolean its <c>false</c>), else what <c>bool.Parse</c> reads. Returns the binder's refusal of any
    /// other text, the empty one of an empty list or an empty string included, the switch then left on.
    /// </summary>
    private static string? ReadCircuitSwitch(IConfiguration configuration, out bool enabled)
    {
        var unconverted = new List<(string Path, string Problem)>();
        enabled = SettingsBinding.Bind<DecisionCircuitOptions>(configuration, DecisionCircuitOptions.SectionName, unconverted).Enabled;
        return unconverted.Count > 0 ? unconverted[0].Problem : null;
    }

    /// <summary>
    /// The runner's strict tool resolution as the host reads it, one value with a default
    /// (<c>GetValue</c>): on when the key is absent or holds no value, else what <c>bool.Parse</c> reads.
    /// Returns the refusal of any other text, the switch then left on.
    /// </summary>
    private static string? ReadStrictTools(IConfiguration configuration, out bool strictTools)
    {
        try
        {
            strictTools = configuration.GetValue(StrictToolsPath, defaultValue: true);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            strictTools = true;
            return SettingsBinding.WithoutValue(ex);
        }
    }

    /// <summary>
    /// What the host's start refuses in the section of the provider in use, as the engine binds and
    /// validates it (<c>AddOrkeonDecisionProvider</c>, validated at start): a value the binder cannot
    /// convert, a timeout under one second, retries under zero, an API root that is no absolute URL.
    /// The sections left aside are bound by nobody. Each failure is one refusal of the host
    /// (<c>OptionsValidationException.Failures</c>, listed one by one), so the start raises the first;
    /// empty when the section is accepted.
    /// </summary>
    private static List<string> OptionRefusals(IConfiguration configuration, DecisionProviderDescriptor provider)
    {
        var options = new SystemOneProviderOptions();
        provider.ApplyDefaults(options);
        try
        {
            configuration.GetSection(provider.Section).Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return [SettingsBinding.WithoutValue(ex)];
        }

        // FillBlanks, for the one default the rules below read.
        if (options.BaseUrl is null || string.IsNullOrWhiteSpace(options.BaseUrl.OriginalString))
            options.BaseUrl = provider.DefaultBaseUrl;

        // The engine's own wording, each failure apart, in the validator's order.
        var failures = new List<string>();
        if (options.TimeoutSeconds <= 0)
            failures.Add($"{provider.Section}:TimeoutSeconds: one second or more.");
        if (options.MaxRetries < 0)
            failures.Add($"{provider.Section}:MaxRetries: zero or more.");
        if (!options.BaseUrl.IsAbsoluteUri)
            failures.Add($"{provider.Section}:BaseUrl: an absolute URL.");
        return failures;
    }

    /// <summary>
    /// What a provider that needs a key lacks, when no key is in place in the file (<c>ApiKey</c>) nor in
    /// the variable it reads (<c>ApiKeyEnvVar</c>, the provider's own by default): the engine then turns
    /// the circuit off (<c>no-key</c>, Orkeon #451). As the engine names it (<c>DecisionApiKey.Remedy</c>,
    /// Orkeon #467): the variable when it is the provider's default (unset reads as that default), else
    /// the setting that names it, never the value written there, which may be a key pasted by mistake.
    /// Null when the key is in place or optional.
    /// </summary>
    private static (string? Variable, string? Setting)? MissingKey(
        IConfiguration configuration, DecisionProviderDescriptor provider, Func<string, string?> environment)
    {
        if (!provider.KeyRequired || !string.IsNullOrWhiteSpace(configuration[$"{provider.Section}:ApiKey"]))
            return null;

        var written = configuration[$"{provider.Section}:ApiKeyEnvVar"];
        var variable = string.IsNullOrWhiteSpace(written) ? provider.DefaultApiKeyVariable : written.Trim();
        if (!string.IsNullOrWhiteSpace(environment(variable)))
            return null;

        return string.Equals(variable, provider.DefaultApiKeyVariable, StringComparison.Ordinal)
            ? (variable, null)
            : (null, ApiKeyEnvVarSetting(provider));
    }

    /// <summary>The setting that names the variable a provider reads its key from: <c>Orkeon:Liquid:ApiKeyEnvVar</c>.</summary>
    public static string ApiKeyEnvVarSetting(DecisionProviderDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return $"{provider.Section}:ApiKeyEnvVar";
    }

    /// <summary>
    /// The document as the configuration a run binds, as the JSON provider flattens it
    /// (<see cref="SettingsBinding.Flatten"/>): an empty object is a key without a value, so a section
    /// written empty still counts as present, and an empty array is an empty text. A file the provider
    /// refuses to load (<see cref="AppSettingsDocument.LoadRefusal"/>) is flattened all the same.
    /// With <paramref name="environment"/>, the settings of the decision circuit a run also reads from
    /// the environment are laid as the runner lays them: the variables without a prefix under the
    /// file, the <c>ORKEON_</c> ones above it.
    /// </summary>
    internal static IConfiguration ToConfiguration(JsonObject root, Func<string, string?>? environment = null)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (environment is not null)
            LayEnvironment(environment, prefix: "", data);
        foreach (var (key, value) in SettingsBinding.Flatten(root, out _))
            data[key] = value;
        if (environment is not null)
            LayEnvironment(environment, EnvironmentPrefix, data);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    /// <summary>The settings a run of the decision circuit reads, as an environment variable names each (<c>:</c> written <c>__</c>).</summary>
    internal static IEnumerable<string> EnvironmentSettings()
    {
        yield return EnabledPath;
        yield return ProviderPath;
        yield return StrictToolsPath;
        yield return GuardianEnabledPath;
        yield return GuardianLlmOptions.SectionName + ":Enabled";
        yield return GuardianLlmOptions.SectionName + ":FailMode";
        // A list is one variable per item: the first rungs and levels a variable could give.
        for (var rung = 0; rung < MaxProbedRungs; rung++)
            yield return $"{RouteLadderPath}:{rung}";
        for (var level = 0; level < KnownEffortLevels.Count; level++)
            yield return $"{EffortLevelsPath}:{level}";
        foreach (var provider in Providers)
        {
            foreach (var field in ProviderFields)
                yield return $"{provider.Section}:{field}";
        }
    }

    private static void LayEnvironment(Func<string, string?> environment, string prefix, Dictionary<string, string?> data)
    {
        foreach (var setting in EnvironmentSettings())
        {
            if (environment(prefix + setting.Replace(":", "__", StringComparison.Ordinal)) is { } value)
                data[setting] = value;
        }
    }

}

/// <summary>The decision circuit a run of the settings gets, as the engine resolves it.</summary>
/// <param name="Enabled">The <c>Orkeon:Decision:Enabled</c> switch, on by default.</param>
/// <param name="Provider">The provider the run uses, or null when none is configured (or the choice is refused).</param>
/// <param name="How">Whether the provider was named or deduced.</param>
/// <param name="IgnoredSections">The provider sections present but not used.</param>
/// <param name="Refusal">
/// The engine's message when it refuses the settings at start (a file that does not load for a key
/// written twice, an unknown name, two providers that could be deduced, a Kev address the key may not
/// travel to, a decision profile it refuses (<see cref="DecisionSection.ProfileProblems"/>), a ladder or
/// levels it refuses, a switch or a value of the guardian that does not convert or breaks a rule of
/// its section, a section of the provider in use its validation refuses, a decision profile under the
/// reserved name <c>default</c> or a name it shares with a model profile, a fail-closed judge left
/// without a circuit, the provider's missing key included); null
/// otherwise.
/// One sentence: the first the host meets among those (<see cref="DecisionStatus.RefusalStage"/>).
/// Under a refusal the other members still hold what the file tells, each as it says.
/// </param>
public sealed record DecisionStatus(
    bool Enabled,
    DecisionProviderDescriptor? Provider,
    DecisionProviderSelection How,
    IReadOnlyList<string> IgnoredSections,
    string? Refusal)
{
    /// <summary>
    /// Whether a run asks the decision model as far as the settings go: the switch is on, a provider is
    /// chosen and the start refuses nothing. The provider's key is aside (<see cref="KeyMissing"/>): the
    /// engine turns the circuit off without it, which a consumer says as a warning, the key being one a
    /// launch may still give; a fail-closed judge refuses the start for it (<see cref="DecisionRefusalStage.Judge"/>).
    /// </summary>
    public bool CircuitOn => Enabled && Provider is not null && Refusal is null;

    /// <summary>
    /// Where the host meets <see cref="Refusal"/>, among everything its start judges:
    /// <see cref="DecisionRefusalStage.None"/> when it refuses nothing here.
    /// </summary>
    public DecisionRefusalStage RefusalStage { get; init; }

    /// <summary>
    /// The variable the chosen provider reads its key from, when it needs one and none is in place there
    /// nor in the file, and that variable is the provider's default (<c>LIQUID_API_KEY</c>): the engine
    /// then turns the circuit off (<c>no-key</c>, Orkeon #451). Null when the key is in place, optional,
    /// no provider is chosen, or <c>ApiKeyEnvVar</c> names another variable
    /// (<see cref="MissingKeyVariableSetting"/>): as the engine does (Orkeon #467), Studio never repeats
    /// a value written there, which may be a key pasted by mistake.
    /// </summary>
    public string? MissingKeyVariable { get; init; }

    /// <summary>
    /// The setting that names the variable the chosen provider reads its key from
    /// (<c>Orkeon:Liquid:ApiKeyEnvVar</c>), when the key is missing and that variable is not the
    /// provider's default: what Studio names in its place. Null otherwise.
    /// </summary>
    public string? MissingKeyVariableSetting { get; init; }

    /// <summary>
    /// Whether the chosen provider needs a key and none is in place (<see cref="MissingKeyVariable"/> or
    /// <see cref="MissingKeyVariableSetting"/>): the engine turns the circuit off for it (<c>no-key</c>).
    /// </summary>
    public bool KeyMissing => MissingKeyVariable is not null || MissingKeyVariableSetting is not null;

    /// <summary>
    /// Whether the runner resolves tools strictly (<c>Orkeon:CrewFactory:StrictTools</c>, on by default):
    /// on, a crew that states no mode and holds <c>system_one_decide</c> is refused at load once the
    /// circuit is off (a check loads it and only warns); off, the tool is dropped and the crew runs
    /// without it.
    /// </summary>
    public bool StrictTools { get; init; } = true;

    /// <summary>
    /// The ladder System One picks a profile on (<c>Orkeon:Decision:Route:Ladder</c>), as the run reads
    /// it; empty when the settings give none, and an agent on <c>llm.profile: auto</c> then fails to load.
    /// Empty too when the start refuses the ladder itself; under any other refusal, the ladder as read.
    /// </summary>
    public IReadOnlyList<string> RouteLadder { get; init; } = [];

    /// <summary>
    /// The rungs of <see cref="RouteLadder"/> that name no profile of these settings. The engine refuses
    /// a rung it is not offered when it loads a crew with an agent on <c>llm.profile: auto</c>; a run
    /// launched from Studio is offered Studio's own model profiles besides, through its environment, so
    /// such a rung is warned of and never blocks.
    /// </summary>
    public IReadOnlyList<string> UnknownRungs { get; init; } = [];

    /// <summary>
    /// The names of the decision profiles the settings define (<c>Orkeon:Decision:Profiles</c>): an agent
    /// or a task that names one asks System One through the profile's stages. Held under a refusal too,
    /// whatever it is: the names are read before anything is judged.
    /// </summary>
    public IReadOnlyList<string> DecisionProfiles { get; init; } = [];

    /// <summary>
    /// The decision profiles of <c>Orkeon:Decision:Profiles</c> that block every call once the circuit is
    /// off: those that are fail-closed (<c>FailClosed: true</c>) and carry a guard stage (a <c>Kind</c>
    /// that reads <c>guard</c>, trimmed, in any letter case), as the engine builds them without decisions
    /// (<c>CircuitOffGuardStage</c>). Empty when none is; a <c>FailClosed</c> that reads as no boolean
    /// counts as off, and a guard stage the binder leaves out for a value it cannot convert is no stage.
    /// Held under a refusal too, whatever it is.
    /// </summary>
    public IReadOnlyList<string> BlockingProfiles { get; init; } = [];

    /// <summary>
    /// Whether the settings give a ladder at all: two rungs or more (a single one already refuses the
    /// start). Without one the engine refuses, at load, a crew with an agent or a task routed on
    /// <c>llm.profile: auto</c>.
    /// </summary>
    public bool HasRouteLadder => RouteLadder.Count >= 2;

    /// <summary>Whether the ladder serves such a crew for sure: it is there, and each rung is a profile of these settings.</summary>
    public bool ServesRouteAuto => HasRouteLadder && UnknownRungs.Count == 0;

    /// <summary>
    /// Whether the host runs a fail-closed LLM judge (<c>Orkeon:Guardian:Llm</c>): it refuses to start
    /// once the circuit is off, a run's own <c>--no-decision</c> included
    /// (<see cref="RefusalOfRunSwitch"/>). Held under a refusal too, as the file tells it; false when
    /// the guardian's own settings are what does not convert (<c>Orkeon:Guardian:Enabled</c>, a value of
    /// <c>Orkeon:Guardian:Llm</c>).
    /// </summary>
    public bool ClosedJudge { get; init; }

    /// <summary>
    /// What the host answers a run that switches the circuit off itself (<c>--no-decision</c>) while a
    /// fail-closed LLM judge is configured; null when it starts.
    /// </summary>
    public string? RefusalOfRunSwitch => ClosedJudge
        ? GuardianLlmOptions.CircuitRefusal(DecisionCircuit.OffBy(DecisionCircuitCause.Switch).OffReason!)
        : null;
}

/// <summary>
/// Where the host's start meets a refusal of the decision settings, in the order it judges everything:
/// a later stage is reached only once the earlier ones, and the sections judged between them, pass.
/// </summary>
public enum DecisionRefusalStage
{
    /// <summary>Nothing is refused.</summary>
    None,

    /// <summary>The configuration does not load the file (<see cref="AppSettingsDocument.LoadRefusal"/>).</summary>
    File,

    /// <summary>
    /// Refused while the host is composed, before any section is judged: <c>Orkeon:CrewFactory:StrictTools</c>
    /// that is no boolean, the choice of a provider, a decision profile the engine's rules refuse (a
    /// reserved name, no target, no stage, a stage it cannot build).
    /// </summary>
    Composition,

    /// <summary>The ladder or the thinking levels: judged after the compaction and the skills.</summary>
    Picks,

    /// <summary>
    /// A value of the guardian that does not convert or breaks a rule of its section, or the circuit's
    /// switch that does not convert: judged before the tool bridge.
    /// </summary>
    Switches,

    /// <summary>The section of the provider in use: the last section judged, before the keys no section carries.</summary>
    Provider,

    /// <summary>A decision profile named <c>default</c>, or a name two profiles share: said after the keys no section carries.</summary>
    Profiles,

    /// <summary>A fail-closed LLM judge left without a circuit (switched off, no provider, the provider's key missing): said last.</summary>
    Judge,
}

/// <summary>
/// The section of one decision provider (<c>Orkeon:TypeSafe</c>, <c>Orkeon:Liquid</c>, <c>Orkeon:Kev</c>),
/// as <c>SystemOneProviderOptions</c> binds it. A blank value removes its key: the engine then gives the
/// provider's default (<c>DecisionProviderDescriptor.FillBlanks</c>).
/// </summary>
public sealed class DecisionProviderSettings
{
    private readonly AppSettingsDocument _document;

    internal DecisionProviderSettings(AppSettingsDocument document, DecisionProviderDescriptor descriptor)
    {
        _document = document;
        Descriptor = descriptor;
    }

    /// <summary>The provider, as the engine describes it.</summary>
    public DecisionProviderDescriptor Descriptor { get; }

    /// <summary>The section's path, as the engine spells it.</summary>
    public string SectionPath => Descriptor.Section;

    /// <summary>The path of a field, as the file spells the keys it holds (<see cref="AppSettingsDocument.PathAsWritten"/>).</summary>
    private string Field(string name) => _document.PathAsWritten($"{SectionPath}:{name}");

    /// <summary>Whether the section is written, even empty or null: what the engine deduces a provider from.</summary>
    public bool Exists => DecisionProviderChoice.HasSection(DecisionSection.ToConfiguration(_document.Root), SectionPath);

    /// <summary>The API root; null gives the provider's (<see cref="DecisionProviderDescriptor.DefaultBaseUrl"/>).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056",
        Justification = "The JSON field as typed: an in-progress or malformed URL must survive an edit; the engine judges it at start.")]
    public string? BaseUrl
    {
        get => _document.GetString(Field("BaseUrl"));
        set => _document.SetString(Field("BaseUrl"), Blank(value));
    }

    /// <summary>The model; null gives the provider's (<see cref="DecisionProviderDescriptor.DefaultModel"/>).</summary>
    public string? Model
    {
        get => _document.GetString(Field("Model"));
        set => _document.SetString(Field("Model"), Blank(value));
    }

    /// <summary>The variable the key is read from; null gives the provider's (<see cref="DecisionProviderDescriptor.DefaultApiKeyVariable"/>).</summary>
    public string? ApiKeyEnvVar
    {
        get => _document.GetString(Field("ApiKeyEnvVar"));
        set => _document.SetString(Field("ApiKeyEnvVar"), Blank(value));
    }

    /// <summary>The variable a run reads the key from: the one written, trimmed, else the provider's.</summary>
    public string EffectiveApiKeyEnvVar =>
        ApiKeyEnvVar is { } written && !string.IsNullOrWhiteSpace(written) ? written.Trim() : Descriptor.DefaultApiKeyVariable;

    /// <summary>Whether a key is written in the file itself (<c>ApiKey</c>): Studio never writes one.</summary>
    public bool HasApiKeyInFile => !string.IsNullOrEmpty(_document.GetString(Field("ApiKey")));

    /// <summary>The time a request may take, in seconds; null gives <see cref="DecisionSection.DefaultTimeoutSeconds"/>.</summary>
    public int? TimeoutSeconds
    {
        get => _document.GetInt32(Field("TimeoutSeconds"));
        set => _document.SetInt32(Field("TimeoutSeconds"), value);
    }

    /// <summary>The retries of a failed request; null gives <see cref="DecisionSection.DefaultMaxRetries"/>.</summary>
    public int? MaxRetries
    {
        get => _document.GetInt32(Field("MaxRetries"));
        set => _document.SetInt32(Field("MaxRetries"), value);
    }

    /// <summary>Writes the section, empty when nothing is set: an empty section still makes the provider deducible.</summary>
    public void Create()
    {
        var path = _document.PathAsWritten(SectionPath);
        if (_document.GetNode(path) is not JsonObject)
            _document.SetNode(path, new JsonObject());
    }

    /// <summary>Removes the whole section, under every letter case the file spells it in.</summary>
    public void Remove()
    {
        var parentPath = ConfigurationPath.GetParentPath(SectionPath)!;
        var leaf = ConfigurationPath.GetSectionKey(SectionPath);
        if (_document.GetNode(_document.PathAsWritten(parentPath)) is not JsonObject parent)
            return;

        foreach (var key in parent.Select(property => property.Key)
                     .Where(key => string.Equals(key, leaf, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            parent.Remove(key);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
