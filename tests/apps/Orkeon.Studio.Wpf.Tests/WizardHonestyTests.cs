using System.ComponentModel;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;
using Orkeon.Studio.Wpf.ViewModels.Teams;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// What the wizard is allowed to assert. Every test here stands for a sentence the screen
/// used to make and could not support: a green tick on a failed task, a spinner turning while
/// the engine waits on the user, a proposal card over an empty proposal, and chips the view
/// was never told about.
/// </summary>
public sealed class WizardHonestyTests
{
    private static ProcessOutputLine Out(string json) =>
        ProcessOutputLine.Now(ProcessOutputChannel.StandardOutput, json);

    private const string Blueprint =
        """{"v":2,"seq":5,"ts":"t","kind":"blueprint.ready","iteration":1,"blueprint":{"crew":{"name":"veille","goal":"g"},"agents":[{"key":"a","role":"Lecteur","goal":"lire","tools":["file_read"]}],"tasks":[{"key":"t1","description":"d","agent":"a","deliverable":{"path":"/output/note.md"}}]}}""";

    private static (CreateTeamViewModel Vm, FakeProcessLauncher Processes) Build()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var llm = new LlmSectionViewModel(() => document, () => { });
        var profiles = new ModelProfilesViewModel(new InMemoryModelProfileStore(), llm, probe: new FakeLlmEndpointProbe());
        profiles.CommitEdit(
            new ModelProfile { Name = "Local", Provider = "Ollama", Model = "qwen2.5:14b", BaseUrl = "http://localhost:11434/v1" },
            previousName: null);
        profiles.StudioProfileName = "Local";

        var processes = new FakeProcessLauncher();
        var vm = new CreateTeamViewModel(
            profiles,
            new CreateTeamDependencies
            {
                Client = new ForgeClient(processes, new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
                WorkspaceDirectory = "/ws",
                TeamsRoot = "/teams",
            });
        vm.Need = "une veille documentaire";
        return (vm, processes);
    }

    /// <summary>
    /// The defect the owner photographed: the hint that explains the chips appeared while the
    /// chips did not. HasDerivedMounts was in the notification batch and re-evaluated;
    /// MountRows — the one the list binds — was not, so WPF kept its empty
    /// snapshot until some unrelated gesture happened to refresh it.
    /// </summary>
    [Fact]
    public async Task The_view_is_told_about_the_mounts_the_blueprint_implies()
    {
        var (vm, processes) = Build();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        processes.WhileRunning = () =>
        {
            // Only what is raised AFTER the blueprint counts: ResetProjection refreshes the
            // mount surfaces when the compose starts, which would make a broken notification
            // path look healthy.
            raised.Clear();
            processes.Emit(Out(Blueprint));
        };

        await vm.ComposeCommand.ExecuteAsync();

        Assert.NotEmpty(vm.MountRows);
        Assert.Contains(nameof(vm.MountRows), raised, StringComparer.Ordinal);
        Assert.Contains(nameof(vm.HasDerivedMounts), raised, StringComparer.Ordinal);
    }

    [Fact]
    public async Task No_proposal_card_before_there_is_a_proposal()
    {
        var (vm, processes) = Build();
        var duringCompose = true;
        processes.WhileRunning = () =>
        {
            duringCompose = vm.HasProposal;
            processes.Emit(Out(Blueprint));
        };

        await vm.ComposeCommand.ExecuteAsync();

        Assert.False(duringCompose);   // the stepper reaches step 2 long before the blueprint
        Assert.True(vm.HasProposal);
    }

    /// <summary>
    /// The engine child stays alive while blocked on stdin, so «is it running» cannot mean
    /// «is it working». A spinner turning at the arbitration tells the user to wait for
    /// something that is waiting for them.
    /// </summary>
    [Fact]
    public async Task The_spinner_stops_when_the_engine_is_the_one_waiting()
    {
        var (vm, processes) = Build();
        bool workingWhileAsked = true, waiting = false;
        processes.WhileRunning = () =>
        {
            processes.Emit(Out(
                """{"v":2,"seq":8,"ts":"t","kind":"decision.needed","options":["accept","retry","refine","abort"]}"""));
            workingWhileAsked = vm.IsEngineWorking;
            waiting = vm.IsEngineWaitingOnUser;
        };

        await vm.ComposeCommand.ExecuteAsync();

        Assert.True(waiting);
        Assert.False(workingWhileAsked);
    }

    /// <summary>
    /// RunInProgress is raised by run.started and cleared only by run.finished — which never
    /// arrives when the child dies. The indicator used to stay lit until «Recommencer».
    /// </summary>
    [Fact]
    public async Task A_trial_that_crashed_stops_claiming_to_be_running()
    {
        var (vm, processes) = Build();
        processes.ExitCode = 2;
        processes.WhileRunning = () => processes.Emit(Out(
            """{"v":2,"seq":6,"ts":"t","kind":"run.started","run":1,"target":"/crew"}"""));

        await vm.ComposeCommand.ExecuteAsync();

        Assert.True(vm.RunInProgress);      // the model never heard run.finished — that is true
        Assert.False(vm.TrialInProgress);   // but the screen must not keep spinning over it
    }

    [Fact]
    public async Task A_failed_task_is_not_drawn_as_a_success()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () => processes.Emit(Out(
            """{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":false,"durationMs":900}"""));

        await vm.ComposeCommand.ExecuteAsync();

        var line = Assert.Single(vm.Activity);
        Assert.False(line.Success);
        // The task id joins the role: one agent may own several tasks.
        Assert.Equal("extraction", line.Detail);
        Assert.True(line.HasDetail);
    }

    [Fact]
    public async Task A_task_without_a_duration_is_not_said_done_in_zero_seconds()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () => processes.Emit(Out(
            """{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true}"""));

        await vm.ComposeCommand.ExecuteAsync();

        var line = Assert.Single(vm.Activity);
        Assert.True(line.Success);
        Assert.Equal("Lecteur — done", line.Text);
    }

    [Fact]
    public async Task A_task_that_ended_on_its_iteration_cap_is_not_said_plainly_done()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () => processes.Emit(Out(
            """{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true,"durationMs":900,"endedOnIterationCap":true}"""));

        await vm.ComposeCommand.ExecuteAsync();

        var line = Assert.Single(vm.Activity);
        Assert.Equal("Lecteur — cut short: its iterations ran out, its work may be incomplete", line.Text);
        Assert.True(line.CutShort);
    }

    [Fact]
    public async Task A_trial_task_whose_later_pass_went_through_is_no_longer_said_cut_short()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () =>
        {
            processes.Emit(Out(
                """{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true,"endedOnIterationCap":true}"""));
            processes.Emit(Out(
                """{"v":2,"seq":8,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true}"""));
        };

        await vm.ComposeCommand.ExecuteAsync();

        Assert.Equal([false, false], vm.Activity.Select(line => line.CutShort));
        Assert.All(vm.Activity, line => Assert.Equal("Lecteur — done", line.Text));
    }

    [Fact]
    public async Task A_trial_task_that_did_not_write_its_file_says_so_before_its_cap()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () =>
        {
            // Cut short and missing its file: the fact the engine checked first, then the cap.
            processes.Emit(Out(
                """{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true,"endedOnIterationCap":true,"deliverableMissing":true}"""));
            // Cut short, its file written: the cap's sentence, as with an older CLI.
            processes.Emit(Out(
                """{"v":2,"seq":8,"ts":"t","kind":"task.completed","taskId":"synthese","agentRole":"Redacteur","success":true,"endedOnIterationCap":true,"deliverableMissing":false}"""));
        };

        await vm.ComposeCommand.ExecuteAsync();

        Assert.Equal("Lecteur — done, but did not write its file; cut short too: its iterations ran out", vm.Activity[0].Text);
        Assert.True(vm.Activity[0].FileMissing);
        Assert.True(vm.Activity[0].CutShort);
        Assert.True(vm.Activity[0].HasWarning);
        Assert.Equal("Redacteur — cut short: its iterations ran out, its work may be incomplete", vm.Activity[1].Text);
        Assert.False(vm.Activity[1].FileMissing);
        Assert.True(vm.Activity[1].HasWarning);
    }

    /// <summary>
    /// An earlier pass keeps its finding unless a later pass of the same task made it good, as
    /// the Launch screen keeps an earlier row's note: a later pass that failed, or that missed
    /// its file again, is no reason to tick the earlier one.
    /// </summary>
    [Fact]
    public async Task A_trial_task_whose_later_pass_failed_or_missed_again_keeps_its_earlier_warning()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () =>
        {
            processes.Emit(Out(
                """{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true,"endedOnIterationCap":true,"deliverableMissing":true}"""));
            processes.Emit(Out(
                """{"v":2,"seq":8,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true,"endedOnIterationCap":true,"deliverableMissing":true}"""));
            processes.Emit(Out(
                """{"v":2,"seq":9,"ts":"t","kind":"task.completed","taskId":"synthese","agentRole":"Redacteur","success":true,"deliverableMissing":true}"""));
            processes.Emit(Out(
                """{"v":2,"seq":10,"ts":"t","kind":"task.completed","taskId":"synthese","agentRole":"Redacteur","success":false}"""));
        };

        await vm.ComposeCommand.ExecuteAsync();

        Assert.Equal([true, true, true, false], vm.Activity.Select(line => line.FileMissing));
        Assert.Equal([true, true, false, false], vm.Activity.Select(line => line.CutShort));
        Assert.Equal("Redacteur — done, but did not write its file", vm.Activity[2].Text);
    }

    [Fact]
    public async Task A_trial_task_whose_later_pass_wrote_its_file_is_said_done()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () =>
        {
            processes.Emit(Out(
                """{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true,"deliverableMissing":true}"""));
            processes.Emit(Out(
                """{"v":2,"seq":8,"ts":"t","kind":"task.completed","taskId":"extraction","agentRole":"Lecteur","success":true,"deliverableMissing":false}"""));
        };

        await vm.ComposeCommand.ExecuteAsync();

        Assert.Equal([false, false], vm.Activity.Select(line => line.HasWarning));
        Assert.All(vm.Activity, line => Assert.Equal("Lecteur — done", line.Text));
    }

    /// <summary>
    /// The owner's own screenshot: score 0,70, a green «passing» badge, and four «?» beside
    /// it. Every one of those is honest — 0.70 is the deterministic fallback's constant and
    /// no per-criterion verdict exists without an LLM judge — but the card never said so, so
    /// the reader was left with an enigma.
    /// </summary>
    [Fact]
    public async Task A_mechanical_verdict_says_it_judged_nothing_line_by_line()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () => processes.Emit(Out(
            """{"v":2,"seq":9,"ts":"t","kind":"verdict.ready","score":0.7,"passing":true,"judge":"deterministic","findings":[],"suggestions":[]}"""));

        await vm.ComposeCommand.ExecuteAsync();

        Assert.True(vm.HasVerdict);
        Assert.True(vm.VerdictIsMechanical);
    }

    [Fact]
    public async Task An_llm_verdict_is_not_called_mechanical()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () => processes.Emit(Out(
            """{"v":2,"seq":9,"ts":"t","kind":"verdict.ready","score":0.9,"passing":true,"judge":"llm","findings":[],"suggestions":[]}"""));

        await vm.ComposeCommand.ExecuteAsync();

        Assert.False(vm.VerdictIsMechanical);
    }

    /// <summary>
    /// The engine computes these, puts them on the wire, and Studio.Core parses them. Nothing
    /// showed them — so the fix-and-retry button asked the user to invent the correction the
    /// judge had already written.
    /// </summary>
    [Fact]
    public async Task What_the_engine_suggests_reaches_the_screen()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () => processes.Emit(Out(
            """{"v":2,"seq":9,"ts":"t","kind":"verdict.ready","score":0.4,"passing":false,"judge":"llm","findings":[],"suggestions":[{"target":"redacteur","change":"citer le fichier source","reason":"un critère l'exige"}]}"""));

        await vm.ComposeCommand.ExecuteAsync();

        Assert.True(vm.HasSuggestions);
        var line = Assert.Single(vm.Suggestions);
        Assert.Contains("redacteur", line, StringComparison.Ordinal);
        Assert.Contains("citer le fichier source", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mechanical finding carries the run's own error as evidence — precisely the crash
    /// text step 3 could not show. It travelled on the wire and was dropped at the projection.
    /// </summary>
    [Fact]
    public async Task A_findings_evidence_reaches_the_checklist()
    {
        var (vm, processes) = Build();
        processes.WhileRunning = () => processes.Emit(Out(
            """{"v":2,"seq":9,"ts":"t","kind":"verdict.ready","score":0.0,"passing":false,"judge":"deterministic","findings":[{"id":"F-RUN","severity":"blocking","statement":"Le run a échoué","evidence":"file_write: access denied"}],"suggestions":[]}"""));

        await vm.ComposeCommand.ExecuteAsync();

        var line = Assert.Single(vm.Checklist, l => l.Statement.Contains("échoué", StringComparison.Ordinal));
        Assert.Contains("access denied", line.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_engines_own_error_survives_the_generic_apology()
    {
        var (vm, processes) = Build();
        processes.ExitCode = 2;
        processes.WhileRunning = () =>
        {
            processes.Emit(ProcessOutputLine.Now(
                ProcessOutputChannel.StandardError, "orkeon forge: the model refused twice."));
            processes.Emit(Out(
                """{"v":2,"seq":9,"ts":"t","kind":"session.finished","status":"failed","exitCode":2}"""));
        };

        await vm.ComposeCommand.ExecuteAsync();

        Assert.Contains("refused twice", vm.StatusMessage, StringComparison.Ordinal);
    }
}
