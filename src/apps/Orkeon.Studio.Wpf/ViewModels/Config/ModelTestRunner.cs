using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.Decision;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;
using Orkeon.Studio.Wpf.ViewModels.Services;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>Where the test of one model, or of the decision provider, stands.</summary>
public enum ModelTestState
{
    /// <summary>Not tested.</summary>
    Idle,

    /// <summary>Waiting for its turn.</summary>
    Queued,

    /// <summary>Running.</summary>
    Running,

    /// <summary>It answered, as expected.</summary>
    Passed,

    /// <summary>It did not.</summary>
    Failed,

    /// <summary>Nothing was sent, and the message says why.</summary>
    Skipped,

    /// <summary>Stopped by the user.</summary>
    Stopped,
}

/// <summary>What the runner of the tests is built over; each one left null is the machine's own.</summary>
public sealed record ModelTestSeams
{
    /// <summary>Asks the models.</summary>
    public ILlmEndpointProbe? Probe { get; init; }

    /// <summary>Asks the decision provider.</summary>
    public IDecisionProbe? DecisionProbe { get; init; }

    /// <summary>The keys in place.</summary>
    public IApiKeyStore? KeyStore { get; init; }

    /// <summary>The interface's language.</summary>
    public IStudioStrings? Strings { get; init; }

    /// <summary>Where the journal is copied.</summary>
    public IClipboardService? Clipboard { get; init; }

    /// <summary>Asks where the journal is saved.</summary>
    public IPathPicker? Picker { get; init; }

    /// <summary>Stamps the journal.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>Takes the keys in place out of a text that leaves Studio.</summary>
    public Func<string, string>? Mask { get; init; }

    /// <summary>The capabilities the settings declare.</summary>
    public Func<IReadOnlyDictionary<string, ThinkingSupport?>>? ThinkingOverrides { get; init; }

    /// <summary>Writes the journal to the file picked.</summary>
    public Func<string, string, Task>? SaveText { get; init; }
}

/// <summary>One model, or the decision provider, under test.</summary>
public sealed class ModelTestRowViewModel : ObservableObject
{
    private readonly ModelTestRunner _runner;
    private readonly Func<string, string?> _key;
    private readonly Func<DecisionProbeRequest>? _decision;
    private ModelTestState _state;
    private Func<string>? _message;

    internal ModelTestRowViewModel(ModelTestRunner runner, ModelProfile profile, Func<string, string?> key, Action? open = null)
        : this(runner, profile.Name, open)
    {
        Profile = profile;
        _key = key;
    }

    internal ModelTestRowViewModel(ModelTestRunner runner, string name, Func<DecisionProbeRequest> decision, Action? open = null)
        : this(runner, name, open)
    {
        _decision = decision;
    }

    private ModelTestRowViewModel(ModelTestRunner runner, string name, Action? open)
    {
        _runner = runner;
        _key = static _ => null;
        Name = name;
        TestCommand = new AsyncRelayCommand(() => _runner.RunAsync([this]), () => !_runner.IsRunning);
        OpenCommand = new RelayCommand(() => open?.Invoke(), () => open is not null);
        CanOpen = open is not null;
    }

    /// <summary>The setting's name, or the decision provider's.</summary>
    public string Name { get; }

    /// <summary>The setting tested; null for the decision provider.</summary>
    internal ModelProfile? Profile { get; }

    /// <summary>The card and the model, or what the decision provider is.</summary>
    public string Detail => Profile is { } profile ? profile.Summary(_runner.Strings) : "System One";

    /// <summary>Whether this row is the decision provider.</summary>
    public bool IsDecision => _decision is not null;

    /// <summary>Where its test stands.</summary>
    public ModelTestState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>Whether its test is queued or running.</summary>
    public bool IsBusy => _state is ModelTestState.Queued or ModelTestState.Running;

    /// <summary>What the test says, formed as it is read so it follows the language.</summary>
    public string Message => _message?.Invoke() ?? _runner.Strings[_state switch
    {
        ModelTestState.Queued => StudioStringKeys.SandboxQueued,
        ModelTestState.Running => StudioStringKeys.SandboxRunning,
        ModelTestState.Stopped => StudioStringKeys.SandboxStopped,
        _ => StudioStringKeys.SandboxIdle,
    }];

    /// <summary>Whether the setting can be opened from here.</summary>
    public bool CanOpen { get; }

    /// <summary>Tests this row alone.</summary>
    public AsyncRelayCommand TestCommand { get; }

    /// <summary>Opens the setting.</summary>
    public RelayCommand OpenCommand { get; }

    internal ModelTest Prepare(string? prompt = null) => ModelTest.Of(
        Profile!, _key, _runner.ThinkingOverrides, prompt);

    internal DecisionProbeRequest PrepareDecision() => _decision!();

    internal void Show(ModelTestState state, Func<string>? message = null)
    {
        _message = message;
        State = state;
        OnPropertyChanged(nameof(Message));
    }

    internal void RefreshTexts() => OnPropertiesChanged(nameof(Message), nameof(Detail));
}

/// <summary>
/// Runs the tests one after the other, on a click only, and keeps their journal: what the
/// sandbox tab and the last step of the guided setup share.
/// </summary>
public sealed class ModelTestRunner : ObservableObject
{
    private readonly ModelTestSeams _seams;
    private readonly ILlmEndpointProbe _probe;
    private readonly IDecisionProbe _decisionProbe;
    private readonly IClipboardService _clipboard;
    private readonly TestJournal _journal;
    private CancellationTokenSource? _running;
    private bool _isJournalCopied;
    private string? _saveFailure;

    internal ModelTestRunner(ModelTestSeams seams)
    {
        _seams = seams;
        _probe = seams.Probe ?? HttpLlmEndpointProbe.ForCurrentMachine();
        _decisionProbe = seams.DecisionProbe ?? SystemOneDecisionProbe.ForCurrentMachine();
        _clipboard = seams.Clipboard ?? new InMemoryClipboardService();
        Strings = seams.Strings ?? EnglishStudioStrings.Instance;
        _journal = new TestJournal(seams.Clock);
        TestAllCommand = new AsyncRelayCommand(() => RunAsync([.. Rows]), () => Rows.Count > 0 && !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        CopyJournalCommand = new RelayCommand(CopyJournal, () => HasJournal);
        SaveJournalCommand = new AsyncRelayCommand(SaveJournalAsync, () => HasJournal);
        ClearJournalCommand = new RelayCommand(
            () =>
            {
                _journal.Clear();
                JournalChanged();
            },
            () => HasJournal);
        Strings.CultureChanged += (_, _) =>
        {
            foreach (var row in Rows)
                row.RefreshTexts();
            OnPropertiesChanged(nameof(Summary), nameof(SaveError));
        };
    }

    internal IStudioStrings Strings { get; }

    internal IReadOnlyDictionary<string, ThinkingSupport?>? ThinkingOverrides => _seams.ThinkingOverrides?.Invoke();

    /// <summary>What is tested.</summary>
    public ObservableCollection<ModelTestRowViewModel> Rows { get; } = [];

    /// <summary>Whether there is something to test.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Whether a test, or a message, is in flight.</summary>
    public bool IsRunning => _running is not null;

    /// <summary>Tests every row.</summary>
    public AsyncRelayCommand TestAllCommand { get; }

    /// <summary>Stops what is in flight.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Copies the journal.</summary>
    public RelayCommand CopyJournalCommand { get; }

    /// <summary>Saves the journal to a file.</summary>
    public AsyncRelayCommand SaveJournalCommand { get; }

    /// <summary>Empties the journal.</summary>
    public RelayCommand ClearJournalCommand { get; }

    /// <summary>The journal, without a key.</summary>
    public string JournalText => _seams.Mask?.Invoke(_journal.Text) ?? _journal.Text;

    /// <summary>Whether something was journaled.</summary>
    public bool HasJournal => _journal.Text.Length > 0;

    /// <summary>Whether the journal as it stands is on the clipboard.</summary>
    public bool IsJournalCopied
    {
        get => _isJournalCopied;
        private set => SetProperty(ref _isJournalCopied, value);
    }

    /// <summary>Why the journal was not saved.</summary>
    public string? SaveError => _saveFailure is { } cause ? Strings.Format(StudioStringKeys.SandboxSaveFailed, cause) : null;

    /// <summary>« 2 passed, 1 failed, 0 not sent », once something was tested.</summary>
    public string? Summary
    {
        get
        {
            var passed = Rows.Count(row => row.State == ModelTestState.Passed);
            var failed = Rows.Count(row => row.State == ModelTestState.Failed);
            var skipped = Rows.Count(row => row.State == ModelTestState.Skipped);
            return passed + failed + skipped == 0 ? null : Strings.Format(StudioStringKeys.SandboxSummary, passed, failed, skipped);
        }
    }

    /// <summary>Replaces the rows.</summary>
    internal void SetRows(IEnumerable<ModelTestRowViewModel> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);
        OnPropertiesChanged(nameof(HasRows), nameof(Summary));
        RaiseCommands();
    }

    /// <summary>Stops what is in flight, if anything.</summary>
    public void Stop() => _running?.Cancel();

    /// <summary>Tests <paramref name="rows"/> in order; a stop leaves the rest untested.</summary>
    internal Task RunAsync(IReadOnlyList<ModelTestRowViewModel> rows) => ExclusiveAsync(async token =>
    {
        foreach (var row in rows)
            row.Show(ModelTestState.Queued);
        try
        {
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                row.Show(ModelTestState.Running);
                await TestAsync(row, token).ConfigureAwait(true);
                OnPropertyChanged(nameof(Summary));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (var row in rows.Where(row => row.IsBusy))
                row.Show(ModelTestState.Stopped);
            _journal.Note("tests", "stopped");
            JournalChanged();
        }
    });

    /// <summary>Runs <paramref name="work"/> unless something is in flight already.</summary>
    internal async Task ExclusiveAsync(Func<CancellationToken, Task> work)
    {
        if (_running is not null)
            return;

        using var source = new CancellationTokenSource();
        _running = source;
        OnPropertyChanged(nameof(IsRunning));
        RaiseCommands();
        try
        {
            await work(source.Token).ConfigureAwait(true);
        }
        finally
        {
            _running = null;
            OnPropertiesChanged(nameof(IsRunning), nameof(Summary));
            RaiseCommands();
        }
    }

    /// <summary>Asks a model, and journals the answer.</summary>
    [SuppressMessage("Design", "CA1031",
        Justification = "A test must never fault its command: an unexpected failure is the verdict.")]
    internal async Task<LlmProbeResult> AskAsync(string name, LlmProbeRequest request, CancellationToken token)
    {
        LlmProbeResult result;
        try
        {
            result = await _probe.ProbeAsync(request, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = LlmProbeResult.Unreachable(SecretMasker.Apply(ex.Message, [request.ApiKey]));
        }

        _journal.Add(name, request, result);
        JournalChanged();
        return result;
    }

    /// <summary>Asks the decision provider, and journals the answers.</summary>
    [SuppressMessage("Design", "CA1031",
        Justification = "A test must never fault its command: an unexpected failure is the verdict.")]
    internal async Task<DecisionProbeResult> AskAsync(string name, DecisionProbeRequest request, CancellationToken token)
    {
        DecisionProbeResult result;
        try
        {
            result = await _decisionProbe.ProbeAsync(request, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new DecisionProbeResult
            {
                Provider = request.Provider,
                Failure = DecisionProbeFailure.Refused,
                Detail = SecretMasker.Apply(ex.Message, [request.ApiKey]),
            };
        }

        _journal.Add(name, request, result);
        JournalChanged();
        return result;
    }

    /// <summary>Why <paramref name="test"/> is not sent, in the interface's language.</summary>
    internal string Describe(ModelTest test, string? baseUrl) => test.Refusal switch
    {
        ModelTestRefusal.KeyMissing => Strings.Format(StudioStringKeys.SandboxKeyMissing, test.KeyVariable ?? ""),
        ModelTestRefusal.OllamaElsewhere => Strings.Format(
            StudioStringKeys.ProfileOllamaReadElsewhere, StudioEndpointReading.Of(baseUrl)?.ProviderKey ?? ""),
        _ => Strings[StudioStringKeys.SandboxNoModel],
    };

    /// <summary>What a decision provider's answer says, in the interface's language.</summary>
    internal string Describe(DecisionProbeResult result) => result.Failure switch
    {
        DecisionProbeFailure.KeyMissing => Strings.Format(StudioStringKeys.SandboxKeyMissing, result.Detail ?? ""),
        DecisionProbeFailure.AddressInvalid => Strings[StudioStringKeys.SandboxDecisionAddress],
        DecisionProbeFailure.NothingAsked => Strings[StudioStringKeys.SandboxNothingAsked],
        DecisionProbeFailure.Refused => result.Detail ?? "",
        _ when !result.Succeeded => Strings.Format(
            StudioStringKeys.SandboxDecisionWrong,
            string.Join("; ", result.Checks.Where(check => check.Passed == false).Select(check => $"{check.Name}: {check.Answer}"))),
        _ => Strings.Format(StudioStringKeys.SandboxDecisionPassed, Seconds(result.Elapsed)),
    };

    internal static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>Journals something that was not sent.</summary>
    internal void Note(string name, string message)
    {
        _journal.Note(name, "not sent - " + message);
        JournalChanged();
    }

    private async Task TestAsync(ModelTestRowViewModel row, CancellationToken token)
    {
        if (row.IsDecision)
        {
            var asked = row.PrepareDecision();
            var answer = await AskAsync(row.Name, asked, token).ConfigureAwait(true);
            row.Show(
                answer.Succeeded ? ModelTestState.Passed
                : answer.Failure is DecisionProbeFailure.KeyMissing or DecisionProbeFailure.AddressInvalid ? ModelTestState.Skipped
                : ModelTestState.Failed,
                () => Describe(answer));
            return;
        }

        var test = row.Prepare();
        if (test.Request is not { } request)
        {
            Note(row.Name, test.Refusal.ToString());
            row.Show(ModelTestState.Skipped, () => Describe(test, row.Profile!.BaseUrl));
            return;
        }

        var result = await AskAsync(row.Name, request, token).ConfigureAwait(true);
        row.Show(result.Succeeded ? ModelTestState.Passed : ModelTestState.Failed, () => LlmProbeText.Describe(result, Strings));
    }

    private void CopyJournal()
    {
        _clipboard.SetText(JournalText);
        IsJournalCopied = true;
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "Whatever the write throws belongs on the line under the journal.")]
    private async Task SaveJournalAsync()
    {
        var path = (_seams.Picker ?? NullPathPicker.Instance).PickSaveFile(
            Strings[StudioStringKeys.SandboxJournal], "Text|*.txt", "orkeon-studio-tests.txt");
        if (path is null)
            return;

        try
        {
            await (_seams.SaveText ?? TestJournal.SaveAsync)(path, JournalText).ConfigureAwait(true);
            _saveFailure = null;
        }
        catch (Exception ex)
        {
            _saveFailure = ex.Message;
        }

        OnPropertyChanged(nameof(SaveError));
    }

    private void JournalChanged()
    {
        IsJournalCopied = false;
        OnPropertiesChanged(nameof(JournalText), nameof(HasJournal));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        TestAllCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        CopyJournalCommand.RaiseCanExecuteChanged();
        SaveJournalCommand.RaiseCanExecuteChanged();
        ClearJournalCommand.RaiseCanExecuteChanged();
        foreach (var row in Rows)
            row.TestCommand.RaiseCanExecuteChanged();
    }
}
