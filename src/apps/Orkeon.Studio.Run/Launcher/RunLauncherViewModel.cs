using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.FileSystem;
using Orkeon.Studio.Core.History;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Targets;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Run.Launcher;

/// <summary>
/// Everything the launcher screen renders, and nothing it draws.
/// <para>
/// It composes the three pieces of a launch — which crew
/// (<see cref="TargetSelectionModel"/>), with which options
/// (<see cref="LaunchOptionsModel"/>), spawned how (<see cref="RunSession"/>) — and answers
/// the questions the screen asks between them: which command line this adds up to, what a
/// dry run would say, which mounts the appsettings file already declares, and what a replay
/// from the recent list would run.
/// </para>
/// </summary>
internal sealed class RunLauncherViewModel
{
    private readonly IAppSettingsReader _settingsReader;
    private readonly ITargetProbe _targetProbe;
    private readonly IDirectoryProbe _directories;
    private readonly Func<string, string?> _environment;
    private readonly Func<IEnumerable<string>> _environmentNames;

    /// <summary>
    /// Creates the launcher over explicit collaborators (the test seam). <paramref name="teamsRoot"/>
    /// is the teams root a real run stamps its team's last run under (STUDIO-31, D-05; STUDIO-61)
    /// and the root a team's own settings file is anchored on (STUDIO-62); null stamps nothing and
    /// finds no team file. <paramref name="targetProbe"/> is the disk the team file is looked up on
    /// — the detector's, in production; the real disk when absent. <paramref name="environment"/> is
    /// where the decision providers' keys are read, the process environment the child inherits when absent;
    /// <paramref name="environmentNames"/> lists the names of its variables, for the settings a run reads
    /// under names this launcher cannot spell in advance (a model profile laid in any letter case): the
    /// process environment's when both are absent, none when only <paramref name="environment"/> is given.
    /// </summary>
    public RunLauncherViewModel(
        RunTargetDetector detector,
        OrkeonProcessRunner runner,
        ILaunchHistoryStore? history = null,
        MountValidator? mountValidator = null,
        IAppSettingsReader? settingsReader = null,
        TeamsRootResolution? teamsRoot = null,
        ITargetProbe? targetProbe = null,
        Func<string, string?>? environment = null,
        Func<IEnumerable<string>>? environmentNames = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(runner);

        Target = new TargetSelectionModel(detector);
        Options = new LaunchOptionsModel(mountValidator);
        TeamsRoot = teamsRoot;
        Session = new RunSession(runner, history, teamsRoot?.Path);
        _settingsReader = settingsReader ?? PhysicalAppSettingsReader.Instance;
        _targetProbe = targetProbe ?? PhysicalTargetProbe.Instance;
        _directories = new TargetProbeDirectories(_targetProbe);
        _environmentNames = environmentNames ?? (environment is null ? LaunchLlmProfiles.ProcessEnvironmentNames : () => []);
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// Creates the launcher over the real machine: real disk, real processes, real history file —
    /// and the teams root the <c>ORKEON_STUDIO_TEAMS_ROOT</c> variable names (STUDIO-61), read
    /// through <paramref name="environment"/>, the process environment unless a test hands one.
    /// The TUI parses no startup option and has no preferences file: the variable alone, else the
    /// default. A run from here then stamps its team's last run as the WPF Launch tab does.
    /// </summary>
    public static RunLauncherViewModel ForCurrentMachine(Func<string, string?>? environment = null) =>
        new(
            new RunTargetDetector(PhysicalTargetProbe.Instance),
            OrkeonProcessRunner.ForCurrentMachine(),
            TryCreateHistoryStore(),
            teamsRoot: TeamsRootLocator.Resolve(environment),
            targetProbe: PhysicalTargetProbe.Instance);

    /// <summary>
    /// The teams root in force and where it came from (STUDIO-61): the variable or the default
    /// for <see cref="ForCurrentMachine"/>; null when the launcher was built without one, and a
    /// run then stamps no team.
    /// </summary>
    public TeamsRootResolution? TeamsRoot { get; }

    /// <summary>The target picker.</summary>
    public TargetSelectionModel Target { get; }

    /// <summary>The options form.</summary>
    public LaunchOptionsModel Options { get; }

    /// <summary>The run lifecycle.</summary>
    public RunSession Session { get; }

    /// <summary>
    /// Working directory of the child process. Null follows the target: the directory holding
    /// the crew, which is also where the CLI's settings resolution starts looking.
    /// </summary>
    public string? WorkingDirectoryOverride { get; set; }

    /// <summary>Mounts already declared by the settings file the run will read, shown read-only.</summary>
    public IReadOnlyList<string> SettingsMounts { get; private set; } = [];

    /// <summary>
    /// The settings file the run will read: the pin, else the team's own file (STUDIO-62), else
    /// null — the CLI's own chain. The team file is looked up again for the current target first.
    /// </summary>
    public string? ResolvedSettingsPath
    {
        get
        {
            SyncTeamSettingsPath();
            return Options.ResolvedSettingsPath;
        }
    }

    /// <summary>
    /// The settings label of the form: « Settings: auto », or « Settings: auto (team file: &lt;path&gt;) »
    /// when the workshop's file for the selected team will be read (STUDIO-62) — nothing is pinned
    /// and the team sits right under the teams root with its <c>settings/&lt;slug&gt;/appsettings.json</c>.
    /// </summary>
    public string DescribeSettings()
    {
        SyncTeamSettingsPath();
        return Options.UseAutomaticSettings && Options.TeamSettingsPath is { Length: > 0 } teamFile
            ? $"Settings: auto (team file: {teamFile})"
            : "Settings: auto";
    }

    /// <summary>Why <see cref="SettingsMounts"/> is empty, when it is empty for a reason.</summary>
    public string? SettingsMountsNotice { get; private set; }

    /// <summary>Whether <see cref="SettingsMounts"/> is the list of a settings file read here, not a blank for an unknown one.</summary>
    private bool _settingsMountsKnown;

    /// <summary>The decision circuit of the settings file read here; null while the CLI resolves the file.</summary>
    private DecisionStatus? _settingsDecision;
    private ToolBridgePreflightFinding? _settingsBridge;

    /// <summary>
    /// The engine's refusal of an <c>Orkeon:Compaction:Profile</c> the settings file read here does not
    /// define and the environment does not lay; null when there is none.
    /// </summary>
    private (string Path, string Problem)? _settingsCompactionProfile;

    /// <summary>
    /// The <c>Provider</c> refusals of the <c>Llm</c> section and its profiles in the settings file read
    /// here (<see cref="LlmProviderRules"/>): the host's start refuses each; empty while the CLI resolves
    /// the file.
    /// </summary>
    private IReadOnlyList<ValidationMessage> _settingsLlmProvider = [];

    /// <summary>
    /// The rungs of the ladder and the decision profiles of the settings file read here that the
    /// environment of this launcher lays as LLM profiles (the spawned CLI inherits them): the only
    /// profiles a run gets besides the file's, this launcher laying none itself. A rung is then served;
    /// a decision profile then shares its name, which the engine refuses.
    /// </summary>
    private IReadOnlyList<string> _profilesLaidInEnvironment = [];

    /// <summary>Options the form leaves enabled for the detected target.</summary>
    public IReadOnlyList<RunOption> AvailableOptions => LaunchOptionsModel.AvailableOptions(Target.Target);

    /// <summary>True when <paramref name="option"/> applies to the detected target.</summary>
    public bool IsOptionAvailable(RunOption option) => LaunchOptionsModel.IsAvailable(Target.Target, option);

    /// <summary>A launch can be prepared: a target was resolved and no run is in flight.</summary>
    public bool CanLaunch => Target.IsResolved && !Session.IsRunning;

    /// <summary>The directory the child process will run in.</summary>
    public string? EffectiveWorkingDirectory =>
        !string.IsNullOrWhiteSpace(WorkingDirectoryOverride)
            ? WorkingDirectoryOverride
            : DirectoryOf(Target.Target);

    /// <summary>Builds the argument list for the current form.</summary>
    /// <param name="dryRun">True to add <c>--validate</c>.</param>
    /// <exception cref="InvalidOperationException">No target is resolved, or an option is invalid.</exception>
    public IReadOnlyList<string> BuildArguments(bool dryRun = false)
    {
        var target = RequireTarget();
        SyncTeamSettingsPath();
        return RunArgumentsBuilder.Build(target, Options.ToLaunchOptions(target, dryRun));
    }

    /// <summary>
    /// The command line equivalent to what will be spawned, for the "this is what runs" field.
    /// Returns the reason instead when the form does not yet add up to one.
    /// </summary>
    public string DescribeCommandLine(bool dryRun = false)
    {
        if (Target.Target is null)
            return "Select a crew to see the command line.";

        try
        {
            return CommandLineDisplay.Format(BuildArguments(dryRun));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Everything a UI should show before launching: the option/target mismatches and
    /// malformed values from Core, plus the launch mounts' own findings.
    /// </summary>
    public IReadOnlyList<ValidationMessage> Validate(bool dryRun = false)
    {
        var messages = new List<ValidationMessage>();
        SyncTeamSettingsPath();

        if (Target.Target is { } target)
        {
            messages.AddRange(RunArgumentsBuilder.Validate(target, Options.ToLaunchOptions(target, dryRun)));

            // The detector's notices — the root folders a crew/ sub-folder set aside
            // (STUDIO-59) — read as information, like the directory-run advice.
            foreach (var notice in Target.Detection?.Notices ?? [])
                messages.Add(ValidationMessage.Information(notice.Code, notice.Text, target.RunPath));
        }

        messages.AddRange(Options.ValidateMounts());
        messages.AddRange(ValidateFileSystemScopes(dryRun));
        messages.AddRange(ValidateDeliverableWriters());
        messages.AddRange(ValidateDecisionModels(dryRun));
        messages.AddRange(ValidateToolBridge());
        foreach (var refused in _settingsLlmProvider)
        {
            // The host refuses the start on it, a check included.
            messages.Add(ValidationMessage.Error(
                refused.Code, "The model settings stop the run at start: " + refused.Text, refused.Path));
        }

        if (_settingsCompactionProfile is { } unknown)
        {
            // Nothing lays the profile for this run: the host refuses to start, a check included.
            messages.Add(ValidationMessage.Error(
                ValidationCodes.EngineSettingRefused,
                "The compaction of the settings stops the run at start: " + unknown.Problem,
                unknown.Path));
        }

        return messages;
    }

    /// <summary>
    /// What the tool bridge of the settings file read here does to the run (<see cref="ToolBridgePreflight"/>):
    /// an error when the host would refuse to start, a check included (a value of one of the bridge's
    /// two sections it refuses, or tools to bridge with no token in its environment variable);
    /// information when an address is named and no tool allowed. Nothing while the CLI resolves the
    /// settings itself.
    /// </summary>
    private List<ValidationMessage> ValidateToolBridge() => _settingsBridge switch
    {
        null => [],
        { Kind: ToolBridgePreflightKind.SettingsRefused } refused => [ValidationMessage.Error(
            ValidationCodes.EngineSettingRefused,
            "The tool bridge of the settings stops the run at start: " + refused.Reason,
            refused.Path)],
        { Kind: ToolBridgePreflightKind.TokenRefused } token => [ValidationMessage.Error(
            ValidationCodes.BridgeToken,
            "The tool bridge refuses the start: " + token.Reason,
            ToolBridgeSection.SectionPath)],
        var idle => [ValidationMessage.Information(
            ValidationCodes.BridgeIdle,
            $"The settings name a tool bridge ({idle.Reason}) and allow no tool on it: nothing is bridged, every tool runs on this machine.",
            ToolBridgeSection.SectionPath)],
    };

    /// <summary>
    /// What the decision models (System One) change to the run (<see cref="DecisionPreflight"/>), the
    /// crew read again. An error when the launch is refused for sure: a crew that states
    /// <c>decision.mode: required</c>, a holder of <c>system_one_decide</c> under strict tools, or a
    /// decision task with neither a fallback nor an <c>otherwise</c> task, on a run with the circuit off;
    /// a crew that states <c>decision.mode: none</c> and is routed on <c>llm.profile: auto</c>; and, with
    /// the settings file read here, a provider the engine refuses or a decision profile that bears the
    /// name of a model profile the environment lays (a check too), a crew routed on
    /// <c>llm.profile: auto</c> the ladder does not serve (none, or a rung that names a profile neither
    /// the file defines nor the environment lays: this launcher lays none), a decision profile named
    /// under the mode none, and a fail-closed guarded profile on a run with the circuit off. A warning
    /// while the CLI resolves the settings itself, for a check of what only a run refuses (the check
    /// stays offered: it loads the crew, and reports the engine's line when the load fails), for a
    /// provider without its key, and for a holder of the tool a lenient runner withholds; information
    /// for a crew that goes on its relays, its fallbacks and what the settings state.
    /// </summary>
    private List<ValidationMessage> ValidateDecisionModels(bool dryRun)
    {
        if (Target.Target is not { } target
            || DecisionPreflight.Check(
                CrewDecisionUsage.Of(target), _settingsDecision, _settingsDecision is not null, Options.NoDecision, _profilesLaidInEnvironment)
                is not { } finding)
        {
            return [];
        }

        var holders = Quoted(finding.Usage.ToolHolders);
        var reasons = string.Concat(finding.Reasons.Select(reason => " " + ReasonText(reason, finding.Status)));
        var blocks = dryRun ? finding.BlocksValidate : finding.BlocksLaunch;
        return finding.Kind switch
        {
            DecisionPreflightKind.ProviderRefused => [Say(
                ValidationCodes.DecisionProviderRefused,
                blocks,
                $"The engine refuses the decision settings, so the CLI will not start: {finding.Status?.Refusal}")],
            DecisionPreflightKind.ProfileRefusedByMode => [Say(
                ValidationCodes.DecisionModeRefused,
                blocks,
                ModeRefusalText(finding)
                + ", and the crew states decision.mode: none, which refuses any use of System One: "
                + "the engine refuses the crew when it loads it.")],
            DecisionPreflightKind.RouteWithoutLadder => [Say(
                ValidationCodes.DecisionLadder,
                blocks,
                Quoted(finding.RoutedHolders)
                + " run on llm.profile: auto, and "
                + (finding.UnknownRungs.Count > 0
                    ? $"Orkeon:Decision:Route:Ladder names {Quoted(finding.UnknownRungs)}, a profile neither the settings define "
                      + "(Llm:Profiles) nor the environment lays: the engine refuses the crew when it loads it."
                    : "the settings give no ladder of two profiles or more in Orkeon:Decision:Route:Ladder: the engine refuses the crew when it loads it."))],
            DecisionPreflightKind.RequiredWithoutModel => [Say(
                ValidationCodes.DecisionRequired,
                blocks,
                (dryRun ? CheckOfRequiredText(finding) + "this crew" : "This crew")
                + $" cannot run without the decision models (System One): {NeedsText(finding)}." + reasons)],
            DecisionPreflightKind.ProfileBlockedWithoutModel => [Say(
                ValidationCodes.DecisionProfileBlocked,
                blocks,
                (dryRun ? "A check loads this crew, but a run will fail: " : "")
                + Quoted(finding.BlockedProfileHolders)
                + " run on a fail-closed decision profile with a guard stage, and this run goes without the decision models "
                + "(System One): the guard cannot judge, so the engine blocks every call made through the profile." + reasons)],
            DecisionPreflightKind.RequiredUnknownSettings => [Say(
                ValidationCodes.DecisionRequired,
                blocks,
                $"This crew cannot run without the decision models (System One): {NeedsText(finding)}." + reasons
                + " The CLI resolves the settings at launch: check that they configure a decision provider and leave the circuit on, or pin the file.")],
            DecisionPreflightKind.ProviderKeyMissing => [ValidationMessage.Warning(
                ValidationCodes.DecisionKeyMissing,
                $"The decision provider {finding.Status?.Provider?.DisplayName} needs an API key and none is in place: "
                + $"set {KeyVariableText(finding.Status)} (or ApiKey in its section). Until then the engine turns the decision models off "
                + "and sends it no request: the crew runs without them, each decision task taking its relay's answer or its fallback, "
                + "or letting its otherwise task run.",
                target.RunPath)],
            DecisionPreflightKind.ToolWithheld => [ValidationMessage.Warning(
                ValidationCodes.DecisionToolDropped,
                $"{holders} use system_one_decide, and this run goes without the decision models: with "
                + "Orkeon:CrewFactory:StrictTools false, the engine withholds the tool and runs the crew without it." + reasons,
                target.RunPath)],
            _ => [ValidationMessage.Information(
                ValidationCodes.DecisionFallback,
                "This run goes without the decision models (System One): "
                + string.Join("; ", finding.Usage.DecisionTasks.Where(task => task.AnswersWithoutModel).Select(FallbackText)
                    .Concat(finding.Usage.OtherUses.Select(PickText)))
                + "." + reasons,
                target.RunPath)],
        };

        ValidationMessage Say(string code, bool error, string text) =>
            error ? ValidationMessage.Error(code, text, target.RunPath) : ValidationMessage.Warning(code, text, target.RunPath);
    }

    /// <summary>The names of agents, tasks or profiles, each quoted, comma-separated.</summary>
    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"'{name}'"));

    /// <summary>
    /// What a crew that states <c>decision.mode: none</c> asks System One all the same: a profile to
    /// pick, a decision profile of the settings, or both.
    /// </summary>
    private static string ModeRefusalText(DecisionPreflightFinding finding)
    {
        var asks = new List<string>();
        if (finding.RoutedHolders.Count > 0)
            asks.Add(Quoted(finding.RoutedHolders) + " run on llm.profile: auto, which System One picks");
        if (finding.DecisionProfileHolders.Count > 0)
            asks.Add(Quoted(finding.DecisionProfileHolders) + " name a decision profile");
        return string.Join("; ", asks);
    }

    /// <summary>
    /// What a check of a crew a run refuses does: it stays offered, loads the crew and reports what the
    /// run would refuse as a warning.
    /// </summary>
    private static string CheckOfRequiredText(DecisionPreflightFinding finding) =>
        finding.RefusedByMode
            ? "A check loads this crew and warns, but a run will refuse it (exit code 69): "
            : "A check loads this crew, but a run will refuse it: ";

    /// <summary>
    /// Why the crew needs the decision models on this run: its own word (<c>decision.mode: required</c>),
    /// or, of a crew that states no mode, the holders of <c>system_one_decide</c> (unless a lenient runner
    /// withholds the tool and goes on) and the decision tasks without an answer of their own.
    /// </summary>
    private static string NeedsText(DecisionPreflightFinding finding)
    {
        if (finding.Usage.Declared)
            return "it states decision.mode: required, so the engine refuses the run before its first task";

        var needs = new List<string>();
        var holders = finding.Usage.ToolHolders;
        if (holders.Count > 0 && finding.Status is not { StrictTools: false })
        {
            needs.Add(string.Join(", ", holders.Select(holder => $"'{holder}'"))
                + " use system_one_decide, which the engine withholds without the decision models");
        }

        var unanswered = finding.Usage.TasksWithoutAnswer;
        if (unanswered.Count > 0)
        {
            var tasks = string.Join(", ", unanswered.Select(task => $"'{task}'"));
            needs.Add(unanswered.Count == 1
                ? $"the decision task {tasks} has neither a fallback nor an otherwise task, so it needs the decision models on"
                : $"the decision tasks {tasks} have neither a fallback nor an otherwise task, so they need the decision models on");
        }

        return string.Join("; ", needs);
    }

    /// <summary>One reason the run goes without the decision models, and its fix.</summary>
    private static string ReasonText(DecisionOffReason reason, DecisionStatus? status) => reason switch
    {
        DecisionOffReason.NoProvider => "No decision provider is configured (an Orkeon:TypeSafe or Orkeon:Liquid section, or its API key; Kev must be named in Orkeon:Decision:Provider).",
        DecisionOffReason.SettingsSwitch => "The settings switch the decision models off (Orkeon:Decision:Enabled is false).",
        DecisionOffReason.NoKey => $"The decision provider {status?.Provider?.DisplayName} has no API key, so the engine turns the decision models off: "
            + $"set {KeyVariableText(status)} (or ApiKey in its section).",
        DecisionOffReason.RunSwitch => "This run switches the decision models off: untick --no-decision.",
        _ => "",
    };

    /// <summary>
    /// The variable to set for a missing key: the provider's own by name; another one by the setting that
    /// names it, never the value written there, which may be a key pasted by mistake (as the engine, Orkeon #467).
    /// </summary>
    private static string KeyVariableText(DecisionStatus? status) =>
        status?.MissingKeyVariableSetting is { } setting
            ? $"the variable named by {setting}"
            : status?.MissingKeyVariable ?? "";

    /// <summary>What one decision task answers once the circuit is off.</summary>
    private static string FallbackText(CrewDecisionTask task) =>
        task.RelayAgent is { } relay
            ? $"the agent '{relay}' answers task '{task.Task}' in place of the model"
            : task.Fallback.Count == 0 && task.OtherwiseTask is { } otherwise
                ? $"task '{task.Task}' gives no answer and task '{otherwise}' runs instead"
                : $"task '{task.Task}' answers "
                  + string.Join(", ", task.Questions.Where(task.Fallback.ContainsKey).Select(question => $"{question} = {task.Fallback[question]}"));

    /// <summary>What becomes, once the circuit is off, of one thing the crew lets System One pick.</summary>
    private static string PickText(CrewSystemOneUse use) => use.Kind switch
    {
        CrewSystemOneUseKind.RouteAuto => $"'{use.Holder}' runs on the default profile, no profile being picked",
        CrewSystemOneUseKind.SkillsAuto => $"'{use.Holder}' carries its fallback skills, none being picked",
        CrewSystemOneUseKind.Prune => $"'{use.Holder}' prunes no tool result",
        CrewSystemOneUseKind.DecisionProfile => $"the decision stages of the profile of '{use.Holder}' are skipped",
        _ => $"'{use.Holder}' keeps the thinking level of its profile, none being picked",
    };

    /// <summary>
    /// The agents the engine will refuse with the mounts of this run (#349): a <c>filesystem:</c>
    /// scope or a working root the folders do not grant, judged by the engine's own rule
    /// (<see cref="FileSystemScopePreflight"/>). A malformed declaration is an error, for the crew
    /// does not load. A scope that does not fit is an error only for a run that starts the agent for
    /// sure with the settings file read here; a warning for a check (<c>--validate</c> starts no
    /// agent), for an agent the run may not start, and when the CLI resolves the settings at launch,
    /// for its mounts may then grant what the list shown here does not. The crew is read again: a
    /// launch sees the files as they are now.
    /// </summary>
    private List<ValidationMessage> ValidateFileSystemScopes(bool dryRun)
    {
        if (Target.Target is not { } target)
            return [];

        return JudgeFileSystemScopes(target).Select(refusal => refusal.Kind switch
        {
            FileSystemScopeRefusalKind.Load => ValidationMessage.Error(
                ValidationCodes.FileSystemScopeRefused,
                $"The engine will refuse this crew as it loads: {refusal.Reason}",
                target.RunPath),
            FileSystemScopeRefusalKind.AtStart when dryRun => ValidationMessage.Warning(
                ValidationCodes.FileSystemScopeRefused,
                $"A check starts no agent, but a run will refuse to start agent '{refusal.Agent}' with these mounts: {refusal.Reason}",
                target.RunPath),
            FileSystemScopeRefusalKind.AtStart when _settingsMountsKnown => ValidationMessage.Error(
                ValidationCodes.FileSystemScopeRefused,
                $"The engine will refuse to start agent '{refusal.Agent}' with these mounts: {refusal.Reason}",
                target.RunPath),
            FileSystemScopeRefusalKind.AtStart => ValidationMessage.Warning(
                ValidationCodes.FileSystemScopeRefused,
                $"Unless the settings the CLI reads mount it, the engine will refuse to start agent '{refusal.Agent}': {refusal.Reason}",
                target.RunPath),
            _ => ValidationMessage.Warning(
                ValidationCodes.FileSystemScopeRefused,
                $"If the run starts agent '{refusal.Agent}', the engine will refuse it with these mounts: {refusal.Reason}",
                target.RunPath),
        }).ToList();
    }

    /// <summary>
    /// The tasks whose deliverable may never be written (Orkeon #392, <see cref="CrewDeliverableWriters"/>):
    /// none of the agents that may run one holds a tool that writes its file, or it is a decision task,
    /// which writes no file. The engine runs them all the same, and an Orkeon 1.2 CLI warns of them at
    /// load too. A warning for a run and a check alike, the crew read again.
    /// </summary>
    private List<ValidationMessage> ValidateDeliverableWriters() =>
        Target.Target is not { } target
            ? []
            : CrewDeliverableWriters.Of(target).Select(missing => ValidationMessage.Warning(
                ValidationCodes.DeliverableWithoutWriter,
                DeliverableWriterText(missing),
                target.RunPath)).ToList();

    /// <summary>The warning of one task: who cannot write its file, and the fix in the crew's YAML.</summary>
    private static string DeliverableWriterText(CrewDeliverableWithoutWriter missing)
    {
        const string AlsoAtLoad = " An Orkeon 1.2 CLI also warns of it when it loads the crew.";
        if (missing.Kind == CrewDeliverableFindingKind.DecisionTask)
        {
            return $"Task '{missing.Task}' is a decision task and must write {missing.Path}, but a decision writes no file: "
                + "the file will not be written. In the crew's YAML, drop the deliverable, or move it to a task an agent runs."
                + AlsoAtLoad;
        }

        var who = missing.Agents.Count == 1
            ? $"its agent '{missing.Agents[0]}' has no tool that writes it"
            : $"none of the agents that may run it ({string.Join(", ", missing.Agents.Select(agent => $"'{agent}'"))}) has a tool that writes it";
        var give = missing.PreferredWriter is { } tool
            ? $"give {tool} to the agent meant to write it"
            : "give the agent meant to write it a tool that writes it (an MCP server's, a plugin's)";
        var other = missing.IsBinary
            ? "or deliver a text format (.md, .html, .csv) instead."
            : "or set the task's deliverable to source: final_message.";
        return $"Task '{missing.Task}' must write {missing.Path}, but {who}: the file may not be written. "
            + $"In the crew's YAML, {give}, {other}" + AlsoAtLoad;
    }

    /// <summary>True when <see cref="Validate"/> found nothing that would break the launch.</summary>
    public bool HasBlockingErrors(bool dryRun = false) =>
        Validate(dryRun).Any(message => message.Severity == ValidationSeverity.Error);

    /// <summary>
    /// The mount list the runtime will see: the runner's auto-injected mounts, then the launch
    /// mounts, then whatever settings mounts the two did not reach. Empty until a target is
    /// resolved — the auto-injected mounts, and therefore every index, depend on it.
    /// </summary>
    public IReadOnlyList<EffectiveMount> EffectiveMounts =>
        Target.Target is { } target ? Options.ComputeEffectiveMounts(target, SettingsMounts) : [];

    /// <summary>
    /// What the mounts dialog says of each refusal, the crew read again: one line each, worded as
    /// <see cref="Validate"/> words it. While the settings mounts are unknown (the CLI resolves the
    /// file at launch, or the file could not be read), a refusal at start is only likely: the mounts
    /// the CLI reads may grant what the list shown does not.
    /// </summary>
    public IReadOnlyList<string> DescribeFileSystemScopeRefusals()
    {
        if (Target.Target is not { } target)
            return [];

        return JudgeFileSystemScopes(target).Select(refusal => refusal.Kind switch
        {
            FileSystemScopeRefusalKind.Load => $"Refused at load - agent '{refusal.Agent}': {refusal.Reason}",
            FileSystemScopeRefusalKind.AtStart when _settingsMountsKnown => $"Refused at start - agent '{refusal.Agent}': {refusal.Reason}",
            FileSystemScopeRefusalKind.AtStart =>
                $"Refused at start unless the settings the CLI reads mount it - agent '{refusal.Agent}': {refusal.Reason}",
            _ => $"Refused if started - agent '{refusal.Agent}': {refusal.Reason}",
        }).ToList();
    }

    /// <summary>
    /// The agents of the crew the engine will refuse with <see cref="EffectiveMounts"/> (#349), the
    /// crew read from disk at each call; empty when no agent declares a scope or a working root.
    /// </summary>
    private IReadOnlyList<FileSystemScopeRefusal> JudgeFileSystemScopes(RunTarget target) =>
        CrewFileSystemScopes.Of(target) is { Count: > 0 } agents
            ? FileSystemScopePreflight.Check(agents, EffectiveMounts)
            : [];

    /// <summary>Why <see cref="EffectiveMounts"/> is empty while no crew is selected.</summary>
    public const string EffectiveMountsUnknownNotice =
        "Select a crew first: the runner injects its own mounts ahead of every --mount, so which " +
        "configuration key each mount occupies depends on the crew being launched.";

    /// <summary>
    /// Re-reads the mounts of the settings file the run will read: the pinned one, else the
    /// team's own file (STUDIO-62). With neither nothing is read: which file wins is then the
    /// CLI's decision, and guessing it here would be a second implementation of the resolution
    /// chain — the screen shows the chain instead.
    /// </summary>
    public void RefreshSettingsMounts()
    {
        var path = ResolvedSettingsPath;
        _settingsMountsKnown = false;
        _settingsDecision = null;
        _settingsBridge = null;
        _settingsCompactionProfile = null;
        _settingsLlmProvider = [];
        _profilesLaidInEnvironment = [];

        if (string.IsNullOrWhiteSpace(path))
        {
            SettingsMounts = [];
            SettingsMountsNotice =
                "Settings are resolved by the CLI at launch, so the mounts already declared are not known here. " +
                "Pin a file with --settings to list them.";
            return;
        }

        if (!_settingsReader.TryRead(path, out var json, out var readError))
        {
            SettingsMounts = [];
            SettingsMountsNotice = readError;
            return;
        }

        if (!AppSettingsDocument.TryParse(json, out var document, out var parseError))
        {
            SettingsMounts = [];
            SettingsMountsNotice = parseError;
            return;
        }

        SettingsMounts = document.Mounts.RawEntries;
        _settingsMountsKnown = true;
        _settingsDecision = document.Decision.Resolve(_environment);
        _settingsBridge = ToolBridgePreflight.Check(document, _environment);
        _settingsLlmProvider = LlmProviderRules.StartRefusals(document);

        // The environment is listed once: a profile is looked up among the names it lays.
        var laid = LaunchLlmProfiles.LaidIn(_environmentNames());
        _profilesLaidInEnvironment =
        [
            .. _settingsDecision.UnknownRungs.Concat(_settingsDecision.DecisionProfiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(laid.Contains),
        ];
        _settingsCompactionProfile = document.Compaction.Profile is { } profile && laid.Contains(profile.Trim())
            ? null
            : document.Compaction.UnknownProfile();
        SettingsMountsNotice = SettingsMounts.Count == 0
            ? $"'{path}' declares no mount."
            : null;
    }

    /// <summary>Spawns the run the form describes.</summary>
    /// <param name="dryRun">True for the "Validate" button: <c>--validate</c>, not recorded in the history.</param>
    /// <param name="onOutput">Called for each output line while the child runs.</param>
    /// <param name="cancellationToken">Cancels the run; so does <see cref="RunSession.RequestCancellation"/>.</param>
    public Task<ProcessRunResult> LaunchAsync(
        bool dryRun = false,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireTarget();

        var request = new RunLaunchRequest
        {
            TargetPath = target.SelectedPath,
            Arguments = BuildArguments(dryRun),
            // Recorded as a pin is: the entry replays on the file the run read (STUDIO-62).
            SettingsPath = Options.ResolvedSettingsPath,
            WorkingDirectory = EffectiveWorkingDirectory,
            RecordInHistory = !dryRun,
        };

        return Session.RunAsync(request, onOutput, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Re-runs a past launch exactly as it was: the recorded argument list is replayed
    /// verbatim, so a replay cannot drift from what actually ran because the form has since
    /// been edited.
    /// </summary>
    public Task<ProcessRunResult> ReplayAsync(
        LaunchHistoryEntry entry,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Arguments.Count == 0)
            throw new InvalidOperationException($"The history entry for '{entry.Target}' recorded no arguments to replay.");

        var request = new RunLaunchRequest
        {
            TargetPath = entry.Target,
            Arguments = entry.Arguments,
            SettingsPath = entry.SettingsPath,
            WorkingDirectory = entry.WorkingDirectory,
        };

        return Session.RunAsync(request, onOutput, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Loads a past launch back into the form, so the user sees what a replay will run and
    /// can adjust it. Returns false when its target no longer resolves — a crew that moved.
    /// </summary>
    public bool ApplyHistoryEntry(LaunchHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        WorkingDirectoryOverride = entry.WorkingDirectory;
        var resolved = Target.Select(entry.Target).IsResolved;
        SyncTeamSettingsPath();

        // An entry recorded on the team's own file was not a pin (STUDIO-62): the form stays
        // automatic, as it was when the entry was recorded.
        var recordedTeamFile = entry.SettingsPath is { Length: > 0 } recorded
            && Options.TeamSettingsPath is { Length: > 0 } teamFile
            && string.Equals(recorded, teamFile, Orkeon.Domain.FileSystem.PhysicalPathContainment.Comparison);
        Options.UseAutomaticSettings = recordedTeamFile || string.IsNullOrWhiteSpace(entry.SettingsPath);
        Options.ExplicitSettingsPath = recordedTeamFile ? null : entry.SettingsPath;
        RefreshSettingsMounts();

        return resolved;
    }

    /// <summary>
    /// Looks the team's settings file up for the current target (STUDIO-62): the workshop's
    /// <c>settings/&lt;slug&gt;/appsettings.json</c> beside the teams root, for a team folder right
    /// under it; null without a root, outside it, or when the file is missing.
    /// </summary>
    private void SyncTeamSettingsPath() =>
        Options.TeamSettingsPath = TeamSettingsFile.Find(TeamsRoot?.Path, Target.SelectedPath, _directories, _targetProbe.FileExists);

    /// <summary>One display line per past launch, newest first.</summary>
    public IReadOnlyList<string> DescribeHistory() =>
    [
        .. Session.History.Entries.Select(entry =>
            $"{entry.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}  [{DescribeOutcome(entry)}]  {entry.Target}"),
    ];

    private static string DescribeOutcome(LaunchHistoryEntry entry) =>
        entry.ExitCode is { } code ? $"{entry.Outcome} {code}" : entry.Outcome.ToString();

    private RunTarget RequireTarget() =>
        Target.Target ?? throw new InvalidOperationException(
            "No crew is selected: pick a .yaml/.ork.ts file or a crew directory first.");

    /// <summary>
    /// The directory the CLI is launched from — <see cref="RunTarget.WorkingDirectory"/>, the
    /// same rule the WPF launcher uses.
    /// <para>
    /// This used to derive it from <c>RunPath</c>, which was correct only while the run path's
    /// parent was the folder the user picked. The ADR-008 detector change made <c>RunPath</c>
    /// descend into a promoted team's <c>crew/</c>, and the fix landed in the WPF launcher
    /// alone: the TUI kept starting the CLI inside <c>crew/</c>, where the sidecar, the
    /// appsettings and the team's own output folder are not.
    /// </para>
    /// </summary>
    private static string? DirectoryOf(RunTarget? target) => target?.WorkingDirectory;

    private static LaunchHistoryFileStore? TryCreateHistoryStore() =>
        LaunchHistoryFileStore.TryGetDefaultPath(out var path, out _)
            ? new LaunchHistoryFileStore(path!)
            : null;

    /// <summary>
    /// The target probe seen as a directory probe, for the one question the team file lookup asks
    /// — is the target a folder — over the same declared disk the detector reads. Nothing is ever
    /// created through it.
    /// </summary>
    private sealed class TargetProbeDirectories(ITargetProbe probe) : IDirectoryProbe
    {
        public bool Exists(string path) => probe.DirectoryExists(path);

        public void Create(string path) =>
            throw new NotSupportedException("The launcher's team file lookup never creates a directory.");
    }
}
