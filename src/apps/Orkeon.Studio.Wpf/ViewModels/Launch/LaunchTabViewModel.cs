using System.Collections.ObjectModel;
using System.Globalization;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Events;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Run;
using System.Text.Json;
using Orkeon.Studio.Core.FileSystem;
using Orkeon.Studio.Core.History;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Core.Targets;
using Orkeon.Studio.Core.Validation;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;
using Orkeon.Studio.Wpf.ViewModels.Services;
using Orkeon.Studio.Wpf.ViewModels.Common;

namespace Orkeon.Studio.Wpf.ViewModels.Launch;

/// <summary>
/// The Launch tab (spec §5): pick a target, pick an appsettings, set the options that apply to that
/// target's shape, then run the co-installed <c>orkeon</c> CLI as a child process with its output
/// streamed live, cancellable, and its exit code interpreted.
/// <para>
/// Running out-of-process is the spec's deliberate choice: a crashing crew cannot take the UI with
/// it, and the behaviour is identical to the CLI by construction rather than by re-implementation.
/// </para>
/// </summary>
public sealed partial class LaunchTabViewModel : ObservableObject
{
    private readonly OrkeonProcessRunner _runner;
    private readonly RunSession _session;
    private volatile IProcessInputWriter? _input;

    // The last run.finished usage, captured on the process thread (W-08): the history
    // entry is recorded before the dispatcher drains, so it cannot read the progress
    // model — this capture is the join between the event stream and the history.
    private long? _finalTokens;
    private long? _finalCacheHitTokens;
    private long? _finalCacheMissTokens;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly IStudioStrings _strings;
    private readonly Func<string, TeamLlmChoice?, string?, IReadOnlyDictionary<string, string>?>? _environmentForTarget;
    private readonly Func<string, TeamLlmChoice, string?>? _rememberLlmChoice;
    private readonly Func<IReadOnlyList<string>> _declaredMounts;
    private readonly string? _teamSettingsRoot;
    private readonly IDirectoryProbe _directories;
    private readonly Func<string, string?>? _restoreTeam;
    private readonly Func<ModelProfileSet>? _modelSettings;
    private readonly Func<IReadOnlyList<string>, string?, Task<string?>>? _prepareLaunch;
    private bool _isRunning;
    private string? _runningTarget;
    private readonly IShellOpener? _shellOpener;
    private TargetDescription _team = new();
    /// <summary>The model this run takes (STUDIO-73): the default, or the setting <see cref="_runLlmProfile"/>.</summary>
    private bool _runUsesDefaultLlm = true;
    private string? _runLlmProfile;
    /// <summary>How many agents and tasks of the target's crew name their own model.</summary>
    private int _crewLlmDeclarations;
    /// <summary>The <c>filesystem:</c> declarations of the target's crew (#349), read when the target changes and again at each launch.</summary>
    private IReadOnlyList<CrewAgentFileSystem> _crewFileSystems = [];
    /// <summary>The refusals the engine will make with the mounts of this run, judged at each change of the target or the mounts, and again at each launch.</summary>
    private IReadOnlyList<FileSystemScopeRefusal> _fileSystemScopeRefusals = [];
    private IReadOnlyList<CrewDeliverableWithoutWriter> _deliverablesWithoutWriter = [];
    private readonly Func<DecisionStatus?> _machineDecisionStatus;
    private readonly IApiKeyStore _keyStore;
    private CrewDecisionUsage _decisionUsage = CrewDecisionUsage.None;
    // The file the run reads (a pin, the team's), once read: its decision circuit is resolved again at
    // each judgement, a key stored since included. While the CLI picks the file, the machine's file
    // stands in for it and is not known to be the run's.
    private AppSettingsDocument? _settingsDecisionDocument;
    private bool _settingsDecisionKnown;
    private DecisionPreflightFinding? _decisionFinding;
    /// <summary>
    /// Whether the panel's settings mounts are those of the file the run will read: true only when the
    /// run carries <c>--settings</c> (the Expert pin, else the team's file) and Studio read that very
    /// file. False while it is loading, when it is missing or unreadable, and in automatic mode
    /// without a team file: the CLI then resolves the file itself, a file beside the crew or above it
    /// winning over the per-user one the panel shows (<c>RunnerSettings.ResolveSettingsPath</c>), and
    /// may mount what Studio does not see.
    /// </summary>
    private bool _settingsMountsKnown;
    /// <summary>
    /// The settings file (pinned, or the team's) the panel's settings mounts were read from; null for
    /// the live declared list, and once the target is read again, so the file is too.
    /// </summary>
    private string? _settingsMountsSource;
    private string? _commandLinePreview;
    private bool _commandLineCopied;
    private readonly IClipboardService _clipboard;
    /// <summary>The outcome sentence of the last run, which the verdict line stands for.</summary>
    private string? _outcomeMessage;
    private string? _statusMessage;
    private bool _isJournalOpen = true;
    private ProcessRunResult? _lastResult;
    private BinaryLocation? _binaryLocation;

    /// <summary>
    /// The teams root a real run stamps its team's last run under (STUDIO-31, D-05; STUDIO-61: the
    /// one the window resolved); null stamps nothing — the Test screen's launcher.
    /// </summary>
    public string? TeamsRoot { get; }

    /// <summary>Builds the tab over its seams; each one has an in-memory double in the tests.</summary>
    public LaunchTabViewModel(LaunchTabDependencies? dependencies = null)
    {
        var seams = dependencies ?? new LaunchTabDependencies();

        _environmentForTarget = seams.EnvironmentForTarget;
        _declaredMounts = seams.DeclaredMounts ?? (() => []);
        _machineDecisionStatus = seams.MachineDecisionStatus ?? (() => null);
        _keyStore = seams.KeyStore ?? new EnvironmentApiKeyStore();
        _clipboard = seams.Clipboard ?? new InMemoryClipboardService();
        _directories = seams.Directories ?? PhysicalDirectoryProbe.Instance;
        _runner = seams.ProcessRunner ?? OrkeonProcessRunner.ForCurrentMachine();
        // The run lifecycle is the shared Core session, not a re-implementation: the terminal
        // launcher runs over the very same class, which is what keeps the two in step. The teams
        // root is how a real run stamps its team's last run (STUDIO-31, D-05) — the trial
        // launcher is built without one.
        _session = new RunSession(_runner, seams.HistoryStore, seams.TeamsRoot);
        TeamsRoot = seams.TeamsRoot;
        IsTrial = seams.IsTrial;
        _teamSettingsRoot = seams.TeamSettingsRoot ?? seams.TeamsRoot;
        _restoreTeam = seams.RestoreTeam;
        _modelSettings = seams.ModelSettings;
        _rememberLlmChoice = seams.RememberLlmChoice;
        _prepareLaunch = seams.PrepareLaunch;
        _settingsStore = seams.SettingsStore ?? PhysicalAppSettingsStore.Instance;
        _dispatcher = seams.Dispatcher ?? ImmediateUiDispatcher.Instance;
        _strings = seams.Strings ?? EnglishStudioStrings.Instance;
        _strings.CultureChanged += (_, _) =>
        {
            OnPropertiesChanged(nameof(BinaryStatus), nameof(ValidationSummary),
                nameof(CliBanner), nameof(TeamMetaLine), nameof(TeamDescription), nameof(OpenResultLabel), nameof(ArchivedTargetMessage),
                nameof(TeamSettingsLine), nameof(CrewLlmWarning),
                nameof(DecisionMessage), nameof(DecisionWarning), nameof(DecisionInfo));
            // The « Default (X) » entry is worded in the language: the list and its selection move with it.
            RaiseRunLlmChanged();
            RaiseRunStateChanged();
        };

        _shellOpener = seams.ShellOpener;
        Target = new TargetSelectionViewModel(seams.TargetProbe, seams.Picker, _strings);
        Options = new LaunchOptionsViewModel(seams.Picker, _strings);
        Mounts = new LaunchMountsViewModel(seams.Directories, seams.Picker, _strings);
        Log = new RunLogViewModel(_strings);
        Progress = new RunProgressViewModel(_strings, seams.Clock);
        History = new LaunchHistoryViewModel(
            seams.HistoryStore, _dispatcher, _strings, _shellOpener, _declaredMounts,
            teamNameOf: target => TeamCatalog.DescribeTarget(target).Name);

        Target.TargetChanged += OnTargetChanged;
        Options.Changed += OnInputsChanged;
        Options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LaunchOptionsViewModel.UseDecisionModels))
                RefreshDecisionVerdict();
        };
        Mounts.Changed += OnMountsChanged;
        History.ReplayRequested += OnReplayRequested;
        History.RestoreRequested += OnRestoreRequested;

        ValidateCommand = new AsyncRelayCommand(() => ValidateAsync(), CanValidate);
        RunCommand = new AsyncRelayCommand(() => RunAsync(), CanLaunch);
        // The undeclared-folders refusal gates the replay too. A recorded argument list is
        // replayed verbatim precisely so it cannot drift from what ran - but "Settings > Allowed
        // folders" is machine policy, and it changes. Without this, revoking a folder left every
        // past launch of that team one click away from running against it anyway, from a button
        // whose whole promise is that it changes nothing.
        ReplayCommand = new AsyncRelayCommand(
            parameter => parameter is LaunchHistoryEntry entry ? ReplayAsync(entry) : Task.CompletedTask,
            _ => IsBinaryAvailable && !IsRunning && !IsBlockedByUndeclaredFolders);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        OpenAllowedFoldersCommand = new RelayCommand(() => OpenAllowedFoldersRequested?.Invoke(this, EventArgs.Empty));
        OpenDecisionSettingsCommand = new RelayCommand(() => OpenDecisionSettingsRequested?.Invoke(this, EventArgs.Empty));
        RestoreTargetCommand = new RelayCommand(() => RestoreTeamAt(_team.TeamDirectory), () => IsTargetArchived);
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine, () => CommandLinePreview is { Length: > 0 });
        ChooseTeamCommand = new RelayCommand(() => ChooseTeamRequested?.Invoke(this, EventArgs.Empty));
        CreateTeamCommand = new RelayCommand(() => CreateTeamRequested?.Invoke(this, EventArgs.Empty));
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        RememberLlmForTeamCommand = new RelayCommand(RememberLlmForTeam, () => CanRememberLlmForTeam);
        ConfigureModelCommand = new RelayCommand(() => ConfigureModelRequested?.Invoke(this, EventArgs.Empty));
        OpenResultCommand = new RelayCommand(OpenResult, () => CanOpenResult);
        OpenLogsCommand = new RelayCommand(OpenLogs, CanOpenLogs);
        CheckOptionsCommand = new RelayCommand(() => CheckOptions());

        // Locate the CLI now rather than only in InitializeAsync. The lookup is a synchronous
        // filesystem probe either way, and settling it here keeps the launch buttons from
        // spending the first frames enabled — offering a run that cannot happen — before the
        // answer lands. InitializeAsync still refreshes it: an install can happen while Studio
        // is open, and the Diagnostic screen re-runs it.
        BinaryLocation = _runner.LocateBinary();

        InitializeTabs();
        InitializeRunPresentation();
        RefreshPreview();
    }

    /// <summary>The target picker (spec §5.1).</summary>
    public TargetSelectionViewModel Target { get; }

    /// <summary>The run options (spec §5.2).</summary>
    public LaunchOptionsViewModel Options { get; }

    /// <summary>The launch-time mount panel (spec §5.2).</summary>
    public LaunchMountsViewModel Mounts { get; }

    /// <summary>The streamed output panel (spec §5.3).</summary>
    public RunLogViewModel Log { get; }

    /// <summary>
    /// What the run reported, as progress rather than scrollback (BUS-06). The raw
    /// <see cref="Log"/> stays available behind it — demoted, not removed.
    /// </summary>
    public RunProgressViewModel Progress { get; }

    /// <summary>The replayable launch history (spec §5.3).</summary>
    public LaunchHistoryViewModel History { get; }

    /// <summary>Runs the crew with <c>--validate</c>: strict load, no LLM probe, no kickoff.</summary>
    public AsyncRelayCommand ValidateCommand { get; }

    /// <summary>Runs the crew for real.</summary>
    public AsyncRelayCommand RunCommand { get; }

    /// <summary>
    /// Re-runs a <see cref="LaunchHistoryEntry"/> exactly as it was recorded. It takes the entry
    /// as its command parameter, which is what the history panel supplies.
    /// </summary>
    public AsyncRelayCommand ReplayCommand { get; }

    /// <summary>Stops the child process: graceful signal first, kill after the grace period.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Empties the log panel.</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>Re-checks the options against the detected shape without launching anything.</summary>
    public RelayCommand CheckOptionsCommand { get; }

    /// <summary>What the current selection would reject, before anything is spawned.</summary>
    public ObservableCollection<ValidationMessageViewModel> ValidationMessages { get; } = [];

    /// <summary>Whether a child process is currently running.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value))
                return;

            RunCommand.RaiseCanExecuteChanged();
            ValidateCommand.RaiseCanExecuteChanged();
            ReplayCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            RaiseRunStateChanged();
        }
    }

    /// <summary>
    /// The target of the run in flight — the path its request carried when it started — or null
    /// while nothing runs (STUDIO-28, D-02). Never <see cref="TargetSelectionViewModel.SelectedPath"/>:
    /// the picker stays live during a run, and a folder browsed to since is not the one being read.
    /// My teams asks it before a gesture that moves or hides a team's folder.
    /// </summary>
    public string? RunningTarget
    {
        get => _runningTarget;
        private set => SetProperty(ref _runningTarget, value);
    }

    /// <summary>The exact command line that will be run, shown so nothing is hidden from the user.</summary>
    public string? CommandLinePreview
    {
        get => _commandLinePreview;
        private set
        {
            if (!SetProperty(ref _commandLinePreview, value))
                return;

            // A new command is not the one that was copied: the button reads "copy" again.
            CommandLineCopied = false;
            CopyCommandLineCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CommandLineDisplayText));
        }
    }

    /// <summary>
    /// Puts <see cref="CommandLinePreview"/> on the clipboard, verbatim — the same text a
    /// terminal takes. The owner's screenshot of 2026-09-20: the command was readable and not
    /// selectable, so reproducing a Studio launch by hand meant retyping a ULID.
    /// </summary>
    public RelayCommand CopyCommandLineCommand { get; }

    /// <summary>True right after a copy, until the command changes; the button's label follows.</summary>
    public bool CommandLineCopied
    {
        get => _commandLineCopied;
        private set => SetProperty(ref _commandLineCopied, value);
    }

    private void CopyCommandLine()
    {
        if (CommandLinePreview is not { Length: > 0 } command)
            return;

        _clipboard.SetText(command);
        CommandLineCopied = true;
    }

    /// <summary>The outcome of the last action.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
                OnPropertiesChanged(nameof(ShowsStatusMessage));
        }
    }

    /// <summary>
    /// Whether the status line says something the verdict line does not (#117): the outcome
    /// sentence of the last run is shown as its badge and exit code, never as a second line.
    /// The announcement of a launch («Running…») is not said either while the run lives: the
    /// title and the badge already say it, and the progress lines say what is going on (D03).
    /// </summary>
    public bool ShowsStatusMessage =>
        _statusMessage is { Length: > 0 }
        && !string.Equals(_statusMessage, _outcomeMessage, StringComparison.Ordinal)
        && !(IsRunning && IsLaunchAnnouncement(_statusMessage));

    /// <summary>The result of the last run, exit code and interpretation included.</summary>
    public ProcessRunResult? LastResult
    {
        get => _lastResult;
        private set
        {
            if (SetProperty(ref _lastResult, value))
                OnPropertiesChanged(nameof(ExitCode), nameof(Outcome), nameof(ExitDescription), nameof(ExitTooltip), nameof(HasResult),
                    nameof(ResultMetricsLine));
            RaiseRunStateChanged();
        }
    }

    /// <summary>Whether a run has completed at least once.</summary>
    public bool HasResult => LastResult is not null;

    /// <summary>The exit code of the last run.</summary>
    public int? ExitCode => LastResult?.ExitCode;

    /// <summary>How the last exit code is classified.</summary>
    public RunOutcome? Outcome => LastResult?.Outcome;

    /// <summary>The plain-language reading of the last exit code.</summary>
    public string? ExitDescription => LastResult?.Description;

    /// <summary>
    /// The tooltip of the finished subtitle: the engine's English sentence, for the expert only —
    /// the novice reads Studio's own sentence in the journal and nothing else.
    /// </summary>
    public string? ExitTooltip => IsExpertMode ? ExitDescription : null;

    /// <summary>
    /// What the finished run cost, under the result sentence (T-23): tokens, cache hit as a
    /// percentage AND in tokens, wall time. A metric the stream did not measure produces no
    /// chip at all — never a zero, which would read as «it cost nothing».
    /// </summary>
    public string? ResultMetricsLine
    {
        get
        {
            var chips = Orkeon.Studio.Core.Launch.UsageMetricsFormatter.Chips(
                _finalTokens is > 0 ? _finalTokens : null,
                _finalCacheHitTokens,
                _finalCacheMissTokens,
                Progress.FinalDurationMs,
                _strings,
                CultureInfo.CurrentCulture);

            return chips.Count > 0 ? string.Join(" · ", chips) : null;
        }
    }

    /// <summary>Where the co-installed CLI was found, or why it was not.</summary>
    public BinaryLocation? BinaryLocation
    {
        get => _binaryLocation;
        private set
        {
            if (!SetProperty(ref _binaryLocation, value))
                return;

            OnPropertiesChanged(nameof(IsBinaryAvailable), nameof(BinaryStatus), nameof(CliBanner));

            // The probe finishes after the window is up: without this the buttons keep the
            // enabled state they were born with.
            RunCommand.RaiseCanExecuteChanged();
            ValidateCommand.RaiseCanExecuteChanged();
            ReplayCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Whether the <c>orkeon</c> binary was located.</summary>
    public bool IsBinaryAvailable => BinaryLocation?.Found ?? false;

    /// <summary>The status line about the co-installed CLI.</summary>
    public string BinaryStatus => BinaryLocation switch
    {
        null => _strings[StudioStringKeys.LaunchBinaryNotLocated],
        { Found: true, Path: { } path } => string.Create(CultureInfo.InvariantCulture, $"orkeon: {path}"),
        var location => location.Error ?? _strings[StudioStringKeys.LaunchBinaryNotFound],
    };

    /// <summary>Whether the current selection would be refused before any process is spawned.</summary>
    public bool HasBlockingErrors => ValidationMessages.Any(m => m.IsError);

    /// <summary>Locates the CLI and loads the history. Called once when the window opens.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BinaryLocation = _runner.LocateBinary();
        await History.LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Publishes the mounts in force from the appsettings the launch will use, so the
    /// effective-mount table shows what the <c>--mount</c> arguments actually replace and a
    /// team folder the settings already hold is not laid a second time (STUDIO-15 D-05).
    /// <para>
    /// A pinned <c>--settings</c> file, or the team's, is read as is, at each call: those mounts are
    /// the run's. In automatic mode the CLI walks its own resolution chain, whose last step is the
    /// per-user file <c>orkeon init</c> writes — the very file « Settings › Authorized folders »
    /// edits, read live through the same seam the launch refusal uses. An adopted team lives under
    /// the teams root, where no closer file usually exists, so that list is shown; but a crew kept
    /// next to its own appsettings.json, or under an <c>appsettings/</c> folder, runs on that file
    /// instead, so the list is not known to be the run's (#349: a scope refusal it predicts is a
    /// warning). Pinning the file shows exactly it.
    /// </para>
    /// </summary>
    public async Task RefreshSettingsMountsAsync(CancellationToken cancellationToken = default)
    {
        // The file the run will read (STUDIO-62): the pin, else the team's own file — whose
        // mount table is then the one in force — else the machine's declared list.
        if (ResolvedSettingsPath is not { Length: > 0 } path)
        {
            PublishSettingsMounts(_declaredMounts(), known: false);
            PublishSettingsDecision(null, known: false);
            return;
        }

        if (!_settingsStore.Exists(path))
        {
            PublishSettingsMounts([], known: false);
            PublishSettingsDecision(null, known: false);
            return;
        }

        var (document, _) = await _settingsStore.TryLoadAsync(path, cancellationToken);
        // The pin or the target moved while the file was read: its mounts are not those of the run.
        if (!string.Equals(path, ResolvedSettingsPath, StringComparison.Ordinal))
            return;

        PublishSettingsMounts(document?.Mounts.RawEntries ?? [], known: document is not null);
        PublishSettingsDecision(document, known: document is not null);
    }

    /// <summary>
    /// Takes <paramref name="document"/>, the file the run reads, whose decision circuit each judgement
    /// resolves as the engine resolves it, the keys read where the settings put them; a document not
    /// known to be the run's leaves the machine's file to stand in for it.
    /// </summary>
    private void PublishSettingsDecision(AppSettingsDocument? document, bool known)
    {
        _settingsDecisionKnown = known && document is not null;
        _settingsDecisionDocument = _settingsDecisionKnown ? document : null;
        RefreshDecisionVerdict();
    }

    /// <summary>
    /// Shows <paramref name="mounts"/> as the settings mounts, and whether they are those the run will
    /// read: an unknown list turns a scope refusal it predicts into a warning (#349).
    /// </summary>
    private void PublishSettingsMounts(IReadOnlyList<string> mounts, bool known)
    {
        var changed = _settingsMountsKnown != known;
        _settingsMountsKnown = known;
        Mounts.SetSettingsMounts(mounts);
        // An unchanged list raises no Changed: the verdict still moves with what is known of it.
        if (changed)
            RefreshFileSystemScopes();
    }

    /// <summary>
    /// Keeps the panel's settings mounts current before the command line is rebuilt: the
    /// declared list is cheap and read every time; a pinned or team file is read once per path
    /// (<see cref="_settingsMountsSource"/>), not on every keystroke of every other field, then
    /// again whenever the target is read again and at each launch.
    /// </summary>
    private void PublishSettingsMounts()
    {
        var path = ResolvedSettingsPath;
        if (path is not { Length: > 0 })
        {
            _settingsMountsSource = null;
            // No --settings: the CLI picks the file, which may not be the one this list comes from.
            PublishSettingsMounts(_declaredMounts(), known: false);
            if (_settingsDecisionKnown)
                PublishSettingsDecision(null, known: false);
            return;
        }

        if (string.Equals(path, _settingsMountsSource, StringComparison.Ordinal))
            return;

        _settingsMountsSource = path;
        // Until the pinned file is read, the list shown is not the one the run will have.
        PublishSettingsMounts(Mounts.SettingsMounts.ToList(), known: false);
        _settingsDecisionKnown = false;
        _ = RefreshSettingsMountsAsync();
    }

    /// <summary>
    /// Re-runs the Core launch validation over the current selection and republishes the messages.
    /// This is the check that keeps an option the CLI would silently ignore from being passed.
    /// </summary>
    public IReadOnlyList<ValidationMessage> CheckOptions()
    {
        ValidationMessages.Clear();

        if (Target.Target is not { } target)
        {
            if (Target.Detection is { IsResolved: false, Error: { Length: > 0 } error, ErrorCode: { } code })
                ValidationMessages.Add(new ValidationMessageViewModel(ValidationMessage.Error(code, error), _strings));

            OnPropertiesChanged(nameof(HasBlockingErrors), nameof(ValidationSummary));
            return [];
        }

        var options = BuildOptions();
        var messages = new List<ValidationMessage>(RunArgumentsBuilder.Validate(target, options));

        // What the detector had to say without refusing the path — the root folders a crew/
        // sub-folder set aside (STUDIO-59) — reads as information, like the directory-run advice.
        foreach (var notice in Target.Detection?.Notices ?? [])
            messages.Add(ValidationMessage.Information(notice.Code, notice.Text, target.RunPath));

        foreach (var message in messages)
            ValidationMessages.Add(new ValidationMessageViewModel(message, _strings));

        foreach (var message in Mounts.LaunchMounts.ValidationMessages)
            ValidationMessages.Add(message);

        OnPropertiesChanged(nameof(HasBlockingErrors), nameof(ValidationSummary));
        RunCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
        return messages;
    }

    /// <summary>The one-line verdict next to the Run button.</summary>
    public string ValidationSummary
    {
        get
        {
            var errors = ValidationMessages.Count(m => m.IsError);
            var warnings = ValidationMessages.Count(m => m.Severity == ValidationSeverity.Warning);

            return errors == 0 && warnings == 0
                ? _strings[StudioStringKeys.LaunchReady]
                : _strings.Format(
                    CultureInfo.InvariantCulture,
                    StudioStringKeys.ConfigErrorsWarnings, errors, warnings);
        }
    }

    /// <summary>Runs the crew with <c>--validate</c> (spec §5.2).</summary>
    public Task<ProcessRunResult?> ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (RefusesArchivedTarget())
            return Task.FromResult<ProcessRunResult?>(null);

        BeginLaunch();
        return LaunchAsync(validate: true, cancellationToken);
    }

    /// <summary>
    /// Runs the crew for real (spec §5.3). With the dry-run-first option (mock,
    /// expert options), a <c>--validate</c> pass runs first and a failed one stops here —
    /// the dry run is exactly the protection it claims to be.
    /// </summary>
    public async Task<ProcessRunResult?> RunAsync(CancellationToken cancellationToken = default)
    {
        if (RefusesArchivedTarget())
            return null;

        BeginLaunch();

        // The check that comes first passes whatever the scopes: the run it guards is refused before it.
        if (Options.ValidateFirst && await RefusesFileSystemScopeAsync(validate: false, cancellationToken).ConfigureAwait(true))
            return null;

        if (Options.ValidateFirst)
        {
            var validation = await LaunchAsync(validate: true, cancellationToken).ConfigureAwait(true);
            if (validation is not { ExitCode: 0 })
                return validation;
        }

        return await LaunchAsync(validate: false, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// The archived state, read again right before a launch (STUDIO-32, after STUDIO-31 D-07): the one
    /// the target was picked with may be stale — a team archived by hand in its sidecar, or from a
    /// second window, stayed launchable until its target was picked again. True when the target turned
    /// out archived: its banner then offers the restore, and nothing runs.
    /// </summary>
    private bool RefusesArchivedTarget()
    {
        if (Target.Target is not { } target || !TeamCatalog.DescribeTarget(target.SelectedPath).IsArchived)
            return false;

        RefreshTeamDescription();
        StatusMessage = _strings[StudioStringKeys.CommonArchivedTeamRestore];
        return true;
    }

    /// <summary>Stops the running child process.</summary>
    public void Cancel()
    {
        _session.RequestCancellation();
        StatusMessage = _strings[StudioStringKeys.LaunchCancelling];
    }

    /// <summary>
    /// The arguments the current selection produces, empty when it cannot produce any — no target
    /// is resolved, or the selection carries a blocking validation error. A built list always opens
    /// with the run verb and the target path, so emptiness is unambiguous.
    /// </summary>
    public IReadOnlyList<string> BuildArguments()
    {
        if (Target.Target is not { } target)
            return [];

        var options = BuildOptions();
        if (RunArgumentsBuilder.Validate(target, options).Any(m => m.Severity == ValidationSeverity.Error))
            return [];

        return RunArgumentsBuilder.Build(target, options);
    }

    private RunLaunchOptions BuildOptions(bool validate = false) =>
        Options.ToOptions(
            Mounts.ToMountArguments(), Mounts.AllowExternalMounts, validate, Mounts.ToMountIdArguments(),
            settingsPath: ResolvedSettingsPath);

    /// <summary>
    /// The workshop's settings file for the selected team (STUDIO-62): <c>settings/&lt;slug&gt;/appsettings.json</c>
    /// beside the teams root, found by <see cref="TeamSettingsFile.Find"/> at each choice of target
    /// — the team folder right under <see cref="TeamsRoot"/>, the file read through the settings
    /// store. Null for a team without one, a team outside the root, any other folder, or a tab
    /// built without a root. Studio never writes this file.
    /// </summary>
    public string? TeamSettingsPath { get; private set; }

    /// <summary>
    /// The settings file the run will read: the Expert mode's pin, else the team's file, else null
    /// — the CLI's own chain. The command line, the request, the history entry and the settings
    /// mounts panel all read this one. A pin left blank is no pin: the Core model reads it so too
    /// (<c>LaunchOptionsModel.EffectiveSettingsPath</c>), and the team's file then applies.
    /// </summary>
    public string? ResolvedSettingsPath => HasPin ? Options.EffectiveSettingsPath : TeamSettingsPath;

    /// <summary>Whether the Expert mode pins a settings file — a non-blank path in explicit mode.</summary>
    private bool HasPin => !string.IsNullOrWhiteSpace(Options.EffectiveSettingsPath);

    /// <summary>
    /// The one line that says where the model comes from, shown beside the command line and
    /// outside the « Advanced » fold: the team's settings file, and — when the run pins a model
    /// setting of this machine (STUDIO-73: the card's choice, or the one made here for this run) —
    /// that the setting overrides the file's <c>Llm</c> section (laid over the run as <c>ORKEON_Llm__*</c>). Null when no team
    /// file applies — and while a pinned file overrides it: the line would then say the run reads
    /// a file it does not.
    /// </summary>
    public string? TeamSettingsLine
    {
        get
        {
            if (TeamSettingsPath is not { Length: > 0 } path || HasPin)
                return null;

            var line = _strings.Format(StudioStringKeys.RunSettingsTeamFile, path);
            return CardNamesAKnownSetting()
                ? line + " " + _strings[StudioStringKeys.RunSettingsTeamFileProfileNote]
                : line;
        }
    }

    /// <summary>
    /// Whether the run pins a model setting the launch will lay over it: one this machine has
    /// (<see cref="TeamSettingStanding"/>); a tab built without the model settings takes the choice
    /// at its word, as the meta line does. A run that follows the default reads the file's.
    /// </summary>
    private bool CardNamesAKnownSetting() =>
        RunLlmChoice is { UseDefault: false, Profile: { Length: > 0 } profile }
        && (_modelSettings is null || TeamSettingStanding.Of(profile, _modelSettings()).Kind != TeamSettingKind.Missing);

    /// <summary>
    /// Re-reads the team's settings file for the current target — the file may have appeared, or
    /// the target may have left the workshop — and tells the screen.
    /// </summary>
    private void RefreshTeamSettingsPath()
    {
        TeamSettingsPath = TeamSettingsFile.Find(_teamSettingsRoot, Target.SelectedPath, _directories, _settingsStore.Exists);
        OnPropertiesChanged(nameof(TeamSettingsPath), nameof(ResolvedSettingsPath), nameof(TeamSettingsLine), nameof(RunLlmLine));
    }

    // A launch is an invocation of the orkeon CLI, so an absent CLI is a refusal, not a
    // late failure: a live Run button that answers a click with «the tool was not located»
    // is a button that lied about being available. The banner says what is wrong; the
    // commands must agree with it.
    // An archived team is not launched, nor tested, from Studio (STUDIO-31, D-07): its banner
    // offers the restore instead of a button that would run it anyway.
    private bool CanLaunch() => CanValidate() && !IsBlockedByFileSystemScope && !IsBlockedByDecision;

    // A check (--validate) loads the crew and starts no agent: of the scope refusals, only a malformed
    // declaration fails it (#349).
    private bool CanValidate() =>
        IsBinaryAvailable && !IsRunning && Target.IsResolved && !IsBlockedByUndeclaredFolders && !IsBlockedByUnknownMountIds
        && !IsTargetArchived && !_fileSystemScopeRefusals.Any(refusal => refusal.Kind == FileSystemScopeRefusalKind.Load)
        && _decisionFinding?.BlocksValidate != true;

    private async Task<ProcessRunResult?> LaunchAsync(bool validate, CancellationToken cancellationToken)
    {
        CheckOptions();

        if (Target.Target is not { } target)
        {
            StatusMessage = _strings[StudioStringKeys.LaunchNoTarget];
            return null;
        }

        if (HasBlockingErrors)
        {
            StatusMessage = _strings.Format(
                CultureInfo.InvariantCulture,
                StudioStringKeys.LaunchNotLaunchedErrors,
                ValidationMessages.Count(m => m.IsError));
            return null;
        }

        if (await RefusesFileSystemScopeAsync(validate, cancellationToken).ConfigureAwait(true))
            return null;

        if (RefusesDecision(validate))
            return null;

        var options = BuildOptions(validate);
        // The team's file counts as a pin here (STUDIO-62): a launch that reads another file
        // than the machine's saves nothing into the machine's.
        if (await PrepareAsync(options.MountIds, ResolvedSettingsPath).ConfigureAwait(true) is { } refusal)
        {
            StatusMessage = refusal;
            return null;
        }

        // The team's own folders, as the workshop's launchers prepare them (STUDIO-60): a dry
        // run prepares too, since the launcher would.
        if (PrepareTeamFolders(TeamDirectory(), _team.ResolvedMounts) is { } folderRefusal)
        {
            StatusMessage = folderRefusal;
            return null;
        }

        var arguments = RunArgumentsBuilder.Build(target, options);
        var workingDirectory = GetWorkingDirectory(target);

        return await ExecuteAsync(
            new RunLaunchRequest
            {
                TargetPath = target.SelectedPath,
                Arguments = arguments,
                // Recorded as a pin is: the entry replays on the file the run read (STUDIO-62).
                SettingsPath = ResolvedSettingsPath,
                WorkingDirectory = workingDirectory,
                // A dry run is not a launch: recording it would fill the replayable history with
                // entries that never ran a crew. The terminal launcher makes the same exclusion.
                RecordInHistory = !validate,
                EnvironmentOverrides = EnvironmentFor(target.SelectedPath, RunLlmChoice, ResolvedSettingsPath),
                // Kept for « Relancer » (STUDIO-73, D8): the replay runs on the model this run took.
                LlmChoice = RunLlmChoice,
            },
            validate,
            cancellationToken);
    }

    /// <summary>
    /// The scope verdict, made again right before a launch (#349): the crew and the settings file the
    /// run carries (the pin, else the team's) may have been edited on disk since the target was
    /// picked, and a launch judges both as they are now. A check does too, and it stays offered while
    /// a scope refusal blocks « Run »: it is how a run blocked on a mount since added to the settings
    /// file is offered again. Refuses what the commands refuse (<see cref="CanLaunch"/>,
    /// <see cref="CanValidate"/>): a run on any blocking refusal, a check on a malformed declaration
    /// alone. True when the launch stops, its reason said.
    /// </summary>
    private async Task<bool> RefusesFileSystemScopeAsync(bool validate, CancellationToken cancellationToken)
    {
        _crewFileSystems = CrewFileSystemScopes.Of(Target.Target);
        await RefreshSettingsMountsAsync(cancellationToken).ConfigureAwait(true);
        RefreshFileSystemScopes();
        // The crew may have been edited since the target was picked: the warning follows the files.
        _deliverablesWithoutWriter = CrewDeliverableWriters.Of(Target.Target);
        OnPropertiesChanged(nameof(HasDeliverableWriterWarning), nameof(DeliverableWriterWarning));
        _decisionUsage = CrewDecisionUsage.Of(Target.Target);
        RefreshDecisionVerdict();

        if (!validate)
        {
            if (IsBlockedByFileSystemScope)
                StatusMessage = FileSystemScopeMessage;
            return IsBlockedByFileSystemScope;
        }

        // A check fails on the malformed declarations alone: the engine's own reasons, without the
        // sentence of a blocked run, which also lists the refusals at start a check passes.
        var malformed = _fileSystemScopeRefusals
            .Where(refusal => refusal.Kind == FileSystemScopeRefusalKind.Load)
            .Select(refusal => refusal.Reason)
            .ToList();
        if (malformed.Count > 0)
            StatusMessage = string.Join(" ", malformed);
        return malformed.Count > 0;
    }

    /// <summary>
    /// Re-runs a past launch exactly as it was: the recorded argument list is replayed verbatim,
    /// so a replay cannot drift from what actually ran because the form has since been edited.
    /// The form is still refreshed from the entry, so the user sees what is running.
    /// </summary>
    public async Task<ProcessRunResult?> ReplayAsync(
        LaunchHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Arguments.Count == 0)
        {
            StatusMessage = _strings.Format(
                CultureInfo.InvariantCulture,
                StudioStringKeys.LaunchNothingToReplay,
                entry.Target);
            return null;
        }

        // STUDIO-31, D-07: the recorded argv would run an archived team as if nothing had changed —
        // one click, no confirmation. The guard reads the ENTRY's target, not the form's, and the
        // entry offers the restore rather than refusing in silence; the form is left as it was.
        var team = TeamCatalog.DescribeTarget(entry.Target);
        if (team.IsArchived)
        {
            StatusMessage = _strings[StudioStringKeys.CommonArchivedTeamRestore];
            History.OfferRestore(entry);
            return null;
        }

        // The recorded ids, read against the settings file as saved, as a launch's (STUDIO-52).
        if (await PrepareAsync(RecordedMountIds(entry.Arguments), entry.SettingsPath).ConfigureAwait(true) is { } refusal)
        {
            StatusMessage = refusal;
            return null;
        }

        // The journal of this launch begins before the folders are prepared, as a Run's does, so the
        // folders created are named in it and a replay that goes on does not wipe the line (STUDIO-60).
        BeginLaunch();

        // The team's own folders, as a launch's (STUDIO-60); the recorded argv is replayed as it is.
        if (PrepareTeamFolders(team.TeamDirectory, team.ResolvedMounts) is { } folderRefusal)
        {
            StatusMessage = folderRefusal;
            return null;
        }

        LoadIntoForm(entry);
        // The model the run took (STUDIO-73, D8); an entry recorded before has none and takes the
        // team's choice of today. A setting since removed reads as missing and runs on the default.
        if (entry.LlmChoice is { } recorded)
            ApplyRunLlmChoice(recorded);

        return await ExecuteAsync(
            new RunLaunchRequest
            {
                TargetPath = entry.Target,
                Arguments = entry.Arguments,
                SettingsPath = entry.SettingsPath,
                WorkingDirectory = entry.WorkingDirectory,
                // The argv is replayed verbatim; the model is the one the run took, the setting behind
                // its name read fresh — a default since re-elected replays on the default of today.
                EnvironmentOverrides = EnvironmentFor(entry.Target, entry.LlmChoice, entry.SettingsPath),
                LlmChoice = entry.LlmChoice,
            },
            dryRun: false,
            cancellationToken);
    }

    /// <summary>
    /// The environment the run's model lays over the launch — <paramref name="choice"/>, else the
    /// team's — given the settings file it reads (null: the machine's); empty when nothing is wired.
    /// </summary>
    private IReadOnlyDictionary<string, string> EnvironmentFor(string targetPath, TeamLlmChoice? choice, string? settingsPath) =>
        _environmentForTarget?.Invoke(targetPath, choice, settingsPath)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The preparation of a launch (STUDIO-52, decision 4): the runner looks every <c>--mount-id</c> up
    /// in the settings file as saved, and a declaration the screen gave its id when it read the file
    /// is in the document only — the shell saves it first. Null to launch; the line that says why the
    /// run does not start, when the save was refused.
    /// </summary>
    private async Task<string?> PrepareAsync(IReadOnlyList<string> mountIds, string? settingsPath)
    {
        if (_prepareLaunch is null || mountIds.Count == 0)
            return null;

        return await _prepareLaunch(mountIds, settingsPath).ConfigureAwait(true) is { } refusal
            ? _strings.Format(StudioStringKeys.LaunchSettingsNotSaved, refusal)
            : null;
    }

    /// <summary>
    /// The team's own folders before a launch (STUDIO-60): a missing writable one is created and the
    /// journal says so; a missing read-only one, or a creation the disk refuses, stops the launch.
    /// Null to launch; the line that says why the run does not start otherwise. Called once the journal
    /// of the launch has begun — a Run's, a Replay's —, so the notice stays in it.
    /// </summary>
    private string? PrepareTeamFolders(string? teamDirectory, IReadOnlyList<ResolvedTeamMount> mounts)
    {
        var prepared = TeamFolderPreparation.Prepare(teamDirectory, mounts, _directories);

        if (prepared.Created.Count > 0)
        {
            Log.AppendNotice(_strings.Format(
                StudioStringKeys.RunTeamFoldersCreated,
                string.Join(", ", prepared.Created)));
        }

        if (prepared.Failures is [var (folder, error), ..])
            return _strings.Format(StudioStringKeys.RunTeamFolderCreateFailed, folder, error);

        if (prepared.MissingReadOnly is [var (missing, virtualPath), ..])
            return _strings.Format(StudioStringKeys.RunTeamFolderMissingReadOnly, missing, virtualPath);

        return null;
    }

    /// <summary>The <c>--mount-id</c> values of a recorded argument list: the ids after the option, up to the next option.</summary>
    private static List<string> RecordedMountIds(IReadOnlyList<string> arguments)
    {
        var option = RunOptionAvailability.ToCommandLineName(RunOption.MountIds);
        var ids = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (!string.Equals(arguments[i], option, StringComparison.Ordinal))
                continue;

            for (i++; i < arguments.Count && !arguments[i].StartsWith('-'); i++)
                ids.Add(arguments[i]);
            i--;
        }

        return ids;
    }

    /// <summary>
    /// A launch starts from a clean screen (STUDIO-17). The journal used to accumulate across
    /// runs, and the previous run's verdict — exit badge, result row, « Open the result » —
    /// stayed on screen for the whole of the next run, so a reader could take the old outcome
    /// for the new one. Called once per user gesture, not per pass: a « validate first »
    /// launch keeps its dry run and its real run in one journal. « Clear the journal » stays
    /// for the rest; « Copy » is how a journal survives the next click.
    /// </summary>
    private void BeginLaunch()
    {
        Log.Clear();
        LastResult = null;
    }

    private async Task<ProcessRunResult> ExecuteAsync(
        RunLaunchRequest request,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // The target first, so whoever reacts to IsRunning already reads what is running.
        RunningTarget = request.TargetPath;
        ForgetLogFolderProbe();
        IsRunning = true;
        ActiveTab = RunTab;

        Log.AppendCommand(CommandLineDisplay.Format(request.Arguments));
        StatusMessage = dryRun
            ? _strings[StudioStringKeys.LaunchValidating]
            : _strings[StudioStringKeys.LaunchRunning];

        Progress.Reset(Answer, Reply);
        _finalTokens = _finalCacheHitTokens = _finalCacheMissTokens = null;

        try
        {
            var result = await _session.RunAsync(
                request,
                line =>
                {
                    CaptureFinalUsage(line);
                    _dispatcher.Post(() => Receive(line));
                },
                writer => _input = writer,
                cancellationToken: cancellationToken,
                enrichEntry: entry => entry.WithUsage(_finalTokens, _finalCacheHitTokens, _finalCacheMissTokens));

            LastResult = result;

            // A dry run's outcome is a verdict, not just an exit code: the launcher says whether
            // the crew validated, as the terminal launcher does.
            var outcome = LaunchOutcomeFormatter.Describe(result, dryRun, _strings);
            _outcomeMessage = outcome;
            StatusMessage = outcome;
            Log.AppendOutcome(outcome);

            // The session already recorded the run; the panel only projects its list.
            if (request.RecordInHistory)
            {
                History.Publish(_session.History);
                // The team cards' last-run line reads the same history —
                // the shell refreshes it now, not at the next app start (review D5).
                RunRecorded?.Invoke(this, EventArgs.Empty);
            }

            return result;
        }
        finally
        {
            IsRunning = false;
            RunningTarget = null;
            _input = null;
        }
    }

    /// <summary>
    /// Routes one output line: a protocol event feeds the progress panel, anything else lands
    /// in the raw log. A line the panel cannot read is never dropped — what the stream said
    /// stays visible, which is the rule the terminal launcher already followed.
    /// </summary>
    private void Receive(ProcessOutputLine line)
    {
        if (line.Channel == ProcessOutputChannel.StandardOutput && Progress.TryApply(line.Text))
        {
            // A token delta arrives per token and cannot make the folder appear: skip it.
            if (!(OrkeonEventParser.TryParse(line.Text, out var applied) && applied!.Kind == RunEventKinds.LlmDelta))
                RefreshOpenLogs();
            return;
        }

        Log.Append(line);
        RefreshOpenLogs();
    }

    // One Exists per refresh: the probe's answer is kept until the next refresh, and once the
    // folder was found it is kept until a new run starts or the target/options change.
    private (string Folder, bool Exists)? _probedLogFolder;

    private void ForgetLogFolderProbe() => _probedLogFolder = null;

    /// <summary>The log folder may appear while the run goes on: the reason and the command follow each line.</summary>
    private void RefreshOpenLogs()
    {
        if (_probedLogFolder is not { Exists: true })
            _probedLogFolder = null;
        OnPropertiesChanged(nameof(OpenLogsUnavailableReason));
        OpenLogsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Reads the run's closing usage off the raw line, on the process thread — before the
    /// dispatcher, because the history entry is recorded the moment the process exits.
    /// </summary>
    private void CaptureFinalUsage(ProcessOutputLine line)
    {
        if (line.Channel != ProcessOutputChannel.StandardOutput
            || !OrkeonEventParser.TryParse(line.Text, out var orkeonEvent)
            || orkeonEvent!.Kind != RunEventKinds.RunFinished)
        {
            return;
        }

        var tokens = orkeonEvent.GetInt64("tokens");
        _finalTokens = tokens is > 0 ? tokens : null;
        _finalCacheHitTokens = orkeonEvent.GetInt64("cacheHitTokens");
        _finalCacheMissTokens = orkeonEvent.GetInt64("cacheMissTokens");
    }

    /// <summary>
    /// Sends one answer down the run's stdin. False when no child is listening, which the
    /// panel reads as "the question is still open" rather than pretending it was answered.
    /// </summary>
    private bool Answer(string correlationId, string value) =>
        _input is { } writer
        && writer.TryWriteLine(JsonSerializer.Serialize(
            new { kind = RunEventKinds.InputGiven, correlationId, value }));

    /// <summary>
    /// Replies to an agent's <c>send</c> down the run's stdin — the seat at the hub the doc
    /// used to say Studio does not take. Text that parses as JSON travels as that JSON (an
    /// agent may expect a shape); anything else travels as a plain JSON string. The wire form
    /// is the one <c>RunClient.ReplyToAgent</c> writes and the bridge's <c>ReplyFromPeer</c>
    /// requires.
    /// </summary>
    private bool Reply(string correlationId, string text) =>
        _input is { } writer
        && writer.TryWriteLine(JsonSerializer.Serialize(
            new { kind = "reply", correlationId, payload = AsJsonPayload(text) }));

    private static object AsJsonPayload(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string? GetWorkingDirectory(RunTarget target) => target.WorkingDirectory;

    /// <summary>Raised after a real (non-dry) run landed in the history.</summary>
    public event EventHandler? RunRecorded;

    /// <summary>
    /// Re-reads the selected target's sidecar — the change-folders action may have edited
    /// the very team the launcher points at, and the run must lay the NEW mounts
    /// (review D6: the chips and the command must not disagree, even for a minute).
    /// </summary>
    public void RefreshTeamDescription()
    {
        var saved = _team.LlmChoice;
        _team = TeamCatalog.DescribeTarget(Target.SelectedPath, _declaredMounts());
        _crewLlmDeclarations = CrewLlmDeclarations.CountIn(Target.SelectedPath);
        _crewFileSystems = CrewFileSystemScopes.Of(Target.Target);
        _fileSystemScopeRefusals = JudgeFileSystemScopes();
        _deliverablesWithoutWriter = CrewDeliverableWriters.Of(Target.Target);
        _decisionUsage = CrewDecisionUsage.Of(Target.Target);
        RefreshDecisionVerdict();
        // The settings file of the run is read again with the crew (RefreshPreview, below).
        _settingsMountsSource = null;
        // A choice saved from the card replaces the run's; a choice made here for this run stays.
        if (_team.LlmChoice != saved)
            ApplyRunLlmChoice(_team.LlmChoice);
        RefreshTeamSettingsPath();
        Mounts.SetTeamMounts(_team.ResolvedMounts);
        Mounts.AllowExternalMounts = ReachesOutsideTheTeam();
        OnPropertiesChanged(nameof(TeamHeadline), nameof(TeamMetaLine), nameof(TeamDescription), nameof(HasTeamCard),
            nameof(UndeclaredTeamFolders), nameof(IsBlockedByUndeclaredFolders), nameof(UndeclaredFoldersMessage),
            nameof(UnknownTeamMountIds), nameof(IsBlockedByUnknownMountIds), nameof(UnknownMountIdsMessage),
            nameof(FileSystemScopeRefusals), nameof(IsBlockedByFileSystemScope), nameof(FileSystemScopeMessage),
            nameof(HasFileSystemScopeWarning), nameof(FileSystemScopeWarning),
            nameof(HasDeliverableWriterWarning), nameof(DeliverableWriterWarning),
            nameof(IsTargetArchived), nameof(ArchivedTargetMessage), nameof(CrewLlmWarning));
        RunCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
        RestoreTargetCommand.RaiseCanExecuteChanged();
        // Replay is gated on the same refusal, so it has to be told when the refusal changes -
        // otherwise the button stays enabled until something else happens to refresh it.
        ReplayCommand.RaiseCanExecuteChanged();
        RefreshPreview();
    }

    /// <summary>
    /// Follows a team renamed from My teams (STUDIO-28): a target inside <paramref name="from"/> —
    /// the folder, or a crew file in it — is picked again at the same place under
    /// <paramref name="to"/>, where the team now is. Any other target is left as it is.
    /// </summary>
    public void FollowRenamedTeam(string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        var folder = TeamCatalog.NormalizePath(from);
        if (Target.SelectedPath is not { Length: > 0 } selected
            || string.Equals(folder, TeamCatalog.NormalizePath(to), Orkeon.Domain.FileSystem.PhysicalPathContainment.Comparison)
            || !Orkeon.Domain.FileSystem.PhysicalPathContainment.IsUnder(TeamCatalog.NormalizePath(selected), folder))
        {
            return;
        }

        var inside = System.IO.Path.GetRelativePath(folder, TeamCatalog.NormalizePath(selected));
        Target.Select(inside == "." ? to : System.IO.Path.Combine(to, inside));
    }

    /// <summary>
    /// Display name of the selected team (sidecar-backed, file name otherwise) — one line,
    /// whatever the sidecar says (STUDIO-16, D-01): a TextBlock renders line breaks even
    /// without wrapping, and a pasted page in <c>name</c> used to fill the whole card.
    /// </summary>
    public string? TeamHeadline => _team.Name is { Length: > 0 } name ? TeamCatalog.NormalizeName(name) : _team.Name;

    /// <summary>
    /// The need under the meta line, on its own (#117): the derived one-paragraph summary, never
    /// the whole need (STUDIO-16, D-03) — the user's brief, pages long when a README was pasted.
    /// </summary>
    public string? TeamDescription => _team.Summary is { Length: > 0 } summary ? summary : null;

    /// <summary>"3 agents · reads /docs · setting X" — only the parts the catalog can honestly assert.</summary>
    public string? TeamMetaLine
    {
        get
        {
            var parts = TeamMetaParts();
            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
    }

    /// <summary>The parts of <see cref="TeamMetaLine"/>, each as shown: agents, folders, then « model X ».</summary>
    private List<string> TeamMetaParts()
    {
        var parts = new List<string>(4);
        if (_team.AgentCount is { } agents)
            parts.Add(_strings.Format(StudioStringKeys.RunMetaAgents, agents));
        foreach (var mountString in _team.Mounts)
        {
            // "reads /docs · writes to /output" — the sidecar's mounts, in words.
            if (MountDefinition.TryParse(mountString, out var mount, out _) && mount is not null)
            {
                parts.Add(_strings.Format(
                    mount.Rights == MountRights.ReadOnly ? StudioStringKeys.RunMetaReads : StudioStringKeys.RunMetaWrites,
                    mount.VirtualPath));
            }
        }

        // A setting no setting of this machine bears runs on the default: said with the card's
        // own words (STUDIO-52).
        if (_team.Profile is { Length: > 0 } profile)
        {
            var setting = _modelSettings is null ? profile : TeamSettingStanding.Of(profile, _modelSettings()).Label(_strings);
            parts.Add(_strings.Format(StudioStringKeys.RunMetaProfile, setting));
        }

        return parts;
    }

    // ── STUDIO-73: the model this run takes ──

    /// <summary>« Remember for this team »: the run's model becomes the team's.</summary>
    public RelayCommand RememberLlmForTeamCommand { get; }

    /// <summary>« Configure a model », when this machine has no setting yet: the settings' model tab.</summary>
    public RelayCommand ConfigureModelCommand { get; }

    /// <summary>Raised by <see cref="ConfigureModelCommand"/>; the shell opens the model settings.</summary>
    public event EventHandler? ConfigureModelRequested;

    /// <summary>Whether this run takes the default elected when it starts, rather than one setting.</summary>
    public bool RunUsesDefaultLlm
    {
        get => _runUsesDefaultLlm;
        set
        {
            if (_runUsesDefaultLlm == value)
                return;

            _runUsesDefaultLlm = value;
            // Unticked, the list offers the first setting rather than nothing.
            if (!value && _runLlmProfile is null && LlmProfiles is [var first, ..])
                _runLlmProfile = first;
            RaiseRunLlmChanged();
        }
    }

    /// <summary>The setting this run takes when it does not follow the default, by its name.</summary>
    public string? RunLlmProfile
    {
        get => _runLlmProfile;
        set
        {
            if (string.Equals(_runLlmProfile, value, StringComparison.Ordinal))
                return;

            _runLlmProfile = value;
            RaiseRunLlmChanged();
        }
    }

    /// <summary>The entries of the model list: « Default (X) » first, then the settings by name.</summary>
    public IReadOnlyList<string> LlmChoices => [DefaultLlmEntry, .. LlmProfiles];

    /// <summary>The entry the list shows selected, held by string; the default entry is <see cref="RunUsesDefaultLlm"/>.</summary>
    public string? SelectedLlmChoice
    {
        get => _runUsesDefaultLlm || _runLlmProfile is null ? DefaultLlmEntry : _runLlmProfile;
        set
        {
            if (value is null)
                return;

            if (string.Equals(value, DefaultLlmEntry, StringComparison.Ordinal))
            {
                RunUsesDefaultLlm = true;
                return;
            }

            // Only a setting's name pins the run: a stale « Default (X) » worded in another language is no setting.
            if (!LlmProfiles.Contains(value, StringComparer.Ordinal))
                return;

            _runUsesDefaultLlm = false;
            RunLlmProfile = value;
            RaiseRunLlmChanged();
        }
    }

    private string DefaultLlmEntry =>
        _strings.Format(
            StudioStringKeys.RunLlmDefaultEntry,
            _modelSettings?.Invoke().Default?.Name ?? _strings[StudioStringKeys.TeamsLlmNoDefault]);

    /// <summary>The settings of this machine a run can take, by name; empty without model settings.</summary>
    public IReadOnlyList<string> LlmProfiles =>
        _modelSettings is null ? [] : [.. _modelSettings().Profiles.Select(profile => profile.Name)];

    /// <summary>Whether this machine has a setting at all: otherwise the box is greyed and « Configure a model » shows.</summary>
    public bool HasLlmProfiles => LlmProfiles.Count > 0;

    /// <summary>« Use the default model (X) », X the setting elected; without one, says so.</summary>
    public string UseDefaultLlmLabel =>
        _modelSettings?.Invoke().Default is { } elected
            ? _strings.Format(StudioStringKeys.TeamsUseDefaultLlm, elected.Name)
            : _strings[StudioStringKeys.TeamsUseDefaultLlmNone];

    /// <summary>The model this run takes, as the launch and the history record it.</summary>
    public TeamLlmChoice RunLlmChoice =>
        _runUsesDefaultLlm ? TeamLlmChoice.Default : TeamLlmChoice.Pinned(_runLlmProfile);

    /// <summary>
    /// « This run: Default (X) » or « This run: Y » — with the words of the card when the setting is
    /// absent from this machine (STUDIO-52), or when the team's own settings file decides (D7). Null
    /// without model settings, or without a target.
    /// </summary>
    public string? RunLlmLine
    {
        get
        {
            if (_modelSettings is null || !Target.IsResolved)
                return null;

            var settings = _modelSettings();
            var choice = RunLlmChoice;
            var label = choice.UseDefault
                ? TeamSettingStanding.None.LlmLabel(_strings, settings, ownSettingsFile: ResolvedSettingsPath is { Length: > 0 })
                : TeamSettingStanding.Of(choice.Profile, settings).LlmLabel(_strings, settings);
            return _strings.Format(StudioStringKeys.RunLlmForThisRun, label);
        }
    }

    /// <summary>Whether « Remember for this team » applies: a team whose recorded model differs from the run's.</summary>
    public bool CanRememberLlmForTeam =>
        _team.LlmChoice is { } saved && saved != RunLlmChoice && !IsTargetArchived && !IsRunning;

    /// <summary>
    /// The agents and tasks of the crew that name their own model: the choice above does not reach
    /// them (STUDIO-73). Null when there is none.
    /// </summary>
    public string? CrewLlmWarning =>
        _crewLlmDeclarations > 0 ? _strings.Format(StudioStringKeys.TeamsCrewOverridesLlm, _crewLlmDeclarations) : null;

    /// <summary>
    /// Re-reads the model settings — a setting added, removed or elected elsewhere — for the list,
    /// the labels and the line; a setting the run named that is gone stays named, and runs on the default.
    /// </summary>
    public void RefreshLlmSettings() => RaiseRunLlmChanged();

    /// <summary>Sets the run's model to <paramref name="choice"/>; null (no team) is the default.</summary>
    private void ApplyRunLlmChoice(TeamLlmChoice? choice)
    {
        var applied = choice ?? TeamLlmChoice.Default;
        _runUsesDefaultLlm = applied.UseDefault;
        _runLlmProfile = applied.UseDefault ? null : applied.Profile;
        RaiseRunLlmChanged();
    }

    private void RaiseRunLlmChanged()
    {
        OnPropertiesChanged(nameof(RunUsesDefaultLlm), nameof(RunLlmProfile), nameof(LlmProfiles), nameof(HasLlmProfiles),
            nameof(UseDefaultLlmLabel), nameof(LlmChoices), nameof(SelectedLlmChoice), nameof(RunLlmChoice), nameof(RunLlmLine), nameof(CanRememberLlmForTeam),
            nameof(TeamSettingsLine));
        RememberLlmForTeamCommand.RaiseCanExecuteChanged();
    }

    /// <summary>« Remember for this team »: the team's companion file and launchers take the run's model.</summary>
    private void RememberLlmForTeam()
    {
        if (_team.TeamDirectory is not { Length: > 0 } team || !CanRememberLlmForTeam)
            return;

        var choice = RunLlmChoice;
        string? refusal;
        if (_rememberLlmChoice is { } remember)
        {
            refusal = remember(team, choice);
        }
        else
        {
            TeamCatalog.SaveLlmChoice(team, choice);
            refusal = null;
        }

        if (refusal is not null)
        {
            StatusMessage = refusal;
            return;
        }

        _team = TeamCatalog.DescribeTarget(Target.SelectedPath, _declaredMounts());
        StatusMessage = _strings.Format(StudioStringKeys.TeamsLlmSaved, TeamHeadline, RunLlmLabelOf(choice));
        OnPropertiesChanged(nameof(TeamMetaLine));
        RaiseRunLlmChanged();
    }

    /// <summary>The model <paramref name="choice"/> names, in the words of the card.</summary>
    private string RunLlmLabelOf(TeamLlmChoice choice)
    {
        if (_modelSettings is null)
            return choice.Profile ?? _strings[StudioStringKeys.TeamsLlmNoDefault];

        // The team's choice from now on: a follower takes the default of its own settings file (D7).
        var settings = _modelSettings();
        return TeamSettingStanding.Of(choice.UseDefault ? null : choice.Profile, settings)
            .LlmLabel(_strings, settings, ownSettingsFile: choice.UseDefault && TeamSettingsPath is { Length: > 0 });
    }

    /// <summary>
    /// The team's folders that no settings entry allows and that do not live inside the team
    /// itself — spelled as the virtual paths the agents address (ADR-008).
    /// <para>
    /// A team's own <c>/output</c> and <c>/input</c> are created inside the team at adoption and
    /// are never declared; they are the team's plumbing, not a reach outside the allow-list.
    /// Counting them here would make every adopted team unlaunchable.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> UndeclaredTeamFolders =>
        DeclaredMounts.BlockingFolders(_team.Mounts, _declaredMounts(), TeamDirectory());

    /// <summary>
    /// Whether the run is refused. The Settings > Allowed folders screen is the list of what this
    /// machine allows: a team reaching outside it does not start, it says which folder and why.
    /// Discovering that from a run that failed halfway is the outcome this replaces.
    /// </summary>
    public bool IsBlockedByUndeclaredFolders => UndeclaredTeamFolders.Count > 0;

    /// <summary>The refusal, naming the folders and the two ways out.</summary>
    public string UndeclaredFoldersMessage =>
        IsBlockedByUndeclaredFolders
            ? _strings.Format(
                StudioStringKeys.RunBlockedUndeclared,
                string.Join(", ", UndeclaredTeamFolders))
            : "";

    /// <summary>
    /// The folder declarations the team names by id that this machine does not have (VFS-90,
    /// D-06): a team imported from elsewhere, or an entry removed from the settings since.
    /// </summary>
    public IReadOnlyList<string> UnknownTeamMountIds => Mounts.UnknownTeamMountIds;

    /// <summary>Whether the run is refused because the team refers to a declaration missing here.</summary>
    public bool IsBlockedByUnknownMountIds => UnknownTeamMountIds.Count > 0;

    /// <summary>The refusal, naming the ids and the two ways out.</summary>
    public string UnknownMountIdsMessage =>
        IsBlockedByUnknownMountIds
            ? _strings.Format(
                StudioStringKeys.RunBlockedUnknownMountId,
                string.Join(", ", UnknownTeamMountIds))
            : "";

    /// <summary>
    /// The agents of the team the engine will refuse to start with the mounts of this run: a
    /// <c>filesystem:</c> scope (#349) or a working root the folders do not grant. Judged by the
    /// engine's own rule (<see cref="FileSystemScopePreflight"/>), so the refusal comes before the run
    /// rather than from its first task.
    /// </summary>
    public IReadOnlyList<FileSystemScopeRefusal> FileSystemScopeRefusals => _fileSystemScopeRefusals;

    /// <summary>
    /// The refusals that stop the run for sure: a malformed declaration, refused as the crew loads, and
    /// the scope of an agent the run starts whatever its course, when the mounts shown are those the
    /// run will have.
    /// </summary>
    private IEnumerable<FileSystemScopeRefusal> BlockingScopeRefusals =>
        _fileSystemScopeRefusals.Where(IsBlocking);

    private bool IsBlocking(FileSystemScopeRefusal refusal) =>
        refusal.Kind == FileSystemScopeRefusalKind.Load
        || (refusal.Kind == FileSystemScopeRefusalKind.AtStart && _settingsMountsKnown);

    /// <summary>Whether the run is refused because an agent's scope does not fit its mounts.</summary>
    public bool IsBlockedByFileSystemScope => BlockingScopeRefusals.Any();

    /// <summary>The refusal, quoting the engine's reasons and the two ways out.</summary>
    public string FileSystemScopeMessage =>
        IsBlockedByFileSystemScope
            ? _strings.Format(
                StudioStringKeys.RunBlockedFileSystemScope,
                string.Join(" ", BlockingScopeRefusals.Select(refusal => refusal.Reason)))
            : "";

    /// <summary>
    /// Whether an agent's scope does not fit the mounts shown, without stopping the run for sure: the
    /// run may never start the agent, or the settings the CLI reads may mount what it asks.
    /// </summary>
    public bool HasFileSystemScopeWarning => _fileSystemScopeRefusals.Any(refusal => !IsBlocking(refusal));

    /// <summary>The warning, quoting the engine's reasons.</summary>
    public string FileSystemScopeWarning =>
        HasFileSystemScopeWarning
            ? _strings.Format(
                StudioStringKeys.RunWarnFileSystemScope,
                string.Join(" ", _fileSystemScopeRefusals.Where(refusal => !IsBlocking(refusal)).Select(refusal => refusal.Reason)))
            : "";

    /// <summary>
    /// Whether a task's deliverable may never be written (Orkeon #392, <see cref="CrewDeliverableWriters"/>):
    /// none of the agents that may run it holds a tool that writes the file, or it is a decision task,
    /// which writes no file. The engine starts the run all the same, and the task counts as done whether
    /// or not the file was written; an Orkeon 1.2 CLI warns of it at load too. A warning, never a block.
    /// </summary>
    public bool HasDeliverableWriterWarning => _deliverablesWithoutWriter.Count > 0;

    /// <summary>The warning, then one line per task: the task, who cannot write the file, the file and the fix.</summary>
    public string DeliverableWriterWarning =>
        HasDeliverableWriterWarning
            ? string.Join('\n', _deliverablesWithoutWriter
                .Select(missing => "\u2022 " + DeliverableWriterLine(missing))
                .Prepend(_strings[StudioStringKeys.RunWarnDeliverableNoWriter]))
            : "";

    /// <summary>
    /// One task of <see cref="DeliverableWriterWarning"/>: a decision's line, or who cannot write the file
    /// then the fix: the tool of its format when one exists, and <c>source: final_message</c> for a text
    /// file or a text format for a binary one.
    /// </summary>
    private string DeliverableWriterLine(CrewDeliverableWithoutWriter missing)
    {
        if (missing.Kind == CrewDeliverableFindingKind.DecisionTask)
            return _strings.Format(StudioStringKeys.RunWarnDeliverableDecision, missing.Task, missing.Path);

        var who = missing.Agents.Count == 1
            ? _strings.Format(StudioStringKeys.RunWarnDeliverableNoWriterItem, missing.Task, missing.Agents[0], missing.Path)
            : _strings.Format(
                StudioStringKeys.RunWarnDeliverableNoWriterAgents,
                missing.Task,
                string.Join(", ", missing.Agents.Select(agent => _strings.Format(StudioStringKeys.RunWarnDeliverableQuotedAgent, agent))),
                missing.Path);
        var fix = (missing.IsBinary, missing.PreferredWriter) switch
        {
            (false, var tool) => _strings.Format(StudioStringKeys.RunWarnDeliverableFixText, tool ?? "file_write"),
            (true, { } tool) => _strings.Format(StudioStringKeys.RunWarnDeliverableFixBinary, tool),
            (true, null) => _strings[StudioStringKeys.RunWarnDeliverableFixBinaryNoTool],
        };
        return who + " " + fix;
    }

    /// <summary>
    /// Whether the crew asks anything of the decision models (System One): a decision task, an agent
    /// or a task holding <c>system_one_decide</c>, or something System One picks
    /// (<see cref="CrewDecisionUsage"/>). The option
    /// « Use the decision models » is offered only then.
    /// </summary>
    public bool UsesDecisionModels => _decisionUsage.Mode != CrewDecisionMode.None;

    /// <summary>
    /// Whether the launch is refused on the decision models (<see cref="DecisionPreflight"/>): the crew
    /// cannot run without them and the run goes without them, or the file the run reads names a provider
    /// the engine refuses. A check stays offered on the first: it loads the crew and reports what a run
    /// would refuse as a warning.
    /// </summary>
    public bool IsBlockedByDecision => _decisionFinding?.BlocksLaunch == true;

    /// <summary>Why the launch is refused on the decision models, and what to change.</summary>
    public string DecisionMessage => IsBlockedByDecision ? DescribeDecision(_decisionFinding!) : "";

    /// <summary>
    /// Whether the decision models may fail the run, a warning that never blocks: the settings the CLI
    /// picks are not known while the crew needs the models and the machine's file does not give them,
    /// or names a provider the engine refuses; the provider has no key; or a holder of
    /// <c>system_one_decide</c> runs with the circuit off on a lenient runner, which withholds the tool.
    /// </summary>
    public bool HasDecisionWarning =>
        _decisionFinding is
        {
            BlocksLaunch: false,
            Kind: DecisionPreflightKind.RequiredUnknownSettings or DecisionPreflightKind.ProviderRefused
                or DecisionPreflightKind.ProviderKeyMissing or DecisionPreflightKind.ToolWithheld
                or DecisionPreflightKind.RouteWithoutLadder,
        };

    /// <summary>The warning of <see cref="HasDecisionWarning"/>.</summary>
    public string DecisionWarning => HasDecisionWarning ? DescribeDecision(_decisionFinding!) : "";

    /// <summary>
    /// Whether the run goes without the decision models on a crew that runs either way: each decision
    /// task is answered by its relay, answers from its fallback, or lets its <c>otherwise</c> task run,
    /// and what System One picks falls back. Information only.
    /// </summary>
    public bool HasDecisionInfo => _decisionFinding is { Kind: DecisionPreflightKind.OptionalWithoutModel };

    /// <summary>The information of <see cref="HasDecisionInfo"/>, one line per decision task.</summary>
    public string DecisionInfo => HasDecisionInfo ? DescribeDecision(_decisionFinding!) : "";

    /// <summary>
    /// Whether the information alert offers the decision models settings: not when the run's own box is
    /// all that turns them off, which the settings cannot change.
    /// </summary>
    public bool ShowsDecisionSettingsAction =>
        _decisionFinding is { Kind: DecisionPreflightKind.OptionalWithoutModel } finding
        && finding.Reasons.Any(reason => reason != DecisionOffReason.RunSwitch);

    /// <summary>
    /// Judges the decision models again, on the settings as they are now: the shell calls it when the
    /// decision models settings change, a key stored included.
    /// </summary>
    public void RefreshDecision() => RefreshDecisionVerdict();

    /// <summary>
    /// Judges the decision models again (<see cref="DecisionPreflight"/>) on what is known now: the crew
    /// read last, the circuit of the file the run reads (or of the machine's, while the CLI picks it),
    /// and the run's own switch. Tells the alerts and the commands.
    /// </summary>
    private void RefreshDecisionVerdict()
    {
        var status = _settingsDecisionKnown
            ? _settingsDecisionDocument?.Decision.Resolve(_keyStore.Peek)
            : _machineDecisionStatus();
        // The box is offered only to a crew that asks the models; hidden, it sends no --no-decision.
        Options.OffersDecisionModels = UsesDecisionModels;
        var finding = DecisionPreflight.Check(_decisionUsage, status, _settingsDecisionKnown, Options.SwitchesDecisionOff);
        _decisionFinding = finding;
        OnPropertiesChanged(nameof(UsesDecisionModels), nameof(IsBlockedByDecision), nameof(DecisionMessage),
            nameof(HasDecisionWarning), nameof(DecisionWarning), nameof(HasDecisionInfo), nameof(DecisionInfo),
            nameof(ShowsDecisionSettingsAction));
        RunCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The decision verdict, right before a launch: refuses what the commands refuse
    /// (<see cref="CanLaunch"/>, <see cref="CanValidate"/>). True when the launch stops, its reason said.
    /// </summary>
    private bool RefusesDecision(bool validate)
    {
        if (_decisionFinding is not { } finding || !(validate ? finding.BlocksValidate : finding.BlocksLaunch))
            return false;

        StatusMessage = DescribeDecision(finding);
        return true;
    }

    /// <summary>What a decision finding says, in the window's language.</summary>
    private string DescribeDecision(DecisionPreflightFinding finding)
    {
        var reasons = string.Join(" ", finding.Reasons.Select(reason => _strings[reason switch
        {
            DecisionOffReason.RunSwitch => StudioStringKeys.RunDecisionOffRunSwitch,
            DecisionOffReason.SettingsSwitch => StudioStringKeys.RunDecisionOffSettings,
            _ => StudioStringKeys.RunDecisionOffNoProvider,
        }]));
        var provider = finding.Status?.Provider?.DisplayName ?? "";
        switch (finding.Kind)
        {
            case DecisionPreflightKind.ProviderRefused:
                return _strings.Format(
                    finding.SettingsKnown ? StudioStringKeys.RunBlockedDecisionProvider : StudioStringKeys.RunWarnDecisionProvider,
                    finding.Status?.Refusal ?? "");
            case DecisionPreflightKind.RouteWithoutLadder:
                return _strings.Format(
                    finding.SettingsKnown ? StudioStringKeys.RunBlockedDecisionLadder : StudioStringKeys.RunWarnDecisionLadder,
                    Quoted(finding.RoutedHolders),
                    LadderFault(finding.Status));
            case DecisionPreflightKind.RequiredWithoutModel:
                return _strings.Format(StudioStringKeys.RunBlockedDecisionRequired, DecisionNeeds(finding), reasons).TrimEnd();
            case DecisionPreflightKind.RequiredUnknownSettings:
                return _strings.Format(StudioStringKeys.RunWarnDecisionRequiredUnknown, DecisionNeeds(finding), reasons).TrimEnd();
            case DecisionPreflightKind.ProviderKeyMissing:
                return _strings.Format(StudioStringKeys.RunWarnDecisionKeyMissing, provider, finding.Status?.MissingKeyVariable ?? "");
            case DecisionPreflightKind.ToolWithheld:
                return _strings.Format(StudioStringKeys.RunWarnDecisionToolDropped, Quoted(finding.Usage.ToolHolders), reasons).TrimEnd();
            default:
                // Only the tasks that answer without the model are listed: one that has neither a
                // fallback nor an otherwise task makes the finding a refusal, never this information.
                return string.Join('\n', finding.Usage.DecisionTasks
                    .Where(task => task.AnswersWithoutModel)
                    .Select(DecisionFallbackLine)
                    .Concat(finding.Usage.OtherUses.Select(DecisionPickLine))
                    .Select(line => "\u2022 " + line)
                    .Prepend(_strings.Format(StudioStringKeys.RunInfoDecisionFallback, reasons).TrimEnd()));
        }
    }

    /// <summary>What is wrong with the ladder a profile is picked on: none, or the rungs that name no profile.</summary>
    private string LadderFault(DecisionStatus? status) =>
        status is { UnknownRungs.Count: > 0 } unknown
            ? _strings.Format(StudioStringKeys.RunDecisionLadderUnknownRungs, Quoted(unknown.UnknownRungs))
            : _strings[StudioStringKeys.RunDecisionLadderMissing];

    /// <summary>
    /// What the crew needs of the decision models that this run may not give, joined with « ; »: its
    /// own word (<c>decision.mode: required</c>), or, of a crew that states no mode, the holders of
    /// <c>system_one_decide</c> (unless a lenient runner withholds the tool and goes on) and the decision
    /// tasks without an answer of their own.
    /// </summary>
    private string DecisionNeeds(DecisionPreflightFinding finding)
    {
        if (finding.Usage.Declared)
            return _strings[StudioStringKeys.RunDecisionNeedMode];

        var needs = new List<string>();
        if (finding.Usage.ToolHolders.Count > 0 && finding.Status is not { StrictTools: false })
            needs.Add(_strings.Format(StudioStringKeys.RunDecisionNeedTool, Quoted(finding.Usage.ToolHolders)));
        if (finding.Usage.TasksWithoutAnswer is { Count: > 0 } unanswered)
            needs.Add(_strings.Format(StudioStringKeys.RunDecisionNeedAnswer, Quoted(unanswered)));

        return string.Join("; ", needs);
    }

    /// <summary>The names of agents or tasks, each quoted, comma-separated.</summary>
    private string Quoted(IEnumerable<string> names) =>
        string.Join(", ", names.Select(name => _strings.Format(StudioStringKeys.RunWarnDeliverableQuotedAgent, name)));

    /// <summary>
    /// What one decision task answers once the circuit is off: its relay answers, or its fallback answer
    /// to each question stands, or the task its <c>otherwise</c> branch runs.
    /// </summary>
    private string DecisionFallbackLine(CrewDecisionTask task)
    {
        // A task listed here answers without the model (AnswersWithoutModel): one of the three is there.
        if (task.RelayAgent is { } relay)
            return _strings.Format(StudioStringKeys.RunInfoDecisionRelayItem, task.Task, relay);

        if (task.Fallback.Count == 0 && task.OtherwiseTask is { } otherwise)
            return _strings.Format(StudioStringKeys.RunInfoDecisionOtherwiseItem, task.Task, otherwise);

        var answers = string.Join(", ", task.Questions
            .Where(task.Fallback.ContainsKey)
            .Select(question => question + " = " + task.Fallback[question]));
        return _strings.Format(StudioStringKeys.RunInfoDecisionFallbackItem, task.Task, answers);
    }

    /// <summary>What becomes, once the circuit is off, of one thing the crew lets System One pick.</summary>
    private string DecisionPickLine(CrewSystemOneUse use) => _strings.Format(
        use.Kind switch
        {
            CrewSystemOneUseKind.RouteAuto => StudioStringKeys.RunInfoDecisionRouteItem,
            CrewSystemOneUseKind.SkillsAuto => StudioStringKeys.RunInfoDecisionSkillsItem,
            CrewSystemOneUseKind.Prune => StudioStringKeys.RunInfoDecisionPruneItem,
            _ => StudioStringKeys.RunInfoDecisionEffortItem,
        },
        use.Holder);

    /// <summary>
    /// Whether the selected target is an archived team (STUDIO-31, D-07): Studio neither launches
    /// nor tests it — the banner offers to restore it. The CLI, the terminal launcher and the
    /// operating system run it as ever: archiving is Studio's notion.
    /// </summary>
    public bool IsTargetArchived => _team.IsArchived;

    /// <summary>« Archived team — restore it? » while the target is one; empty otherwise.</summary>
    public string ArchivedTargetMessage =>
        IsTargetArchived ? _strings[StudioStringKeys.CommonArchivedTeamRestore] : "";

    /// <summary>« Restore » — the archived target's team goes back among the active teams.</summary>
    public RelayCommand RestoreTargetCommand { get; }

    /// <summary>
    /// Restores the archived team at <paramref name="teamDirectory"/> — the folder whose sidecar
    /// said archived: the banner's target, or the one of a history entry that offered it
    /// (STUDIO-31). Through the shell's seam when one was wired: « My teams » owns the rules (a
    /// busy team is refused, D-09) and refreshes every list; straight through the catalog
    /// otherwise. The outcome is said, a refusal included, and the target is read again.
    /// </summary>
    internal void RestoreTeamAt(string? teamDirectory)
    {
        if (teamDirectory is not { Length: > 0 } directory)
            return;

        string? refusal;
        if (_restoreTeam is not null)
            refusal = _restoreTeam(directory);
        else
            refusal = TeamCatalog.Restore(directory) ? null : _strings[StudioStringKeys.TeamsRestoreRefused];

        StatusMessage = refusal ?? _strings[StudioStringKeys.CommonTeamRestored];
        RefreshTeamDescription();
    }

    /// <summary>Opens the allowed-folders list — the shell lands on the settings' folders tab.</summary>
    public RelayCommand OpenAllowedFoldersCommand { get; }

    /// <summary>Novice empty state: go pick a team.</summary>
    public RelayCommand ChooseTeamCommand { get; }

    /// <summary>Novice empty state: go make one.</summary>
    public RelayCommand CreateTeamCommand { get; }

    /// <summary>Raised by <see cref="OpenAllowedFoldersCommand"/>.</summary>
    public event EventHandler? OpenAllowedFoldersRequested;

    /// <summary>Opens the decision models settings, from an alert of the decision preflight.</summary>
    public RelayCommand OpenDecisionSettingsCommand { get; }

    /// <summary>Raised by <see cref="OpenDecisionSettingsCommand"/>: the shell lands on the decision models tab.</summary>
    public event EventHandler? OpenDecisionSettingsRequested;

    /// <summary>
    /// Raised by the Novice empty state's choose-a-team button — the shell brings My teams
    /// forward. The screen used to name that place and give no way to reach it.
    /// </summary>
    public event EventHandler? ChooseTeamRequested;

    /// <summary>Raised by its create-a-team sibling — the shell opens the wizard.</summary>
    public event EventHandler? CreateTeamRequested;

    /// <summary>The team card only exists once a target resolves.</summary>
    public bool HasTeamCard => Target.IsResolved;

    // The four states the progress card can be in, in the order the user meets them.
    private enum RunCardState
    {
        Idle,
        Running,
        Succeeded,
        Failed,
        Cancelled,
    }

    // Title, badge, tone and button label are four readings of one and the same state, so they
    // are derived from a single verdict rather than from four parallel chains of conditions:
    // a fifth reading cannot drift away from the other four.
    private RunCardState CardState
    {
        get
        {
            if (IsRunning)
                return RunCardState.Running;

            if (!HasResult)
                return RunCardState.Idle;

            return Outcome switch
            {
                RunOutcome.Success => RunCardState.Succeeded,
                // A Stop is the user's own gesture, not a failure of the team.
                RunOutcome.Cancelled => RunCardState.Cancelled,
                _ => RunCardState.Failed,
            };
        }
    }

    /// <summary>The state badge next to the title.</summary>
    public string RunBadgeText
    {
        get
        {
            // What a success leaves to check, both said when both hold: agents that never ran,
            // and the tasks' finding. Of the two task findings the badge says one: files the
            // engine found not written (Orkeon #392) when there are any, a fact, else tasks that
            // ended on their iteration cap, a suspicion. A task cut short that did not write its
            // file is one task with one file to see to, not two findings; the agents' cards and
            // the tasks' table still say every task cut short.
            var skipped = CardState == RunCardState.Succeeded ? AgentCards.Count(card => card.State == "notrun") : 0;
            var missing = CardState == RunCardState.Succeeded ? MissingDeliverableCount : 0;
            var capped = CardState == RunCardState.Succeeded && missing == 0 ? CappedTaskCount : 0;
            return (skipped > 0, missing > 0, capped > 0) switch
            {
                (true, true, _) => _strings.Format(StudioStringKeys.RunBadgeDoneSkippedMissing, skipped, missing),
                (false, true, _) => _strings.Format(StudioStringKeys.RunBadgeDoneMissing, missing),
                (true, false, true) => _strings.Format(StudioStringKeys.RunBadgeDoneSkippedCapped, skipped, capped),
                (true, false, false) => _strings.Format(StudioStringKeys.RunBadgeDoneSkipped, skipped),
                (false, false, true) => _strings.Format(StudioStringKeys.RunBadgeDoneCapped, capped),
                _ => _strings[CardState switch
                {
                    RunCardState.Running => StudioStringKeys.RunBadgeRunning,
                    RunCardState.Idle => StudioStringKeys.RunBadgeIdle,
                    RunCardState.Succeeded => StudioStringKeys.RunBadgeDone,
                    RunCardState.Cancelled => StudioStringKeys.HistStatusCancelled,
                    _ => StudioStringKeys.RunBadgeFailed,
                }],
            };
        }
    }

    /// <summary>
    /// How many tasks the engine counted as succeeded on the tool-free answer given once the
    /// agent's iterations were spent: its own <c>cappedTasks</c> when the run gave one, else
    /// the lines read, each task by its last. The run still exits 0, so the badge keeps
    /// "succeeded" from reading as "everything was written".
    /// </summary>
    private int CappedTaskCount => Progress.Model.CappedTaskCount;

    /// <summary>
    /// How many tasks the engine counted as succeeded without writing (or updating) the file their
    /// deliverable names (Orkeon #392): its own <c>missingDeliverables</c> when the run gave one, else the lines read,
    /// each task by its last. Zero with a CLI older than Orkeon 1.2, which sends neither.
    /// </summary>
    private int MissingDeliverableCount => Progress.Model.MissingDeliverableCount;

    /// <summary>Whether the run's badge pulses: while the process lives (STUDIO-17).</summary>
    public bool IsRunBadgePulsing => CardState == RunCardState.Running;

    /// <summary>Tone key the view maps to colours: idle | running | ok | warn | fail.</summary>
    public string RunBadgeTone => CardState switch
    {
        RunCardState.Running => "running",
        RunCardState.Idle => "idle",
        RunCardState.Succeeded => AgentCards.Any(card => card.State == "notrun") || CappedTaskCount > 0 || MissingDeliverableCount > 0
            ? "warn"
            : "ok",
        RunCardState.Cancelled => "warn",
        _ => "fail",
    };

    /// <summary>
    /// True for the trial launcher of the Launch screen (refonte, lot 4): built without a history
    /// store, it only changes what its primary button says.
    /// </summary>
    public bool IsTrial { get; }

    /// <summary>Launch now / Running… / Relaunch — the mock's single primary button.</summary>
    public string RunButtonLabel =>
        _strings[CardState switch
        {
            RunCardState.Running => StudioStringKeys.RunButtonRunning,
            RunCardState.Idle => IsTrial ? StudioStringKeys.RunButtonTrial : StudioStringKeys.RunButtonLaunch,
            _ => StudioStringKeys.RunButtonRelaunch,
        }];

    /// <summary>Localized banner when the CLI is missing; the raw locator detail stays expert.</summary>
    public string? CliBanner => IsBinaryAvailable ? null : _strings[StudioStringKeys.RunCliMissing];

    /// <summary>Opens the result — one Explorer window per folder the run could write to.</summary>
    public RelayCommand OpenResultCommand { get; }

    /// <summary>Opens the folder the team writes its LLM exchange logs to.</summary>
    public RelayCommand OpenLogsCommand { get; }

    /// <summary>Whether the logs button is shown: while a run is in progress, once it ended, and with a target.</summary>
    public bool ShowsOpenLogs => Target.Target is not null;

    /// <summary>Why <see cref="OpenLogsCommand"/> is disabled; null when it is available.</summary>
    public string? OpenLogsUnavailableReason => LogFolderState().Reason;

    private (string? Folder, string? Reason) LogFolderState()
    {
        if (Target.Target is not { } target)
            return (null, null);

        var launchDirectory = target.WorkingDirectory ?? Environment.CurrentDirectory;
        var folder = MountOverrideSemantics.LlmLogFolderOf(BuildOptions(), launchDirectory);
        if (folder is null)
            return (null, _strings[StudioStringKeys.RunOpenLogsLoggingOff]);

        if (_probedLogFolder is not { } probed || probed.Folder != folder)
            _probedLogFolder = probed = (folder, _directories.Exists(folder));

        return probed.Exists
            ? (folder, null)
            : (null, _strings[StudioStringKeys.RunOpenLogsNotYet]);
    }

    private bool CanOpenLogs() => _shellOpener is not null && LogFolderState().Folder is not null;

    private void OpenLogs()
    {
        if (LogFolderState().Folder is { } folder)
            _shellOpener?.Open(folder);
    }

    /// <summary>A result folder exists once the run finished and a writable mount is known.</summary>
    public bool CanOpenResult => HasResult && _shellOpener is not null && ResultFolders().Count > 0;

    /// <summary>"Open the result", or "Open the {n} result folders" when the run could write to several.</summary>
    public string OpenResultLabel => ResultFolders() is { Count: > 1 } many
        ? _strings.Format(StudioStringKeys.RunOpenResults, many.Count)
        : _strings[StudioStringKeys.RunOpenResult];

    /// <summary>
    /// The physical folders the run could have written to: every mount **in force** with write
    /// rights — the team's own folders and the per-launch ones first, then the declared entries
    /// kept for this run — each folder once. Read from the effective table rather than from the
    /// three raw lists: since VFS-90 a settings entry may be withdrawn for the run (another
    /// entry of its root was selected), and the folder of an entry the run never mounted is not
    /// a result.
    /// </summary>
    internal IReadOnlyList<string> ResultFolders()
    {
        IEnumerable<string> candidates = Mounts.HasAutoInjection
            ? Mounts.EffectiveMounts
                .Where(m => m.IsMounted)
                .OrderBy(m => m.Origin == MountOrigin.Settings ? 1 : 0)
                .Select(m => m.Value)
            : Mounts.TeamMounts
                .Concat(Mounts.LaunchMounts.Mounts.Select(m => m.MountString))
                .Concat(Mounts.SettingsMounts);

        var folders = new List<string>();
        var seen = new HashSet<string>(
            Orkeon.Domain.FileSystem.PhysicalPathContainment.Comparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var mountString in candidates)
        {
            if (MountDefinition.TryParse(mountString, out var mount, out _)
                && mount is { Rights: MountRights.ReadWrite, PhysicalPath.Length: > 0 }
                && seen.Add(MountDefinition.NormalizeFolder(mount.PhysicalPath)))
            {
                folders.Add(mount.PhysicalPath);
            }
        }

        return folders;
    }

    /// <summary>The first folder of <see cref="ResultFolders"/> — where the team's own deliverables land.</summary>
    internal string? ResultFolder()
    {
        var folders = ResultFolders();
        return folders.Count > 0 ? folders[0] : null;
    }

    private void OpenResult()
    {
        // One window per writable folder (owner's request of 2026-09-20): a team writing to
        // /output and /rapports gets both, and a folder the run never mounted gets none.
        foreach (var folder in ResultFolders())
            _shellOpener?.Open(folder);
    }

    /// <summary>
    /// The adopted team's own folder, resolved by <see cref="DeclaredMounts.TeamDirectoryOf"/> —
    /// the screen asks the question, Studio.Core owns the answer.
    /// </summary>
    private string? TeamDirectory() => DeclaredMounts.TeamDirectoryOf(Target.SelectedPath, _directories);

    /// <summary>
    /// Whether one of the team's folders is a real folder outside the team (STUDIO-14, D-12).
    /// <para>
    /// The engine whitelists a <c>--mount</c> base path for the file tools only under
    /// <c>--allow-external-mounts</c>; a team's own <c>./output</c>, resolved under the team
    /// folder — which is the launch's working directory — needs no such flag, a declared folder
    /// elsewhere on the disk does. So the flag follows the sidecar instead of waiting for the
    /// user to find an expert checkbox: it goes on when a team folder reaches outside the team
    /// and off when none does. The checkbox itself stays, for the per-launch mounts.
    /// </para>
    /// </summary>
    private bool ReachesOutsideTheTeam()
    {
        return DeclaredMounts.ReachesOutsideTheTeam(_team.Mounts, TeamDirectory());
    }

    private void RaiseRunStateChanged()
    {
        ForgetLogFolderProbe();
        OnPropertiesChanged(nameof(RunBadgeText), nameof(RunBadgeTone), nameof(IsRunBadgePulsing),
            nameof(RunButtonLabel), nameof(CanOpenResult), nameof(OpenResultLabel));
        OpenResultCommand.RaiseCanExecuteChanged();
        RememberLlmForTeamCommand.RaiseCanExecuteChanged();
        OnPropertiesChanged(nameof(CanRememberLlmForTeam));
        OnPropertiesChanged(nameof(ShowsOpenLogs));
        RefreshOpenLogs();
    }

    private void OnTargetChanged(object? sender, EventArgs e)
    {
        Options.Target = Target.Target;
        _team = TeamCatalog.DescribeTarget(Target.SelectedPath, _declaredMounts());
        _crewLlmDeclarations = CrewLlmDeclarations.CountIn(Target.SelectedPath);
        _crewFileSystems = CrewFileSystemScopes.Of(Target.Target);
        _fileSystemScopeRefusals = JudgeFileSystemScopes();
        _deliverablesWithoutWriter = CrewDeliverableWriters.Of(Target.Target);
        _decisionUsage = CrewDecisionUsage.Of(Target.Target);
        // The file read last was the previous target's run's: until the new one is read, the new team is
        // judged on the machine's file, a verdict that warns and never blocks.
        _settingsDecisionKnown = false;
        _settingsDecisionDocument = null;
        RefreshDecisionVerdict();
        // The settings file of the run is read again with the crew (RefreshPreview, below).
        _settingsMountsSource = null;
        // The run takes the team's model unless changed here (STUDIO-73); a target that is no team, the default.
        ApplyRunLlmChoice(_team.LlmChoice);
        RefreshTeamSettingsPath();
        Mounts.SetTeamMounts(_team.ResolvedMounts);
        Mounts.AllowExternalMounts = ReachesOutsideTheTeam();
        OnPropertiesChanged(nameof(TeamHeadline), nameof(TeamMetaLine), nameof(TeamDescription), nameof(HasTeamCard),
            nameof(UndeclaredTeamFolders), nameof(IsBlockedByUndeclaredFolders), nameof(UndeclaredFoldersMessage),
            nameof(UnknownTeamMountIds), nameof(IsBlockedByUnknownMountIds), nameof(UnknownMountIdsMessage),
            nameof(FileSystemScopeRefusals), nameof(IsBlockedByFileSystemScope), nameof(FileSystemScopeMessage),
            nameof(HasFileSystemScopeWarning), nameof(FileSystemScopeWarning),
            nameof(HasDeliverableWriterWarning), nameof(DeliverableWriterWarning),
            nameof(IsTargetArchived), nameof(ArchivedTargetMessage), nameof(CrewLlmWarning));
        RunCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
        RestoreTargetCommand.RaiseCanExecuteChanged();
        RefreshPreview();
    }

    private void OnInputsChanged(object? sender, EventArgs e) => RefreshPreview();

    // The mounts of the run decide whether the agents' scopes fit (#349): a launch mount that lowers a
    // root's rights refuses the run as surely as the team's own declaration.
    private void OnMountsChanged(object? sender, EventArgs e)
    {
        RefreshFileSystemScopes();
        RefreshPreview();
    }

    /// <summary>The refusals the engine will make with the crew's declarations and the mounts the panel shows.</summary>
    private IReadOnlyList<FileSystemScopeRefusal> JudgeFileSystemScopes() =>
        _crewFileSystems.Count == 0
            ? []
            : FileSystemScopePreflight.Check(_crewFileSystems, Mounts.EffectiveMounts.Select(row => row.Mount).ToList());

    /// <summary>Judges the scopes again and tells the alerts and the commands.</summary>
    private void RefreshFileSystemScopes()
    {
        _fileSystemScopeRefusals = JudgeFileSystemScopes();
        OnPropertiesChanged(nameof(FileSystemScopeRefusals), nameof(IsBlockedByFileSystemScope), nameof(FileSystemScopeMessage),
            nameof(HasFileSystemScopeWarning), nameof(FileSystemScopeWarning));
        RunCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// One click replays: the history panel's button runs the recorded command line, it does not
    /// merely fill the form in and wait for a second click on Run.
    /// </summary>
    private void OnReplayRequested(object? sender, LaunchReplayEventArgs e) =>
        ReplayCommand.Execute(e.Entry);

    /// <summary>« Restore » on a history entry that refused to replay an archived team (STUDIO-31, D-07).</summary>
    private void OnRestoreRequested(object? sender, LaunchReplayEventArgs e) =>
        RestoreTeamAt(TeamCatalog.DescribeTarget(e.Entry.Target).TeamDirectory);

    /// <summary>
    /// Shows a past launch in the form, so the user can see and then adjust what was replayed.
    /// The target is re-detected rather than trusted blindly — the crew may have moved — but the
    /// replay itself runs the recorded arguments whatever this finds.
    /// </summary>
    private void LoadIntoForm(LaunchHistoryEntry entry)
    {
        // Selecting the target resolves the team's settings file for it (STUDIO-62).
        Target.Select(entry.Target);

        if (entry.SettingsPath is not { Length: > 0 } settingsPath)
            return;

        // An entry recorded on the team's own file was not a pin: the form stays automatic, as it
        // was when the entry was recorded, and the team file still applies.
        if (TeamSettingsPath is { Length: > 0 } teamFile
            && string.Equals(settingsPath, teamFile, Orkeon.Domain.FileSystem.PhysicalPathContainment.Comparison))
        {
            // A pin the form still holds from an earlier choice would claim the replay reads it.
            Options.SettingsMode = SettingsSelectionMode.Automatic;
            return;
        }

        Options.SettingsPath = settingsPath;
        Options.SettingsMode = SettingsSelectionMode.ExplicitPath;
    }

    private void RefreshPreview()
    {
        // The settings in force decide which team mounts go on the command line at all, so
        // they are refreshed before the arguments are built. A pin set or cleared changes which
        // file that is, and whether the team's file line still holds (STUDIO-62).
        OnPropertiesChanged(nameof(ResolvedSettingsPath), nameof(TeamSettingsLine), nameof(RunLlmLine));
        PublishSettingsMounts();

        // The mount table's appended rows depend on the mount the runner injects ahead of the
        // --mount arguments, which depends on the target and on whether LLM logging is on.
        Mounts.AutoInjection = Target.Target is { } target
            ? MountAutoInjection.For(target, BuildOptions())
            : null;

        // The builder throws on an option the target's shape cannot carry, so the preview is built
        // from the arguments only once the same validation has passed; otherwise the message list is
        // what tells the user why there is nothing to show.
        var arguments = BuildArguments();
        CommandLinePreview = arguments.Count == 0 ? null : CommandLineDisplay.Format(arguments);

        CheckOptions();
        OnPropertiesChanged(nameof(ShowsOpenLogs));
        ForgetLogFolderProbe();
        RefreshOpenLogs();
    }

    // ── hrm:L08 — how the Command and Run tabs show what the launcher already knows ──
    // (rc.5 harmonisation: B09, B05, D01, D03, D05, B04). Readings only: nothing here reaches the
    // engine, the history or a command.

    /// <summary>
    /// The command as the Command tab shows it (B09): one selectable, wrapping text in which the
    /// line can break only before an option. Inside an option and its values the spaces are
    /// no-break spaces and a word joiner follows each hyphen and path separator, so neither an
    /// option nor a value is ever cut; the plain space before each <c>--</c> stays the one break
    /// opportunity. A copy by selection goes through <see cref="FromNoBreakDisplay"/>, which gives
    /// back exactly <see cref="CommandLinePreview"/>. Empty without a command.
    /// </summary>
    public string CommandLineDisplayText => ToNoBreakDisplay(CommandLinePreview);

    /// <summary>The no-break space that holds an option to its value.</summary>
    public const char NoBreakSpace = '\u00A0';

    /// <summary>The word joiner that forbids a break after a hyphen or a path separator.</summary>
    public const char WordJoiner = '\u2060';

    /// <summary>The display form of a command: see <see cref="CommandLineDisplayText"/>.</summary>
    public static string ToNoBreakDisplay(string? command)
    {
        var segments = SplitAtOptions(command);
        var builder = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            if (builder.Length > 0)
                builder.Append(' ');

            foreach (var c in segment)
            {
                builder.Append(c == ' ' ? NoBreakSpace : c);
                if (c is '-' or '/' or '\\')
                    builder.Append(WordJoiner);
            }
        }

        return builder.ToString();
    }

    /// <summary>Gives back the plain command from (a selection of) its display form.</summary>
    public static string FromNoBreakDisplay(string display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return display.Replace(WordJoiner.ToString(), string.Empty, StringComparison.Ordinal).Replace(NoBreakSpace, ' ');
    }

    /// <summary>Whether the technical journal is unfolded (R16): open by default, folded by its own button.</summary>
    public bool IsJournalOpen
    {
        get => _isJournalOpen;
        set => SetProperty(ref _isJournalOpen, value);
    }

    /// <summary>
    /// Whether the run pane says « Run finished · exit code N » under its title (D01, R18): once a
    /// run ended. The engine's own sentence stays in the journal and in the tooltip.
    /// </summary>
    public bool ShowsFinishedSubtitle => HasResult && !IsRunning;

    /// <summary>
    /// Whether the verdict of the checks shows in the run pane (D03, R19): a «ready to launch» is
    /// stale once the run was launched, so it goes; errors and warnings stay.
    /// </summary>
    public bool ShowsValidationSummary =>
        !(IsRunning || HasResult)
        || ValidationMessages.Any(m => m.IsError || m.Severity == ValidationSeverity.Warning);

    /// <summary>
    /// The agent cards of the run pane (D03, D05): one per agent of <see cref="AgentLanes"/>, two
    /// lanes that name the same agent but for case and accents (a lowercase, unaccented role in the files,
    /// its accented name in the run) read as one card that follows the run's state; the card shows a
    /// display name and, under it, the identifier the files give. Once a run ended, an agent it
    /// never started reads «not run» (state <c>notrun</c>), never «waiting».
    /// </summary>
    public ObservableCollection<RunAgentCard> AgentCards { get; } = [];

    /// <summary>Splits a display command at each <c>" --"</c> outside double quotes.</summary>
    public static IReadOnlyList<string> SplitAtOptions(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var segments = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i < command.Length; i++)
        {
            if (command[i] == '"')
                quoted = !quoted;
            else if (!quoted && command[i] == ' ' && string.CompareOrdinal(command, i + 1, "--", 0, 2) == 0 && i > start)
            {
                segments.Add(command[start..i].Trim());
                start = i + 1;
            }
        }

        segments.Add(command[start..].Trim());
        return segments.Where(segment => segment.Length > 0).ToList();
    }

    private void InitializeRunPresentation()
    {
        AgentLanes.CollectionChanged += (_, _) => RebuildAgentCards();
        ValidationMessages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowsValidationSummary));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsRunning) or nameof(HasResult))
            {
                RebuildAgentCards();
                OnPropertiesChanged(nameof(ShowsFinishedSubtitle), nameof(ShowsValidationSummary), nameof(ShowsStatusMessage));
            }
        };
    }

    private bool IsLaunchAnnouncement(string message) =>
        string.Equals(message, _strings[StudioStringKeys.LaunchRunning], StringComparison.Ordinal)
        || string.Equals(message, _strings[StudioStringKeys.LaunchValidating], StringComparison.Ordinal);

    private void RebuildAgentCards()
    {
        var finished = HasResult && !IsRunning;
        var cards = new List<RunAgentCard>();
        foreach (var lane in AgentLanes)
        {
            var index = cards.FindIndex(card => SameAgent(card.Identifier, lane.Name));
            if (index < 0)
            {
                cards.Add(CardOf(lane, lane.Name, finished));
                continue;
            }

            // The run's spelling names the agent; the files' spelling stays its identifier. The
            // lane the run reported on carries the state.
            var first = cards[index];
            var reported = lane.State == "waiting" && first.State is not ("waiting" or "notrun")
                ? first with { DisplayName = DisplayNameOf(lane.Name) }
                : CardOf(lane, first.Identifier, finished) with { DisplayName = DisplayNameOf(lane.Name) };

            // Two spellings the run itself gave for one agent: a plain success under the second
            // does not clear the work to check that the first left, nor does a cut-short one
            // clear a file the first did not write; a file not written and a task cut short,
            // one under each spelling, are both said, as they are under one.
            if (WarnRank(first.State) > WarnRank(reported.State) && reported.State is "done" or "capped")
            {
                cards[index] = reported with
                {
                    State = first.State,
                    StatusText = first.State == "missing" && reported.State == "capped" ? MissingAndCappedText : first.StatusText,
                };
            }
            else
            {
                // The first spelling's cap holds whether it was the card's state or said beside
                // a file not written.
                var firstCapped = first.State == "capped" || first.StatusText == MissingAndCappedText;
                cards[index] = reported.State == "missing" && firstCapped
                    ? reported with { StatusText = MissingAndCappedText }
                    : reported;
            }
        }

        AgentCards.Clear();
        foreach (var card in cards)
            AgentCards.Add(card with { Note = NoteOf(card) });
        OnPropertiesChanged(nameof(RunBadgeText), nameof(RunBadgeTone), nameof(IsRunBadgePulsing));
    }

    // What the card's status advises, as its tooltip: the file note, the cap note, or both when
    // the card says both.
    private string NoteOf(RunAgentCard card) => card.State switch
    {
        "capped" => _strings[StudioStringKeys.RunProgressCapNote],
        "missing" when card.StatusText == MissingAndCappedText =>
            _strings[StudioStringKeys.RunProgressDeliverableNote] + "\n\n" + _strings[StudioStringKeys.RunProgressCapNote],
        "missing" => _strings[StudioStringKeys.RunProgressDeliverableNote],
        _ => string.Empty,
    };

    // The card of an agent that did not write a file and was also cut short: it says both.
    private string MissingAndCappedText =>
        _strings[StudioStringKeys.RunLaneMissing] + " · " + _strings[StudioStringKeys.RunLaneCapped];

    // How much a success leaves to check: a file not written over a task cut short over a plain success.
    private static int WarnRank(string state) => state switch
    {
        "missing" => 2,
        "capped" => 1,
        _ => 0,
    };

    private static RunAgentCard CardOf(RunAgentLane lane, string identifier, bool finished) => new(
        DisplayNameOf(lane.Name),
        identifier,
        finished && lane.State == "waiting" ? "notrun" : lane.State,
        lane.StatusText,
        lane.Detail);

    private static bool SameAgent(string left, string right) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    // « redacteur » reads « Redacteur », « veille-web » « Veille web »; a name already written
    // as a name is kept.
    private static string DisplayNameOf(string name)
    {
        if (name.Length == 0 || !char.IsLower(name[0]))
            return name;

        var spaced = name.Contains(' ', StringComparison.Ordinal) ? name : name.Replace('-', ' ').Replace('_', ' ');
        return char.ToUpper(spaced[0], CultureInfo.CurrentCulture) + spaced[1..];
    }
}

/// <summary>
/// One agent card of the run pane (hrm:L08, D05): the <paramref name="DisplayName"/> on the card,
/// the <paramref name="Identifier"/> the crew's files give in mono under it — shown only when it
/// differs from the name (<see cref="HasIdentifier"/>) —, and the lane's state:
/// waiting | running | done | capped | missing | failed | notrun. <c>capped</c> is a success one of
/// whose tasks ended on its iteration cap, <c>missing</c> one of whose tasks did not write the file
/// its deliverable names (Orkeon #392): the card says so, in the warning tone.
/// </summary>
public sealed record RunAgentCard(string DisplayName, string Identifier, string State, string StatusText, string Detail)
{
    /// <summary>The advice behind the status, as its tooltip: empty unless the card has work to check.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Whether the identifier says something the name does not.</summary>
    public bool HasIdentifier => !string.Equals(Identifier, DisplayName, StringComparison.Ordinal);
}

/// <summary>
/// The seams <see cref="LaunchTabViewModel"/> is built over: the runner that spawns the CLI, the
/// probes it reads the machine through, the stores it persists to, and the UI services it answers
/// on. Every one is optional — left unset, the tab falls back to the real physical collaborator —
/// and every one has an in-memory double in the tests. Grouped into one record so the tab's
/// constructor stays narrow and so a new seam does not move the existing ones.
/// </summary>
public sealed record LaunchTabDependencies
{
    /// <summary>Spawns the co-installed <c>orkeon</c> CLI; defaults to the current machine's.</summary>
    public OrkeonProcessRunner? ProcessRunner { get; init; }

    /// <summary>Tells what a picked path is: a crew file, a script, a directory.</summary>
    public ITargetProbe? TargetProbe { get; init; }

    /// <summary>Reads the disk tree behind the mount panel; defaults to the physical one.</summary>
    public IDirectoryProbe? Directories { get; init; }

    /// <summary>Opens the file and folder dialogs.</summary>
    public IPathPicker? Picker { get; init; }

    /// <summary>Persists the replayable run history; null keeps the run out of any history.</summary>
    public ILaunchHistoryStore? HistoryStore { get; init; }

    /// <summary>True for the trial launcher of the Launch screen: its primary button says « Launch the trial ».</summary>
    public bool IsTrial { get; init; }

    /// <summary>Reads the appsettings the launch points at; defaults to the physical store.</summary>
    public IAppSettingsStore? SettingsStore { get; init; }

    /// <summary>Marshals back onto the UI thread; defaults to running the callback inline.</summary>
    public IUiDispatcher? Dispatcher { get; init; }

    /// <summary>The localized strings; defaults to the English set.</summary>
    public IStudioStrings? Strings { get; init; }

    /// <summary>The ORKEON_* environment a given target's model profile rides on.</summary>
    /// <remarks>
    /// Given the target, the model the run takes (null: the team's own choice, STUDIO-73) and the
    /// settings file it reads (null: the machine's).
    /// </remarks>
    public Func<string, TeamLlmChoice?, string?, IReadOnlyDictionary<string, string>?>? EnvironmentForTarget { get; init; }

    /// <summary>Opens a result folder in the shell; null hides the open-result button.</summary>
    public IShellOpener? ShellOpener { get; init; }

    /// <summary>Reads the settings' allowed folders live — a snapshot would go stale.</summary>
    public Func<IReadOnlyList<string>>? DeclaredMounts { get; init; }

    /// <summary>
    /// The decision circuit of the machine's settings file as saved (<see cref="DecisionSection.Resolve"/>),
    /// read at each verdict: what the run likely gets while the CLI picks the file itself. Null when it is
    /// not known; the verdict then warns of a crew that needs the decision models.
    /// </summary>
    public Func<DecisionStatus?>? MachineDecisionStatus { get; init; }

    /// <summary>
    /// Where the decision providers' keys are read when a pinned or team file is judged; the process and
    /// user environment when absent.
    /// </summary>
    public IApiKeyStore? KeyStore { get; init; }

    /// <summary>The clipboard behind "copy the command"; in-memory when absent (the tests).</summary>
    public IClipboardService? Clipboard { get; init; }

    /// <summary>The clock a run's elapsed time is read on (STUDIO-34); the system's when absent.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>
    /// The teams directory whose teams a real run stamps with its date (STUDIO-31, D-05); null
    /// stamps nothing — the Test screen's launcher is built without it: a trial is a rehearsal.
    /// </summary>
    public string? TeamsRoot { get; init; }

    /// <summary>
    /// The teams root a team's own settings file is looked up under (STUDIO-62): the workshop's
    /// <c>settings/&lt;slug&gt;/appsettings.json</c> beside it. Left unset it is <see cref="TeamsRoot"/>.
    /// The Test screen's launcher sets it alone: a trial stamps no last run, yet reads the same
    /// model and limits as the real run of the team.
    /// </summary>
    public string? TeamSettingsRoot { get; init; }

    /// <summary>
    /// Restores an archived team, given its folder: null once restored, the refusal otherwise
    /// (STUDIO-31). The shell routes it through « My teams », which owns the rules and refreshes
    /// every list; null restores through the catalog directly.
    /// </summary>
    public Func<string, string?>? RestoreTeam { get; init; }

    /// <summary>
    /// The model settings as Studio shows them (STUDIO-52): the team card says when the setting its
    /// target names is absent from this machine — the default runs in its place. Null names the
    /// setting as recorded.
    /// </summary>
    public Func<ModelProfileSet>? ModelSettings { get; init; }

    /// <summary>
    /// The preparation of a launch (STUDIO-52, decision 4), given the <c>--mount-id</c> values the run
    /// passes and the settings file it pins (null: none): the shell saves the settings when the file
    /// as saved lacks one of them — the runner looks them up there. Null to launch, the reason the
    /// save was refused otherwise: the run does not start. Null wires none: the run starts as it is.
    /// </summary>
    public Func<IReadOnlyList<string>, string?, Task<string?>>? PrepareLaunch { get; init; }

    /// <summary>
    /// « Remember for this team » (STUDIO-73): records the model of a team, given its folder, and
    /// writes its launchers again — null once done, the refusal otherwise. Null records it through
    /// the catalog alone.
    /// </summary>
    public Func<string, TeamLlmChoice, string?>? RememberLlmChoice { get; init; }
}
