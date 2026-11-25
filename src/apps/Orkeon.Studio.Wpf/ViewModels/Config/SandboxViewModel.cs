using System.ComponentModel;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Decision;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>A text offered for a message: English whatever the interface's language, like the answer expected of it.</summary>
/// <param name="Title">Its name in the list.</param>
/// <param name="Text">What is sent.</param>
public sealed record SandboxSample(string Title, string Text);

/// <summary>
/// The « Test » tab: every declared model and the decision provider under test, a message of the
/// user's own with its reply, and the journal. Nothing is sent without a click.
/// </summary>
public sealed class SandboxViewModel : ObservableObject
{
    private readonly ModelProfilesViewModel _profiles;
    private readonly DecisionSectionViewModel _decision;
    private readonly IApiKeyStore _keyStore;
    private readonly IStudioStrings _strings;
    private readonly Action? _openDecision;
    private ModelTestRowViewModel? _target;
    private SandboxSample? _sample;
    private string _message = Samples[0].Text;
    private string _caseState = SystemOneDecisionProbe.SampleState;
    private string _caseQuestion = SystemOneDecisionProbe.SampleQuestion;
    private string _caseOptions = string.Join(Environment.NewLine, SystemOneDecisionProbe.SampleOptions);
    private string _reply = "";
    private Func<string>? _replyNote;
    private bool _hasReplyFailed;

    internal SandboxViewModel(
        ModelProfilesViewModel profiles,
        DecisionSectionViewModel decision,
        ModelTestSeams seams,
        Action? openDecision = null)
    {
        _profiles = profiles;
        _decision = decision;
        _keyStore = seams.KeyStore ?? new EnvironmentApiKeyStore();
        _strings = seams.Strings ?? EnglishStudioStrings.Instance;
        _openDecision = openDecision;
        _sample = Samples[0];
        Tests = new ModelTestRunner(seams with
        {
            Mask = seams.Mask ?? profiles.MaskKeys,
            ThinkingOverrides = seams.ThinkingOverrides ?? (() => profiles.ModelThinkingOverrides),
        });
        SendCommand = new AsyncRelayCommand(SendAsync, () => _target is not null && !Tests.IsRunning && (IsDecisionTarget || Message.Trim().Length > 0));
        Tests.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelTestRunner.IsRunning))
                SendCommand.RaiseCanExecuteChanged();
            if (e.PropertyName == nameof(ModelTestRunner.Summary))
                OnPropertyChanged(nameof(TabSubtitle));
        };
        _profiles.Profiles.CollectionChanged += (_, _) => Refresh();
        _decision.PropertyChanged += OnDecisionChanged;
        _strings.CultureChanged += (_, _) => OnPropertiesChanged(nameof(ReplyNote), nameof(TabSubtitle), nameof(DecisionNote));
        Refresh();
    }

    /// <summary>The texts offered, in English.</summary>
    public static IReadOnlyList<SandboxSample> Samples { get; } =
    [
        new("Greeting", "Say hello in one sentence."),
        new("Arithmetic", "What is 17 + 25? Answer with the number only."),
        new("Summary", "Summarize this paragraph in one sentence: The harbour opened in 1850 to ship wool. When the railway came, most of the trade left, and the warehouses stood empty for forty years. Today they hold workshops, a school and a market."),
        new("JSON", "Return a JSON object with the keys \"city\" and \"country\" for the capital of Japan. Return the JSON only."),
    ];

    /// <summary>The tests and their journal.</summary>
    public ModelTestRunner Tests { get; }

    /// <summary>What the last run of the tests says, under the tab's title.</summary>
    public string TabSubtitle => Tests.Summary ?? _strings[StudioStringKeys.SandboxIdle];

    /// <summary>Why the decision provider is not in the list; null when it is.</summary>
    public string? DecisionNote => _decision.Status switch
    {
        { Refusal: not null } => _decision.StatusText,
        { Provider: null } => _strings[StudioStringKeys.SandboxNoDecision],
        _ => null,
    };

    /// <summary>What the message goes to.</summary>
    public ModelTestRowViewModel? Target
    {
        get => _target;
        set
        {
            // A null comes from the list rebuilt under its selection, never from a pick.
            if (value is null || !SetProperty(ref _target, value))
                return;

            OnPropertiesChanged(nameof(IsDecisionTarget), nameof(IsModelTarget));
            ShowReply("", null, failed: false);
            SendCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Whether a decision case is asked rather than a message.</summary>
    public bool IsDecisionTarget => _target is { IsDecision: true };

    /// <summary>Whether a message is sent.</summary>
    public bool IsModelTarget => _target is { IsDecision: false };

    /// <summary>The text offered last; picking one replaces the message.</summary>
    public SandboxSample? Sample
    {
        get => _sample;
        set
        {
            if (SetProperty(ref _sample, value) && value is not null)
                Message = value.Text;
        }
    }

    /// <summary>The message.</summary>
    public string Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value ?? ""))
                SendCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The text a decision is asked about.</summary>
    public string CaseState
    {
        get => _caseState;
        set => SetProperty(ref _caseState, value ?? "");
    }

    /// <summary>The yes/no question.</summary>
    public string CaseQuestion
    {
        get => _caseQuestion;
        set => SetProperty(ref _caseQuestion, value ?? "");
    }

    /// <summary>The options of the choice, one per line.</summary>
    public string CaseOptions
    {
        get => _caseOptions;
        set => SetProperty(ref _caseOptions, value ?? "");
    }

    /// <summary>Sends the message, or asks the case.</summary>
    public AsyncRelayCommand SendCommand { get; }

    /// <summary>The reply, whole.</summary>
    public string Reply => _reply;

    /// <summary>Whether there is a reply.</summary>
    public bool HasReply => _reply.Length > 0;

    /// <summary>The time and the tokens, or what went wrong.</summary>
    public string? ReplyNote => _replyNote?.Invoke();

    /// <summary>Whether the last message got no reply.</summary>
    public bool HasReplyFailed => _hasReplyFailed;

    /// <summary>Stops what is in flight: the tab is left.</summary>
    public void StopActivity() => Tests.Stop();

    /// <summary>
    /// Follows the settings: a setting that did not change keeps its row and its verdict, the
    /// decision provider's verdict goes with any change of its section.
    /// </summary>
    internal void Refresh()
    {
        var known = Tests.Rows.ToList();
        var rows = _profiles.Set.Profiles
            .Select(profile => known.Find(row => row.Profile == profile)
                ?? new ModelTestRowViewModel(Tests, profile, _keyStore.Peek, () => _profiles.BeginEdit(profile)))
            .ToList();
        if (_decision.Status is { Refusal: null, Provider: { } provider })
        {
            rows.Add(known.Find(row => row.IsDecision && row.Name == provider.DisplayName) ?? new ModelTestRowViewModel(
                Tests,
                provider.DisplayName,
                () => DecisionProbeRequest.For(_decision.DocumentForCards.Decision, provider, _keyStore.Peek),
                _openDecision));
        }

        foreach (var row in rows)
            row.ForgetStaleVerdict();

        var picked = _target;
        Tests.SetRows(rows);
        _target = rows.Find(row => row == picked)
            ?? rows.Find(row => picked is not null && row.Name == picked.Name && row.IsDecision == picked.IsDecision)
            ?? rows.FirstOrDefault();
        // The reply shown is the one of the target it was asked of.
        if (picked is not null && _target != picked && !Tests.IsRunning)
            ShowReply("", null, failed: false);
        OnPropertiesChanged(nameof(Target), nameof(IsDecisionTarget), nameof(IsModelTarget), nameof(DecisionNote), nameof(TabSubtitle));
        SendCommand.RaiseCanExecuteChanged();
    }

    private void OnDecisionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DecisionSectionViewModel.Status) or nameof(DecisionSectionViewModel.Exists) or "" or null)
            Refresh();
    }

    private Task SendAsync() => Tests.ExclusiveAsync(async token =>
    {
        if (_target is not { } target)
            return;

        ShowReply("", () => _strings[StudioStringKeys.SandboxRunning], failed: false);
        try
        {
            if (target.IsDecision)
                await AskCaseAsync(target, token).ConfigureAwait(true);
            else
                await AskModelAsync(target, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ShowReply("", () => _strings[StudioStringKeys.SandboxStopped], failed: false);
        }
    });

    private async Task AskModelAsync(ModelTestRowViewModel target, CancellationToken token)
    {
        var test = target.Prepare(Message);
        if (test.Request is not { } request)
        {
            Tests.Note(target.Name, test.Refusal.ToString());
            ShowReply("", () => Tests.Describe(test, target.Profile!.BaseUrl), failed: true);
            return;
        }

        var result = await Tests.AskAsync(target.Name, request, token).ConfigureAwait(true);
        if (!result.Succeeded)
            ShowReply(result.FullDetail ?? result.Detail ?? "", () => LlmProbeText.Describe(result, _strings), failed: true);
        else if (string.IsNullOrWhiteSpace(result.ReplyText))
            ShowReply("", () => _strings[StudioStringKeys.SandboxEmptyReply], failed: true);
        else
            ShowReply(result.ReplyText, () => NoteOf(result.Elapsed, result.Usage?.InputTokens, result.Usage?.OutputTokens), failed: false);
    }

    private async Task AskCaseAsync(ModelTestRowViewModel target, CancellationToken token)
    {
        var request = target.PrepareDecision() with
        {
            Case = new DecisionProbeCase(CaseState, CaseQuestion, CaseOptions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)),
        };
        var result = await Tests.AskAsync(target.Name, request, token).ConfigureAwait(true);
        if (result.Failure != DecisionProbeFailure.None)
            ShowReply("", () => Tests.Describe(result), failed: true);
        else
            ShowReply(
                string.Join(Environment.NewLine, result.Checks.Select(check => $"{check.Name}: {check.Answer}")),
                () => NoteOf(result.Elapsed, result.InputTokens, null),
                failed: !result.Succeeded);
    }

    private string NoteOf(TimeSpan elapsed, int? input, int? output) =>
        input is null && output is null
            ? _strings.Format(StudioStringKeys.SandboxReplyTime, ModelTestRunner.Seconds(elapsed))
            : _strings.Format(StudioStringKeys.SandboxReplyUsage, ModelTestRunner.Seconds(elapsed), input ?? 0, output ?? 0);

    private void ShowReply(string reply, Func<string>? note, bool failed)
    {
        _reply = reply;
        _replyNote = note;
        _hasReplyFailed = failed;
        OnPropertiesChanged(nameof(Reply), nameof(HasReply), nameof(ReplyNote), nameof(HasReplyFailed));
    }
}
