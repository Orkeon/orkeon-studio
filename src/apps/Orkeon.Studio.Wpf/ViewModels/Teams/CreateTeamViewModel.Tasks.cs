using System.Collections.ObjectModel;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Teams;

/// <summary>
/// One task of the Composer step's « Tâches » list: what it does, who does it, the file it writes,
/// what it waits for and under which condition, and the buttons that amend it through the same
/// <c>edit</c> as the agent editor. A decision task is shown, never edited here: its questions
/// are the assistant's to change.
/// </summary>
public sealed class WizardTaskRow
{
    internal WizardTaskRow(
        int number,
        BlueprintTaskView task,
        string who,
        string afterLine,
        string whenLine,
        string removeBlockedBy,
        bool isFirst,
        bool isLast,
        Func<bool> canEdit,
        Action<string?> edit,
        Action<string, int> move,
        Action<string> remove)
    {
        Number = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Key = task.Key;
        Description = task.Description;
        Who = who;
        Deliverable = task.Deliverable ?? "";
        AfterLine = afterLine;
        WhenLine = whenLine;
        IsDecision = task.IsDecision;
        RemoveTooltip = removeBlockedBy;
        EditCommand = new RelayCommand(() => edit(task.Key), () => !task.IsDecision && canEdit());
        MoveUpCommand = new RelayCommand(() => move(task.Key, -1), () => !isFirst && canEdit());
        MoveDownCommand = new RelayCommand(() => move(task.Key, 1), () => !isLast && canEdit());
        RemoveCommand = new RelayCommand(() => remove(task.Key), () => removeBlockedBy.Length == 0 && canEdit());
    }

    /// <summary>The task's place in the list, from 1.</summary>
    public string Number { get; }

    /// <summary>The task's key: the expert's mono line.</summary>
    public string Key { get; }

    /// <summary>What the task does.</summary>
    public string Description { get; }

    /// <summary>Who does it: its agent's role, or « Décision » for a decision task.</summary>
    public string Who { get; }

    /// <summary>The file it writes; empty for none.</summary>
    public string Deliverable { get; }

    /// <summary>Whether it writes a file.</summary>
    public bool HasDeliverable => Deliverable.Length > 0;

    /// <summary>« après : … », the tasks it waits for; empty when it waits for none.</summary>
    public string AfterLine { get; }

    /// <summary>Whether it waits for a task.</summary>
    public bool HasAfter => AfterLine.Length > 0;

    /// <summary>« si : … », its condition; empty when it has none.</summary>
    public string WhenLine { get; }

    /// <summary>Whether it runs under a condition.</summary>
    public bool HasWhen => WhenLine.Length > 0;

    /// <summary>Whether it is a decision task: shown, not edited here.</summary>
    public bool IsDecision { get; }

    /// <summary>Why it cannot be removed (another task's condition reads it); empty when it can.</summary>
    public string RemoveTooltip { get; }

    /// <summary>Opens the task editor over it.</summary>
    public RelayCommand EditCommand { get; }

    /// <summary>Moves it one place up.</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Moves it one place down.</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Removes it from the team.</summary>
    public RelayCommand RemoveCommand { get; }
}

public sealed partial class CreateTeamViewModel
{
    /// <summary>The blueprint's tasks, in its order: the Composer step's « Tâches » list.</summary>
    public ObservableCollection<WizardTaskRow> Tasks { get; } = [];

    /// <summary>Whether the blueprint has tasks to list.</summary>
    public bool HasTasks => Tasks.Count > 0;

    /// <summary>The task editor modal.</summary>
    public TaskEditorViewModel TaskEditor { get; private set; } = null!;

    /// <summary>« Ajouter une tâche »: the task editor for a new task.</summary>
    public RelayCommand AddTaskCommand { get; private set; } = null!;

    private void InitializeTasks()
    {
        TaskEditor = new TaskEditorViewModel(_strings);
        AddTaskCommand = new RelayCommand(() => EditTask(null), () => CanEditAgents);
    }

    private void SyncTasks()
    {
        Tasks.Clear();
        var json = _model.BlueprintJson;
        var tasks = BlueprintTasks.Read(json);
        var roles = BlueprintTasks.Agents(json).ToDictionary(agent => agent.Key, agent => agent.Role, StringComparer.Ordinal);
        var descriptions = tasks.ToDictionary(task => task.Key, task => TaskEditorViewModel.Shorten(task.Description), StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var who = task.IsDecision
                ? _strings[StudioStringKeys.RunLaneDecision]
                : task.AgentKey is { } agent ? roles.GetValueOrDefault(agent, agent) : "";
            var after = task.Dependencies.Count == 0
                ? ""
                : _strings.Format(StudioStringKeys.TaskAfter, string.Join(" ; ", task.Dependencies.Select(key => descriptions.GetValueOrDefault(key, key))));
            var when = task.WhenTerms.Count == 0
                ? ""
                : _strings.Format(StudioStringKeys.TaskWhen, string.Join(" ; ", task.WhenTerms.Select(term => $"{term.Answer} = {term.Value}")));
            var blockers = BlueprintTasks.ConditionedOn(json, task.Key);
            var blocked = blockers.Count == 0
                ? ""
                : _strings.Format(StudioStringKeys.TaskRemoveBlocked, string.Join(" ; ", blockers.Select(key => descriptions.GetValueOrDefault(key, key))));
            Tasks.Add(new WizardTaskRow(
                i + 1, task, who, after, when, blocked, i == 0, i == tasks.Count - 1,
                () => CanEditAgents, EditTask, MoveTask, RemoveTask));
        }

        OnPropertiesChanged(nameof(HasTasks));
        AddTaskCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Opens the task editor, over a task or for a new one when null.</summary>
    private void EditTask(string? taskKey)
    {
        if (!CanEditAgents || _model.BlueprintJson is not { } blueprintJson)
            return;

        TaskEditor.Open(blueprintJson, taskKey, [.. TeamMounts], ApplyEditedBlueprint);
    }

    private void MoveTask(string taskKey, int delta)
    {
        if (CanEditAgents && BlueprintTasks.Move(_model.BlueprintJson, taskKey, delta) is { } amended)
            ApplyEditedBlueprint(amended);
    }

    private void RemoveTask(string taskKey)
    {
        if (CanEditAgents
            && BlueprintTasks.ConditionedOn(_model.BlueprintJson, taskKey).Count == 0
            && BlueprintTasks.Remove(_model.BlueprintJson, taskKey) is { } amended)
            ApplyEditedBlueprint(amended);
    }
}
