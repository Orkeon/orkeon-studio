using System.Collections.ObjectModel;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Teams;

/// <summary>One other task of the blueprint the edited task may wait for: a switch of "After".</summary>
public sealed class TaskDependencyOptionViewModel : ObservableObject
{
    private bool _isChecked;

    internal TaskDependencyOptionViewModel(string key, string label, bool isChecked, Action changed)
    {
        Key = key;
        Label = label;
        _isChecked = isChecked;
        Changed = changed;
    }

    /// <summary>The task's key.</summary>
    public string Key { get; }

    /// <summary>What the task does, shortened.</summary>
    public string Label { get; }

    /// <summary>Whether the edited task waits for this one.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                Changed();
        }
    }

    private Action Changed { get; }
}

/// <summary>
/// The task editor modal: "What to do" ↔ the blueprint task's <c>description</c>,
/// "What is expected" ↔ its <c>expectedOutput</c>, who runs it ↔ its <c>agent</c>, the file it
/// writes ↔ its <c>deliverable</c>, the tasks it waits for ↔ its <c>dependencies</c>, its own tools ↔
/// its <c>tools</c>. Save hands the amended blueprint JSON to the wizard, which sends it over the
/// engine's <c>edit</c>, as the agent editor does: the engine re-validates everything, so this
/// modal asks only for what a task cannot be written without. A decision task is not edited here:
/// its questions are the assistant's to change.
/// </summary>
public sealed class TaskEditorViewModel : ObservableObject
{
    /// <summary>The length a dependency's description is cut to on its switch.</summary>
    public const int DependencyLabelLength = 70;

    private readonly IStudioStrings _strings;
    private string _blueprintJson = "";
    private string? _taskKey;
    private Action<string>? _apply;
    private string _description = "";
    private string _expectedOutput = "";
    private string _deliverable = "";
    private BlueprintAgentRef? _agent;
    private string _scopeInfo = "";
    private string _removeBlockedBy = "";
    private bool _isOpen;

    /// <summary>Builds the editor.</summary>
    public TaskEditorViewModel(IStudioStrings? strings = null)
    {
        _strings = strings ?? EnglishStudioStrings.Instance;
        Tools = new ToolPaletteViewModel(_strings);
        SaveCommand = new RelayCommand(Save, () => CanSave);
        RemoveCommand = new RelayCommand(Remove, () => CanRemove);
        CancelCommand = new RelayCommand(Close);
    }

    /// <summary>Whether the modal is showing.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>
    /// Shows the editor over <paramref name="blueprintJson"/>, for the task named by
    /// <paramref name="taskKey"/> or a new one when null; <paramref name="apply"/> receives the
    /// amended blueprint JSON. <paramref name="teamMounts"/> feeds the folders line and the chips'
    /// words, by virtual path, never by the folder on this machine.
    /// </summary>
    public void Open(string blueprintJson, string? taskKey, IReadOnlyList<string> teamMounts, Action<string> apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintJson);
        ArgumentNullException.ThrowIfNull(teamMounts);
        ArgumentNullException.ThrowIfNull(apply);

        _blueprintJson = blueprintJson;
        _apply = apply;
        _scopeInfo = AgentEditorViewModel.DescribeScope(teamMounts, _strings);

        var tasks = BlueprintTasks.Read(blueprintJson);
        var current = taskKey is null ? null : tasks.FirstOrDefault(task => string.Equals(task.Key, taskKey, StringComparison.Ordinal));
        _taskKey = current?.Key;
        _description = current?.Description ?? "";
        _expectedOutput = current?.ExpectedOutput ?? "";
        _deliverable = current?.Deliverable ?? "";

        Agents.Clear();
        foreach (var agent in BlueprintTasks.Agents(blueprintJson))
            Agents.Add(agent);
        _agent = Agents.FirstOrDefault(agent => string.Equals(agent.Key, current?.AgentKey, StringComparison.Ordinal))
            ?? (current is null ? Agents.FirstOrDefault() : null);

        Dependencies.Clear();
        foreach (var task in tasks.Where(task => !string.Equals(task.Key, _taskKey, StringComparison.Ordinal)))
            Dependencies.Add(new TaskDependencyOptionViewModel(
                task.Key,
                Shorten(task.Description is { Length: > 0 } description ? description : task.Key),
                current?.Dependencies.Contains(task.Key, StringComparer.Ordinal) == true,
                OnChanged));

        Tools.Load(
            blueprintJson,
            current?.Tools ?? [],
            AgentEditorViewModel.FirstScope(teamMounts, readWrite: false),
            AgentEditorViewModel.FirstScope(teamMounts, readWrite: true),
            OnChanged);

        _removeBlockedBy = _taskKey is null
            ? ""
            : RemoveBlockedText(BlueprintTasks.RemovalOf(blueprintJson, _taskKey), key => LabelOf(tasks, key), _strings);

        IsOpen = true;
        OnPropertiesChanged(
            nameof(Description), nameof(ExpectedOutput), nameof(Deliverable), nameof(Agent), nameof(ScopeInfo),
            nameof(Title), nameof(IsNew), nameof(SaveLabel), nameof(KeyLine), nameof(HasDependencies),
            nameof(RemoveTooltip), nameof(CanRemove));
        SaveCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>True when the editor adds a task rather than amending one.</summary>
    public bool IsNew => _taskKey is null;

    /// <summary>"Add a task" / "Edit the task".</summary>
    public string Title => _strings[IsNew ? StudioStringKeys.TaskEditorTitleAdd : StudioStringKeys.TaskEditorTitleEdit];

    /// <summary>The add / save button label.</summary>
    public string SaveLabel => _strings[IsNew ? StudioStringKeys.TaskEditorAdd : StudioStringKeys.ActSave];

    /// <summary>What to do: the task's <c>description</c>.</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value))
                OnChanged();
        }
    }

    /// <summary>What is expected: the task's <c>expectedOutput</c>.</summary>
    public string ExpectedOutput
    {
        get => _expectedOutput;
        set
        {
            if (SetProperty(ref _expectedOutput, value))
                OnChanged();
        }
    }

    /// <summary>The file the task writes, by virtual path: its <c>deliverable</c>; blank for none.</summary>
    public string Deliverable
    {
        get => _deliverable;
        set => SetProperty(ref _deliverable, value);
    }

    /// <summary>The agents the task may be given to.</summary>
    public ObservableCollection<BlueprintAgentRef> Agents { get; } = [];

    /// <summary>The agent that runs the task.</summary>
    public BlueprintAgentRef? Agent
    {
        get => _agent;
        set
        {
            if (SetProperty(ref _agent, value))
                OnChanged();
        }
    }

    /// <summary>The other tasks, each a switch: whether this one waits for it.</summary>
    public ObservableCollection<TaskDependencyOptionViewModel> Dependencies { get; } = [];

    /// <summary>Whether there is another task to wait for.</summary>
    public bool HasDependencies => Dependencies.Count > 0;

    /// <summary>The tools the task grants on top of its agent's.</summary>
    public ToolPaletteViewModel Tools { get; }

    /// <summary>The team's folders, informative: where a deliverable may go.</summary>
    public string ScopeInfo => _scopeInfo;

    /// <summary>The expert mono line: <c>id: key · agent: key · after: […]</c>.</summary>
    public string KeyLine =>
        $"id: {_taskKey ?? BlueprintTasks.NewKey(_blueprintJson, _description)} · agent: {_agent?.Key} · after: [{string.Join(", ", CheckedDependencies)}] · tools: [{string.Join(", ", Tools.Selected)}]";

    /// <summary>What a task cannot be written without: what to do, what is expected, and who does it.</summary>
    public bool CanSave => _description.Trim().Length > 0 && _expectedOutput.Trim().Length > 0 && _agent is not null;

    /// <summary>Whether the task may be removed: an existing one <see cref="BlueprintTasks.RemovalOf"/> allows.</summary>
    public bool CanRemove => !IsNew && _removeBlockedBy.Length == 0;

    /// <summary>Why the task cannot be removed; empty when it can.</summary>
    public string RemoveTooltip => _removeBlockedBy;

    /// <summary>Applies the edit and closes.</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>Removes the task from the team; the others stop waiting for it.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Closes without applying.</summary>
    public RelayCommand CancelCommand { get; }

    private IEnumerable<string> CheckedDependencies => Dependencies.Where(option => option.IsChecked).Select(option => option.Key);

    private void OnChanged()
    {
        OnPropertiesChanged(nameof(KeyLine));
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void Save()
    {
        if (!CanSave)
            return;

        var apply = _apply;
        var json = BlueprintTasks.Upsert(_blueprintJson, new BlueprintTaskEdit(
            _taskKey, _description, _expectedOutput, _agent!.Key, [.. CheckedDependencies], _deliverable, Tools.Selected));
        Close();
        if (json is not null)
            apply?.Invoke(json);
    }

    private void Remove()
    {
        if (!CanRemove || _taskKey is not { } key)
            return;

        var apply = _apply;
        var json = BlueprintTasks.Remove(_blueprintJson, key);
        Close();
        if (json is not null)
            apply?.Invoke(json);
    }

    private void Close()
    {
        _apply = null;
        IsOpen = false;
    }

    /// <summary>A task's description cut to <see cref="DependencyLabelLength"/> characters, ellipsis included.</summary>
    internal static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= DependencyLabelLength ? line : string.Concat(line.AsSpan(0, DependencyLabelLength - 1).TrimEnd(), "…");
    }

    /// <summary>
    /// Why a task cannot be removed, for its remove button; empty when it can. <paramref name="label"/>
    /// names a reading task on screen.
    /// </summary>
    internal static string RemoveBlockedText(BlueprintTaskRemoval removal, Func<string, string> label, IStudioStrings strings)
    {
        var readers = string.Join(" ; ", removal.Readers.Select(label));
        return removal.Verdict switch
        {
            BlueprintTaskRemovalVerdict.Allowed or BlueprintTaskRemovalVerdict.Unknown => "",
            BlueprintTaskRemovalVerdict.OnlyTask => strings[StudioStringKeys.TaskRemoveOnly],
            BlueprintTaskRemovalVerdict.DecisionTask => strings[StudioStringKeys.TaskDecisionTip],
            BlueprintTaskRemovalVerdict.ReadByCondition => strings.Format(StudioStringKeys.TaskRemoveBlocked, readers),
            _ => strings.Format(StudioStringKeys.TaskRemoveState, readers),
        };
    }

    private static string LabelOf(IReadOnlyList<BlueprintTaskView> tasks, string key) =>
        tasks.FirstOrDefault(task => string.Equals(task.Key, key, StringComparison.Ordinal)) is { Description.Length: > 0 } task
            ? Shorten(task.Description)
            : key;
}
