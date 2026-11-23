using Orkeon.Studio.Wpf.ViewModels.Mvvm;
using Orkeon.Studio.Wpf.ViewModels.Teams;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// Findings of the review of the task list: what a task row may not remove, and the buttons that
/// follow the engine starting and dying.
/// </summary>
public partial class CreateTeamWizardTests
{
    private const string StateTasks =
        """{"crew":{"name":"veille"},"agents":[{"key":"a","role":"Scanner","tools":["file_read"]}],"tasks":[{"key":"collecte","description":"Collecter","expectedOutput":"x","agent":"a"},{"key":"tri","description":"Trier","decision":{"questions":{"q":{"choice":{"x":"X"}}},"state":["collecte"]}},{"key":"suite","description":"Suite","expectedOutput":"y","agent":"a"}],"rationale":"r"}""";

    private const string OneTask =
        """{"crew":{"name":"veille"},"agents":[{"key":"a","role":"Scanner"}],"tasks":[{"key":"seule","description":"Seule","expectedOutput":"x","agent":"a"}],"rationale":"r"}""";

    private static async Task<CreateTeamViewModel> AtTheDryPause(string blueprint) => (await TaskDryPause(blueprint)).Vm;

    private static async Task<(CreateTeamViewModel Vm, Doubles.FakeProcessLauncher Processes)> TaskDryPause(string blueprint)
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange(
        [
            Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/ws/.orkeon/forge/veille","format":"yaml","resumed":false}"""),
            Out($$"""{"v":2,"seq":2,"ts":"t","kind":"blueprint.ready","blueprint":{{blueprint}},"iteration":1}"""),
            Out("""{"v":2,"seq":3,"ts":"t","kind":"session.finished","status":"paused","exitCode":0}"""),
        ]);
        FillStepOne(vm);
        await Compose(vm);
        Assert.True(vm.CanEditAgents);
        return (vm, processes);
    }

    [Fact]
    public async Task A_task_a_decision_decides_on_and_a_decision_task_are_not_removed_and_say_why()
    {
        var vm = await AtTheDryPause(StateTasks);

        Assert.Equal(["collecte", "tri", "suite"], vm.Tasks.Select(task => task.Key));
        Assert.False(vm.Tasks[0].RemoveCommand.CanExecute(null));
        Assert.Equal("Cannot be removed: the decision Trier decides on its output", vm.Tasks[0].RemoveTooltip);
        Assert.False(vm.Tasks[1].RemoveCommand.CanExecute(null));
        Assert.Equal("A decision is changed by talking to the assistant", vm.Tasks[1].RemoveTooltip);
        Assert.True(vm.Tasks[2].RemoveCommand.CanExecute(null));
        Assert.Equal("", vm.Tasks[2].RemoveTooltip);
    }

    [Fact]
    public async Task The_only_task_is_not_removed_neither_from_its_row_nor_from_its_editor()
    {
        var vm = await AtTheDryPause(OneTask);

        var row = Assert.Single(vm.Tasks);
        Assert.False(row.RemoveCommand.CanExecute(null));
        Assert.Equal("Cannot be removed: a team keeps at least one task", row.RemoveTooltip);

        row.EditCommand.Execute(null);
        Assert.True(vm.TaskEditor.IsOpen);
        Assert.False(vm.TaskEditor.CanRemove);
        Assert.False(vm.TaskEditor.RemoveCommand.CanExecute(null));
        Assert.Equal("Cannot be removed: a team keeps at least one task", vm.TaskEditor.RemoveTooltip);
    }

    [Fact]
    public void The_task_editor_refuses_to_remove_a_task_a_decision_decides_on()
    {
        var editor = new TaskEditorViewModel();
        string? applied = null;
        editor.Open(StateTasks, "collecte", [], json => applied = json);

        Assert.False(editor.CanRemove);
        editor.RemoveCommand.Execute(null);
        Assert.Null(applied);

        editor.Open(StateTasks, "suite", [], json => applied = json);
        Assert.True(editor.CanRemove);
    }

    [Fact]
    public async Task The_edit_buttons_follow_the_engine_starting()
    {
        var (vm, processes) = await TaskDryPause(StateTasks);
        var row = vm.Tasks[2];
        Assert.True(row.RemoveCommand.CanExecute(null));
        var commands = new RelayCommand[]
        {
            row.EditCommand, row.MoveUpCommand, row.MoveDownCommand, row.RemoveCommand,
            vm.AddTaskCommand, vm.AddAgentCommand, vm.Agents[0].EditCommand,
        };
        var raised = commands.ToDictionary(command => command, _ => 0);
        foreach (var command in commands)
            command.CanExecuteChanged += (_, _) => raised[command]++;

        bool? running = null;
        var silent = new List<int>();
        bool? rowRemovable = null, taskAddable = null;
        processes.OutputToEmit.Clear();
        processes.WhileRunning = () =>
        {
            running = vm.IsEngineRunning;
            silent.AddRange(Enumerable.Range(0, commands.Length).Where(i => raised[commands[i]] == 0));
            rowRemovable = row.RemoveCommand.CanExecute(null);
            taskAddable = vm.AddTaskCommand.CanExecute(null);
        };
        // A move at the dry pause relaunches the engine (resume --edit --dry), which starts silent.
        row.MoveUpCommand.Execute(null);
        Assert.Equal(2, processes.Requests.Count);

        // The engine is alive: every button gated on the edit point was told, and now refuses.
        Assert.True(running);
        Assert.Empty(silent);
        Assert.False(rowRemovable);
        Assert.False(taskAddable);
    }
}
