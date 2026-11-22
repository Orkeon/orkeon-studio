using System.Collections.ObjectModel;
using System.Globalization;
using Orkeon.Studio.Core.Events;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Run;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Launch;

/// <summary>One finished task, as the progress panel shows it.</summary>
public sealed class RunTaskViewModel
{
    private readonly IStudioStrings _strings;

    private readonly bool _writtenLater;

    /// <summary>Wraps what the run reported about one task.</summary>
    /// <param name="task">The completion.</param>
    /// <param name="strings">The catalogue its notes are read from.</param>
    /// <param name="writtenLater">Whether a later completion of the same task succeeded and wrote its file.</param>
    public RunTaskViewModel(RunTaskProgress task, IStudioStrings? strings = null, bool writtenLater = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        Task = task;
        _strings = strings ?? EnglishStudioStrings.Instance;
        _writtenLater = writtenLater;
    }

    /// <summary>The underlying Core record.</summary>
    public RunTaskProgress Task { get; }

    /// <summary>The agent that ran it, or its identifier when the run named no agent.</summary>
    public string Title => Task.AgentRole ?? Task.TaskId ?? "—";

    /// <summary>Whether it succeeded.</summary>
    public bool Success => Task.Success;

    /// <summary>How long it took, in seconds, for a compact label; "—" when the run did not say.</summary>
    public string Duration => Task.DurationMs is { } durationMs
        ? _strings.Format(
            StudioStringKeys.UsageSeconds,
            (durationMs / 1000.0).ToString("0.0", CultureInfo.CurrentCulture))
        : "—";

    /// <summary>Tokens spent, or an empty label when the run did not say.</summary>
    public string Tokens => Task.Tokens is { } tokens
        ? tokens.ToString("N0", CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>
    /// How many tools the task called, or an empty label when the run did not say. A zero is
    /// shown as a zero: a task meant to write a file and reporting no call is the diagnosis
    /// the owner's second run lacked (STUDIO-17).
    /// </summary>
    public string ToolCalls => Task.ToolCalls is { } calls
        ? _strings.Format(StudioStringKeys.RunProgressToolCalls, calls)
        : string.Empty;

    /// <summary>Why the task was skipped, or an empty label when it was not (or the run did not say).</summary>
    public string SkipReason => Task.Skipped && !string.IsNullOrEmpty(Task.SkipReason)
        ? _strings.Format(StudioStringKeys.RunProgressSkipReason, Task.SkipReason)
        : string.Empty;

    /// <summary>
    /// Whether the task succeeded on the answer its agent gave once its iterations were spent:
    /// the engine counts it a success, and the row says its work may be missing.
    /// </summary>
    public bool EndedOnIterationCap => Task.Success && Task.EndedOnIterationCap;

    /// <summary>What a task cut short by its iteration cap leaves to check, or an empty label.</summary>
    public string CapNote => EndedOnIterationCap ? _strings[StudioStringKeys.RunProgressCapNote] : string.Empty;

    /// <summary>
    /// Whether the task succeeded without writing (or updating) the file its deliverable names
    /// (Orkeon #392): the engine checked it, so the row says it for sure. A task cut short whose
    /// file was written keeps its cap note alone; one that also missed its file carries both. An
    /// attempt a later one of the same task made good (a retry, a loop of a graph, that wrote the
    /// file) says nothing of it: the run counts each task by its last completion, and its advice
    /// to run the team again would be moot. Its cap note, a fact of that attempt, stays.
    /// </summary>
    public bool DeliverableMissing => Task.Success && Task.DeliverableMissing && !_writtenLater;

    /// <summary>What a task that did not write its file leaves to do, or an empty label.</summary>
    public string DeliverableNote => DeliverableMissing ? _strings[StudioStringKeys.RunProgressDeliverableNote] : string.Empty;

    /// <summary>Whether the row's icon is a warning: a success cut short, or one that did not write its file.</summary>
    public bool HasWarning => EndedOnIterationCap || DeliverableMissing;

    /// <summary>"decision", "decision (defaulted)" or "decision (relay)" for a decision task, else an empty label.</summary>
    public string DecisionLabel
    {
        get
        {
            if (!Task.IsDecision)
            {
                return string.Empty;
            }

            var key = Task.Defaulted
                ? StudioStringKeys.RunProgressDecisionDefaulted
                : string.Equals(Task.DecisionSource, Orkeon.Constants.Protocol.DecisionSources.Relay, StringComparison.Ordinal)
                    ? StudioStringKeys.RunProgressDecisionRelay
                    : StudioStringKeys.RunProgressDecision;
            return _strings[key];
        }
    }

    /// <summary>
    /// The answers of a decision task, one line per question: the option chosen (or the score, or the yes
    /// of a yes/no question), how likely it is and how sure the model was, as whole percentages. Empty when
    /// the line carried no answers it could read; the raw JSON stays in <see cref="RunTaskProgress.DecisionAnswers"/>.
    /// </summary>
    public string DecisionDetails => string.Join(
        "\n",
        DecisionAnswerReading.Parse(Task.DecisionAnswers).Select(DescribeAnswer));

    /// <summary>Whether <see cref="DecisionDetails"/> has anything to show.</summary>
    public bool HasDecisionDetails => DecisionDetails.Length > 0;

    private string DescribeAnswer(DecisionAnswerReading answer)
    {
        // A defaulted question with no fallback: the engine writes empty probabilities and a zero
        // confidence, which says nothing of the model. Said as no answer, without figures.
        if (answer is { Chosen: null, Score: null, Likelihood: null })
            return answer.Question + " = " + _strings[StudioStringKeys.RunProgressDecisionNoAnswer];

        var value = answer switch
        {
            { Chosen: { } chosen } => chosen,
            { Score: { } score } => _strings.Format(
                StudioStringKeys.RunProgressDecisionScore, score.ToString("0.##", CultureInfo.CurrentCulture)),
            { Likelihood: not null } => _strings[StudioStringKeys.RunProgressDecisionYes],
            _ => "—",
        };
        var figures = new List<string>(2);
        if (answer.Likelihood is { } likelihood)
            figures.Add(_strings.Format(StudioStringKeys.RunProgressDecisionLikelihood, Percent(likelihood)));
        // A yes/no answer's confidence is the likelihood of its side: said once.
        if (answer.Confidence is { } confidence
            && (answer.Likelihood is not { } said || Percent(said) != Percent(confidence)))
        {
            figures.Add(_strings.Format(StudioStringKeys.RunProgressDecisionConfidence, Percent(confidence)));
        }

        // A pattern with no words in it: "question = value · figure · figure".
        return string.Join(" · ", figures.Prepend(answer.Question + " = " + value));
    }

    private static string Percent(double share) =>
        Math.Round(Math.Clamp(share, 0, 1) * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>One task the run has started and not yet finished, as the progress panel shows it (STUDIO-17).</summary>
public sealed class RunningTaskViewModel
{
    private readonly IStudioStrings _strings;

    /// <summary>Wraps what the run said when it started the task.</summary>
    public RunningTaskViewModel(RunTaskInFlight task, IStudioStrings? strings = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        Task = task;
        _strings = strings ?? EnglishStudioStrings.Instance;
    }

    /// <summary>The underlying Core record.</summary>
    public RunTaskInFlight Task { get; }

    /// <summary>The agent whose turn it is, or the task's identifier when the run named no agent.</summary>
    public string Title => Task.AgentRole ?? Task.TaskId ?? "—";

    /// <summary>"since HH:mm:ss", local time, from the run's own clock; empty when the start carried none.</summary>
    public string Since => Task.StartedAt is { } started
        ? _strings.Format(
            StudioStringKeys.RunProgressTaskSince,
            started.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
        : string.Empty;
}

/// <summary>
/// The progress panel of a watched run (BUS-06): what finished, what it cost, and what the run
/// is asking.
/// <para>
/// The screen stops being a terminal here. The raw log does not disappear — what the stream
/// said stays available — but it drops behind the two things a user actually watches for:
/// whether the work is advancing, and whether it is waiting on them.
/// </para>
/// <para>
/// The folding itself lives in <see cref="RunProgressModel"/>, in Core, so the same reading
/// serves this screen, a terminal, and the tests. This type only projects it into bindable
/// collections.
/// </para>
/// </summary>
public sealed class RunProgressViewModel : ObservableObject
{
    private readonly IStudioStrings _strings;
    private readonly TimeProvider? _time;
    private RunProgressModel _model;
    private Func<string, string, bool>? _answer;
    private Func<string, string, bool>? _reply;
    private string _answerText = string.Empty;
    private string _replyText = string.Empty;

    /// <summary>
    /// Builds the panel over the localization port. <paramref name="timeProvider"/> is the clock
    /// each run's model reads its elapsed time on — the system's when absent, a frozen one in the
    /// tests and the screenshot campaign.
    /// </summary>
    public RunProgressViewModel(IStudioStrings? strings = null, TimeProvider? timeProvider = null)
    {
        _strings = strings ?? EnglishStudioStrings.Instance;
        _time = timeProvider;
        _model = new RunProgressModel(_time);
        _strings.CultureChanged += (_, _) => RaiseLabels();

        AnswerCommand = new RelayCommand(Answer, () => PendingQuestion is not null);
        ChooseCommand = new RelayCommand(parameter => Choose(parameter as string), parameter => parameter is string);
        ReplyCommand = new RelayCommand(Reply, () => PendingAgentRequest is not null && _reply is not null);
    }

    /// <summary>
    /// The whole live state of the watched run (STUDIO-30), for a reader that wants more than
    /// this panel shows — the status bar (STUDIO-34). Replaced on every <see cref="Reset"/>, which
    /// announces the replacement: a reader holding the old model would watch a run that is over.
    /// </summary>
    public RunProgressModel Model => _model;

    /// <summary>Tasks the run has finished, in the order it reported them.</summary>
    public ObservableCollection<RunTaskViewModel> Tasks { get; } = [];

    /// <summary>Tasks the run has started and not yet finished, oldest first (STUDIO-17).</summary>
    public ObservableCollection<RunningTaskViewModel> RunningTasks { get; } = [];

    /// <summary>Whether anything is in progress — gates the running rows.</summary>
    public bool HasRunningTasks => RunningTasks.Count > 0;

    /// <summary>"Tool X running…" while a tool call is open; empty otherwise.</summary>
    public string ActivityLine => _model.ActiveToolName is { } tool
        ? _strings.Format(StudioStringKeys.RunProgressToolActive, tool)
        : string.Empty;

    /// <summary>Whether a tool is at work — gates the activity line.</summary>
    public bool HasActivity => _model.ActiveToolName is not null;

    /// <summary>Sends the typed answer to the waiting question.</summary>
    public RelayCommand AnswerCommand { get; }

    /// <summary>Sends one of the offered choices.</summary>
    public RelayCommand ChooseCommand { get; }

    /// <summary>Sends the typed reply to the agent waiting on this process.</summary>
    public RelayCommand ReplyCommand { get; }

    /// <summary>The question the run is waiting on, or null.</summary>
    public RunQuestion? PendingQuestion => _model.PendingQuestion;

    /// <summary>Whether a question is waiting — the panel's only modal-ish state.</summary>
    public bool IsAsking => PendingQuestion is not null;

    /// <summary>The question's wording.</summary>
    public string QuestionPrompt => PendingQuestion?.Prompt ?? string.Empty;

    /// <summary>The offered choices, empty for a free-text or yes/no question.</summary>
    public IReadOnlyList<string> QuestionChoices => PendingQuestion?.Choices ?? [];

    /// <summary>Whether the question offers a fixed set of answers.</summary>
    public bool IsChoice => PendingQuestion?.InputKind == RunQuestion.Choice;

    /// <summary>Whether the question is a yes/no.</summary>
    public bool IsConfirm => PendingQuestion?.InputKind == RunQuestion.Confirm;

    /// <summary>What the user typed, for a free-text question.</summary>
    public string AnswerText
    {
        get => _answerText;
        set => SetProperty(ref _answerText, value);
    }

    /// <summary>The agent request waiting for a reply, or null.</summary>
    public RunAgentRequest? PendingAgentRequest => _model.PendingAgentRequest;

    /// <summary>Whether an agent is waiting on this process — gates the request panel.</summary>
    public bool HasAgentRequest => PendingAgentRequest is not null;

    /// <summary>The asking agent's hub address, empty when the run did not say.</summary>
    public string AgentRequestFrom => PendingAgentRequest?.From ?? string.Empty;

    /// <summary>The request's payload, as the raw JSON the agent sent.</summary>
    public string AgentRequestPayload => PendingAgentRequest?.Payload ?? string.Empty;

    /// <summary>What the user typed as the reply.</summary>
    public string ReplyText
    {
        get => _replyText;
        set => SetProperty(ref _replyText, value);
    }

    /// <summary>Messages the run's hub relayed here, as one line each, oldest first.</summary>
    public ObservableCollection<string> HubMessages { get; } = [];

    /// <summary>Whether any hub message arrived — gates the hub panel's visibility.</summary>
    public bool HasHubMessages => HubMessages.Count > 0;

    /// <summary>
    /// What the run said of the decision models and did around its agents' model calls, one line each,
    /// oldest first: the circuit, the profiles and thinking levels picked, the skills carried, the
    /// contexts compacted or pruned.
    /// </summary>
    public ObservableCollection<string> AgentNotes { get; } = [];

    /// <summary>Whether any such line arrived: gates the panel's visibility.</summary>
    public bool HasAgentNotes => AgentNotes.Count > 0;

    /// <summary>
    /// How many lines <see cref="AgentNotes"/> keeps: the oldest go first, the circuit line excepted,
    /// which stays the first of the list, every « circuit-off » or « relay » note below it being read
    /// against it.
    /// </summary>
    internal const int MaxAgentNotes = 200;

    private string? _pendingCircuitNote;

    /// <summary>Whether the first line of <see cref="AgentNotes"/> is the run's circuit line, which the cap never evicts.</summary>
    private bool _circuitNotePinned;

    /// <summary>
    /// Puts the circuit line the run stated first in the list, wherever the run was when it stated it:
    /// above the notes that came before it, and in place of the line of an earlier statement.
    /// </summary>
    private void ShowCircuitNote()
    {
        if (_pendingCircuitNote is not { } line)
            return;

        _pendingCircuitNote = null;
        if (_circuitNotePinned)
            AgentNotes[0] = line;
        else
            AgentNotes.Insert(0, line);
        _circuitNotePinned = true;
    }

    /// <summary>The cost line, empty until the meter moves.</summary>
    public string CostSummary => _model.Cost is { } cost
        ? _strings.Format(
            StudioStringKeys.RunProgressCost,
            cost.Tokens.ToString("N0", CultureInfo.CurrentCulture),
            cost.Model ?? "—")
        : string.Empty;

    /// <summary>The model the meter last reported, for the finished run's summary line; null until it does.</summary>
    public string? CostModel => _model.Cost?.Model;

    /// <summary>Wall time of the finished run, as the stream reported it; null before it ends.</summary>
    public long? FinalDurationMs => _model.FinalDurationMs;

    /// <summary>
    /// The one-line state of the run. Deliberately says "nothing reported yet" rather than
    /// implying progress: no news is not the same as going well. A finished run appends
    /// what it cost — tokens, cache hit, duration — when the stream measured it (W-08).
    /// </summary>
    public string Summary
    {
        get
        {
            if (_model.Finished)
            {
                var verdict = _strings[_model.Success == true
                    ? StudioStringKeys.RunProgressSucceeded
                    : StudioStringKeys.RunProgressFailed];
                var chips = UsageMetricsFormatter.Chips(
                    _model.FinalTokens is > 0 ? _model.FinalTokens : null,
                    _model.FinalCacheHitTokens,
                    _model.FinalCacheMissTokens,
                    _model.FinalDurationMs,
                    _strings,
                    CultureInfo.CurrentCulture);
                return chips.Count > 0 ? $"{verdict} {string.Join(" · ", chips)}" : verdict;
            }

            if (Tasks.Count == 0 && RunningTasks.Count == 0)
                return _strings[StudioStringKeys.RunProgressNothingYet];

            // A task in progress is news: "nothing reported yet" would be false once the run
            // has said whose turn it is (STUDIO-17).
            if (RunningTasks.Count > 0)
            {
                return _strings.Format(
                    StudioStringKeys.RunProgressTasksProgress,
                    Tasks.Count,
                    RunningTasks.Count);
            }

            return _strings.Format(
                StudioStringKeys.RunProgressTasksDone,
                Tasks.Count);
        }
    }

    /// <summary>The last error the run reported, or an empty string.</summary>
    public string LastError => _model.LastError is { } error
        ? $"{error.Code}: {error.Message}"
        : string.Empty;

    /// <summary>Whether the run reported an error.</summary>
    public bool HasError => _model.LastError is not null;

    /// <summary>Text generated so far, when the run was asked to stream it.</summary>
    public string GeneratedText => _model.GeneratedText;

    /// <summary>Whether any generated text arrived — gates the stream panel's visibility.</summary>
    public bool HasGeneratedText => _model.GeneratedText.Length > 0;

    /// <summary>
    /// Prepares the panel for a new run and takes the channels it will answer on. A null
    /// channel means "watch only" — the panel then shows questions (or agent requests)
    /// without offering to answer, which is honest, rather than accepting an answer that
    /// goes nowhere.
    /// </summary>
    public void Reset(Func<string, string, bool>? answer, Func<string, string, bool>? reply = null)
    {
        _answer = answer;
        _reply = reply;
        Tasks.Clear();
        RunningTasks.Clear();
        HubMessages.Clear();
        AgentNotes.Clear();
        _pendingCircuitNote = null;
        _circuitNotePinned = false;
        AnswerText = string.Empty;
        ReplyText = string.Empty;
        _model = new RunProgressModel(_time);
        OnPropertyChanged(nameof(Model));
        RaiseLabels();
    }

    /// <summary>
    /// A completion of task <paramref name="taskId"/> succeeded and wrote its file: the rows of its
    /// earlier attempts that said the file missing are replaced by rows that no longer say it.
    /// </summary>
    private void ClearMissingFileOfEarlierAttempts(string taskId)
    {
        for (var index = 0; index < Tasks.Count; index++)
        {
            var row = Tasks[index];
            if (row.DeliverableMissing && string.Equals(row.Task.TaskId, taskId, StringComparison.Ordinal))
                Tasks[index] = new RunTaskViewModel(row.Task, _strings, writtenLater: true);
        }
    }

    /// <summary>
    /// Reads one line of the run's output. Returns true when it was a protocol event, so the
    /// caller knows whether to also show it raw — a line this panel could not read must never
    /// be lost.
    /// </summary>
    public bool TryApply(string line)
    {
        if (!OrkeonEventParser.TryParse(line, out var orkeonEvent))
            return false;

        var before = _model.Tasks.Count;
        var hubBefore = _model.HubMessages.Count;
        var notesBefore = _model.AgentNotes.Count;
        var circuitBefore = _model.DecisionCircuit;
        _model.Apply(orkeonEvent!);

        for (var index = before; index < _model.Tasks.Count; index++)
        {
            var task = _model.Tasks[index];
            if (task.Success && !task.DeliverableMissing && task.TaskId is { Length: > 0 } id)
                ClearMissingFileOfEarlierAttempts(id);
            Tasks.Add(new RunTaskViewModel(task, _strings));
        }

        // The in-flight list is short and changes only on a start or a close: rebuilding it
        // is cheaper to reason about than diffing it.
        if (orkeonEvent!.Kind is RunEventKinds.TaskStarted or RunEventKinds.TaskCompleted or RunEventKinds.RunFinished)
        {
            RunningTasks.Clear();
            foreach (var running in _model.RunningTasks)
                RunningTasks.Add(new RunningTaskViewModel(running, _strings));
        }

        for (var index = hubBefore; index < _model.HubMessages.Count; index++)
            HubMessages.Add(Describe(_model.HubMessages[index]));

        // Every run says what its circuit is, a crew that asks nothing of System One included: the line
        // is shown at once for a crew that states it runs with the models (optional, required), and held
        // for the others until the run does something it explains: a decision task completed, or any
        // note of this panel (a profile or a thinking level picked or not, skills, a compaction, a pruning).
        if (_model.DecisionCircuit is { } circuit && !ReferenceEquals(circuit, circuitBefore))
        {
            _pendingCircuitNote = Describe(circuit);
            if (circuit.Mode is Orkeon.Constants.Protocol.RunDecisionModes.Optional or Orkeon.Constants.Protocol.RunDecisionModes.Required)
                ShowCircuitNote();
        }

        if (_model.AgentNotes.Count > notesBefore || _model.Tasks.Skip(before).Any(task => task.IsDecision))
            ShowCircuitNote();
        for (var index = notesBefore; index < _model.AgentNotes.Count; index++)
            AgentNotes.Add(Describe(_model.AgentNotes[index]));
        // A long tool loop prunes or compacts at each turn: the panel keeps the latest lines, under
        // the circuit line that explains them.
        while (AgentNotes.Count > MaxAgentNotes)
            AgentNotes.RemoveAt(_circuitNotePinned ? 1 : 0);

        // A delta arrives per token, on the UI thread. Re-raising the eleven labels plus the
        // command state for each one would mean thousands of change notifications per
        // response, and generated text is the only thing a delta can move.
        if (string.Equals(orkeonEvent.Kind, RunEventKinds.LlmDelta, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(GeneratedText));
            OnPropertyChanged(nameof(HasGeneratedText));
            return true;
        }

        RaiseLabels();
        return true;
    }

    private void Answer()
    {
        if (PendingQuestion is not { } question)
            return;

        var value = question.InputKind == RunQuestion.Confirm && string.IsNullOrWhiteSpace(AnswerText)
            ? "yes"
            : AnswerText;

        Send(question, value);
    }

    private void Choose(string? choice)
    {
        if (choice is null || PendingQuestion is not { } question)
            return;

        Send(question, choice);
    }

    private void Send(RunQuestion question, string value)
    {
        // A refused write means no child is listening. Clearing the question anyway would
        // pretend the answer landed, so the panel keeps showing it.
        if (_answer?.Invoke(question.CorrelationId, value) != true)
            return;

        AnswerText = string.Empty;
        _model.AnswerAccepted();
        RaiseLabels();
    }

    private void Reply()
    {
        if (PendingAgentRequest is not { } request)
            return;

        // Same refusal rule as Send: a write nobody read must keep the request on screen.
        if (_reply?.Invoke(request.CorrelationId, ReplyText) != true)
            return;

        ReplyText = string.Empty;
        _model.ReplyAccepted();
        RaiseLabels();
    }

    /// <summary>One hub message as the panel lists it: `[topic] from · payload`.</summary>
    internal static string Describe(RunHubMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var topic = message.Topic is { Length: > 0 } t ? $"[{t}] " : string.Empty;
        var from = message.From is { Length: > 0 } f ? $"{f} · " : string.Empty;
        return $"{topic}{from}{message.Payload ?? string.Empty}";
    }

    /// <summary>What the run said of the decision models, as the panel lists it.</summary>
    private string Describe(RunDecisionCircuit circuit)
    {
        var mode = circuit.Mode ?? "\u2014";
        if (circuit.On)
            return _strings.Format(StudioStringKeys.RunNoteDecisionOn, mode);

        // A cause this build does not know is said as the engine wrote it.
        var cause = DecisionCircuitCauseKeys.KeyOf(circuit.Cause) is { } key
            ? _strings[key]
            : circuit.Cause ?? "\u2014";
        // The engine's own sentence (Orkeon #467) follows Studio's, in English as written: it names the
        // variable and the setting the key is read from, which Studio's sentence cannot.
        if (circuit.Detail is { Length: > 0 } detail)
            cause = _strings.Format(StudioStringKeys.RunNoteCauseWithDetail, cause, detail);
        return _strings.Format(StudioStringKeys.RunNoteDecisionOff, mode, cause);
    }

    // The two clamps of a pick (Orkeon #460), read from the engine's own constants: a renamed reason
    // fails the build. The type is experimental (ORKEXP008); only its literals are taken.
#pragma warning disable ORKEXP008
    private const string RouteFloor = Orkeon.Application.Crew.Routing.SystemOnePickReasons.Floor;
    private const string RouteCeiling = Orkeon.Application.Crew.Routing.SystemOnePickReasons.Ceiling;
#pragma warning restore ORKEXP008

    /// <summary>
    /// One thing the run did around an agent's model calls, as the panel lists it. The engine's own
    /// words (a reason, a mode, an outcome) are shown as written: they are what its documentation names.
    /// </summary>
    private string Describe(RunAgentNote note)
    {
        var who = note.AgentRole is { Length: > 0 } role ? role : note.TaskId ?? "\u2014";
        var value = note.Value ?? "\u2014";
        var reason = note.Reason ?? "\u2014";
        var share = note.Probability is { } probability
            ? Math.Round(Math.Clamp(probability, 0, 1) * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
            : "\u2014";
        return note.Kind switch
        {
            // A bound of the ladder the model's pick is held to (Orkeon #460): neither the pick nor a fallback.
            RunAgentNoteKind.Route when note.Reason == RouteFloor => _strings.Format(StudioStringKeys.RunNoteRouteFloor, who, value),
            RunAgentNoteKind.Route when note.Reason == RouteCeiling => _strings.Format(StudioStringKeys.RunNoteRouteCeiling, who, value),
            RunAgentNoteKind.Route => note.Picked
                ? _strings.Format(StudioStringKeys.RunNoteRoutePicked, who, value, share)
                : _strings.Format(StudioStringKeys.RunNoteRouteDefault, who, value, reason),
            RunAgentNoteKind.Effort when note.Previous is { Length: > 0 } previous =>
                _strings.Format(StudioStringKeys.RunNoteEffortRaised, who, previous, value),
            RunAgentNoteKind.Effort => note.Picked
                ? _strings.Format(StudioStringKeys.RunNoteEffortPicked, who, value, share)
                : _strings.Format(StudioStringKeys.RunNoteEffortDefault, who, value, reason),
            RunAgentNoteKind.Skills => note.Names.Count > 0
                ? _strings.Format(StudioStringKeys.RunNoteSkills, who, string.Join(", ", note.Names), note.Reason ?? value)
                : _strings.Format(StudioStringKeys.RunNoteSkillsNone, who, note.Reason ?? value),
            RunAgentNoteKind.Compacted when string.Equals(value, Orkeon.Application.Interfaces.Ports.AgentCompactionOutcomes.Compacted, StringComparison.Ordinal) =>
                _strings.Format(StudioStringKeys.RunNoteCompacted, who, Count(note.TokensBefore), Count(note.TokensAfter)),
            RunAgentNoteKind.Compacted => _strings.Format(StudioStringKeys.RunNoteCompactionFailed, who, value),
            _ => _strings.Format(StudioStringKeys.RunNotePruned, who, Count(note.Count), Count(note.TokensAfter)),
        };

        static string Count(long? count) => count?.ToString("N0", CultureInfo.CurrentCulture) ?? "\u2014";
    }

    private void RaiseLabels()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CostSummary));
        OnPropertyChanged(nameof(CostModel));
        OnPropertyChanged(nameof(PendingQuestion));
        OnPropertyChanged(nameof(IsAsking));
        OnPropertyChanged(nameof(QuestionPrompt));
        OnPropertyChanged(nameof(QuestionChoices));
        OnPropertyChanged(nameof(IsChoice));
        OnPropertyChanged(nameof(IsConfirm));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(GeneratedText));
        OnPropertyChanged(nameof(HasGeneratedText));
        OnPropertyChanged(nameof(PendingAgentRequest));
        OnPropertyChanged(nameof(HasAgentRequest));
        OnPropertyChanged(nameof(AgentRequestFrom));
        OnPropertyChanged(nameof(AgentRequestPayload));
        OnPropertyChanged(nameof(HasHubMessages));
        OnPropertyChanged(nameof(HasAgentNotes));
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(ActivityLine));
        OnPropertyChanged(nameof(HasActivity));
        AnswerCommand.RaiseCanExecuteChanged();
        ReplyCommand.RaiseCanExecuteChanged();
    }
}
