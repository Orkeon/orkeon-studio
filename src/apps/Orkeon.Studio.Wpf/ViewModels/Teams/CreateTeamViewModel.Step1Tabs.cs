using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Teams;

/// <summary>
/// Step 1 in four tabs (refonte R14, #22): « Decision models », « Need », « The team's folders »,
/// « Composition instruction ». The decision models come first, but the need opens the step and
/// « Back » on it leads to them; a resumed or reopened session whose mode this machine cannot serve
/// opens on them instead, so their warning is seen (<see cref="OpenStep1WhereAttentionIsNeeded"/>). The flow is the one it was: the folders tab exists
/// from the start (empty until the composition proposes them), and when the engine proposes
/// the folders that tab opens and carries the step's wait point (R13). Each tab has a state —
/// a check, an amber dot, an empty circle — a state subtitle, and its pane's own footer.
/// The states are strings (<c>Empty</c>, <c>Done</c>, <c>Expected</c>), the names of the
/// view's <c>StepTabState</c>: no WPF type here.
/// </summary>
public sealed partial class CreateTeamViewModel
{
    /// <summary>The « Decision models » tab (#22): System One, first of the step.</summary>
    public const int Step1DecisionTab = 0;

    /// <summary>The « Need » tab, the one the step opens on unless a decision needs the user's attention.</summary>
    public const int Step1NeedTab = 1;

    /// <summary>The « The team's folders » tab.</summary>
    public const int Step1FoldersTab = 2;

    /// <summary>The « Composition instruction » tab.</summary>
    public const int Step1NotesTab = 3;

    private const string TabEmpty = "Empty";
    private const string TabDone = "Done";
    private const string TabExpected = "Expected";

    private int _step1TabIndex = Step1NeedTab;

    /// <summary>The tab on screen in step 1.</summary>
    public int Step1TabIndex
    {
        get => _step1TabIndex;
        set
        {
            if (SetProperty(ref _step1TabIndex, Math.Clamp(value, Step1DecisionTab, Step1NotesTab)))
                Step1BackCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>« See the folders → » and « ← Back to the folders ».</summary>
    public RelayCommand ShowFoldersTabCommand { get; private set; } = null!;

    /// <summary>The step's « Back »: the tab before the one on screen.</summary>
    public RelayCommand Step1BackCommand { get; private set; } = null!;

    /// <summary>
    /// The « Decision models » tab's state: the amber warning when the mode shown meets something on
    /// this machine (<see cref="CrewDecisionNotice"/>), a check when the team is meant to use them,
    /// an empty circle when it is not.
    /// </summary>
    public string DecisionTabState
    {
        get
        {
            if (CrewDecisionNotice.Length > 0)
                return TabWarning;

            return ShownCrewDecision == ForgeCrewDecisions.None ? TabEmpty : TabDone;
        }
    }

    /// <summary>The mode shown, in short: « Not used », « When available » or « Always ».</summary>
    public string DecisionTabSubtitle => _strings[ShownCrewDecision switch
    {
        ForgeCrewDecisions.Required => StudioStringKeys.WizardTabDecisionRequired,
        ForgeCrewDecisions.Optional => StudioStringKeys.WizardTabDecisionOptional,
        _ => StudioStringKeys.WizardTabDecisionNone,
    }];

    /// <summary>
    /// The « Need » tab's state: done once the need is typed — amber when the assistant stopped
    /// before the brief was accepted (UX lot 05, 011-012).
    /// </summary>
    public string NeedTabState
    {
        get
        {
            if (_need.Trim().Length == 0)
            {
                return TabEmpty;
            }

            return Chat.WasInterrupted ? TabWarning : TabDone;
        }
    }

    /// <summary>« To describe », « Described », or « Described, 2 questions settled ».</summary>
    public string NeedTabSubtitle
    {
        get
        {
            if (_need.Trim().Length == 0)
                return _strings[StudioStringKeys.WizardTabNeedEmpty];
            if (Chat.WasInterrupted)
                return _strings[StudioStringKeys.WizardTabNeedInterrupted];

            var answers = AnswersGiven;
            return answers == 0
                ? _strings[StudioStringKeys.WizardTabNeedDone]
                : _strings.Format(StudioStringKeys.WizardTabNeedDoneAnswers, answers);
        }
    }

    /// <summary>The answers given in the conversation: what the need tab counts as settled.</summary>
    private int AnswersGiven => Chat.Turns.Count(turn => turn.IsUser);

    /// <summary>Whether the folders were confirmed in this session.</summary>
    private bool FoldersConfirmed => !_model.FoldersPending && _model.Folders.Count > 0;

    /// <summary>
    /// The folders tab's state: the amber dot while they wait for the confirm or for the files
    /// the user drops in them, a check once confirmed.
    /// </summary>
    public string FoldersTabState
    {
        get
        {
            if (_model.FoldersPending || IsFillingFolders)
            {
                return TabExpected;
            }

            return FoldersConfirmed ? TabDone : TabEmpty;
        }
    }

    /// <summary>
    /// « Proposed after the composition », « 3 folders to confirm », « 2 folders · 1 to choose »,
    /// « 2 folders · dropping files », « 3 folders confirmed ».
    /// </summary>
    public string FoldersTabSubtitle
    {
        get
        {
            if (_model.FoldersPending)
            {
                var toChoose = FolderRows.Count(row => row.NeedsChoice);
                return toChoose == 0
                    ? _strings.Format(StudioStringKeys.WizardTabFoldersPending, FolderRows.Count)
                    : _strings.Format(StudioStringKeys.WizardTabFoldersPendingToChoose, FolderRows.Count, toChoose);
            }

            if (IsFillingFolders)
                return _strings.Format(StudioStringKeys.WizardTabFoldersFilling, _model.Folders.Count);
            if (FoldersConfirmed)
                return _strings.Format(StudioStringKeys.WizardTabFoldersConfirmed, _model.Folders.Count);

            return _strings[StudioStringKeys.WizardTabFoldersEmpty];
        }
    }

    /// <summary>Whether the folders tab has nothing to show yet — its pane says when they come.</summary>
    public bool IsFoldersTabEmpty => !_model.FoldersPending && !FoldersConfirmed;

    /// <summary>
    /// The folders pane's footer: how many folders are still to choose and what confirming now
    /// gives them — « 1 folder to choose. You can confirm now: the team will get an empty
    /// « documents » folder at adoption. » — or that each folder has its place. While the user
    /// drops files, it says the assistant reads them once they continue.
    /// </summary>
    public string FoldersFooter
    {
        get
        {
            if (IsFillingFolders)
                return _strings[StudioStringKeys.WizardTabFoldersFillingFooter];
            if (!_model.FoldersPending)
            {
                return _strings[FoldersConfirmed
                    ? StudioStringKeys.WizardTabFoldersConfirmedFooter
                    : StudioStringKeys.WizardTabFoldersEmpty];
            }

            var toChoose = FolderRows.Where(row => row.NeedsChoice).ToList();
            return toChoose.Count switch
            {
                0 => _strings[StudioStringKeys.WizardTabFoldersReadyFooter],
                1 => _strings.Format(StudioStringKeys.WizardTabFoldersOneToChoose, toChoose[0].FolderName),
                _ => _strings.Format(StudioStringKeys.WizardTabFoldersManyToChoose, toChoose.Count),
            };
        }
    }

    /// <summary>The instruction tab's state: optional, so an empty circle until written.</summary>
    public string NotesTabState => ComposeNotes.HasConsigne ? TabDone : TabEmpty;

    /// <summary>« Optional, empty » or « Written ».</summary>
    public string NotesTabSubtitle => _strings[ComposeNotes.HasConsigne
        ? StudioStringKeys.WizardTabNotesDone
        : StudioStringKeys.WizardTabNotesEmpty];

    /// <summary>The instruction pane's footer: « No instruction yet. », or that it is taken as written.</summary>
    public string NotesFooter => _strings[ComposeNotes.HasConsigne
        ? StudioStringKeys.WizardTabNotesFooterTaken
        : StudioStringKeys.WizardTabNotesFooterEmpty];

    /// <summary>
    /// The disabled « Compose the team »'s reason: « Composition under way », or that the engine
    /// was not found (UX lot 05, 012); empty otherwise.
    /// </summary>
    public string ComposeTip
    {
        get
        {
            if (IsEngineRunning)
            {
                return _strings[StudioStringKeys.WizardComposeBusyTip];
            }

            return IsEngineMissing ? _strings[StudioStringKeys.WizardFailureEngineMissing] : string.Empty;
        }
    }

    /// <summary>Whether « Compose the team » carries a reason in its tooltip.</summary>
    public bool HasComposeTip => ComposeTip.Length > 0;

    private void InitializeStep1Tabs()
    {
        ShowFoldersTabCommand = new RelayCommand(() => Step1TabIndex = Step1FoldersTab);
        Step1BackCommand = new RelayCommand(() => Step1TabIndex--, () => _step1TabIndex > Step1DecisionTab);
        ComposeNotes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StepNotesViewModel.HasConsigne))
                OnPropertiesChanged(nameof(NotesTabState), nameof(NotesTabSubtitle), nameof(NotesFooter));
        };
        FolderRows.CollectionChanged += (_, _) => RefreshStep1Tabs();
    }

    /// <summary>
    /// A language switch: the wait point's words — the bubble, the composer's placeholder, the
    /// status bar's « Waiting for you: … » — and the tabs' are read again.
    /// </summary>
    private void OnWaitPointLanguageChanged()
    {
        Chat.SetPendingAction(PendingActionFor(_waitPoint)
            ?? (_resumeAction is not null && Chat.WasInterrupted ? ResumeActionAfterInterruption() : null));
        OnQuestionsLanguageChanged();
        foreach (var held in HeldFolderRows)
            held.OnLanguageChanged();
        OnPropertiesChanged(nameof(WaitPointLabel), nameof(SessionStateLabel), nameof(Step1Hint));
        RefreshStep1Tabs();
        SyncProgress();
    }

    /// <summary>
    /// A session just put on screen (a resume, a reopen) opens step 1 on « Decision models » when
    /// what its mode meets on this machine is to be said (<see cref="CrewDecisionNotice"/>): « the
    /// wizard still opens on « Need » unless a decision needs the user's attention » (#22). Only the
    /// opening tab moves: a tab the session's wait point chose (the folders) stays, and nothing
    /// moves later on, neither a compose nor a change of the decision settings, which leave the
    /// user where they are with the tab's amber state as the cue.
    /// </summary>
    private void OpenStep1WhereAttentionIsNeeded()
    {
        if (_step1TabIndex == Step1NeedTab && CrewDecisionNotice.Length > 0)
            Step1TabIndex = Step1DecisionTab;
    }

    /// <summary>Republishes every tab's state, subtitle and footer.</summary>
    private void RefreshStep1Tabs() => OnPropertiesChanged(
        nameof(DecisionTabState), nameof(DecisionTabSubtitle),
        nameof(NeedTabState), nameof(NeedTabSubtitle),
        nameof(FoldersTabState), nameof(FoldersTabSubtitle), nameof(IsFoldersTabEmpty), nameof(FoldersFooter),
        nameof(NotesTabState), nameof(NotesTabSubtitle), nameof(NotesFooter), nameof(ComposeTip), nameof(HasComposeTip));
}
