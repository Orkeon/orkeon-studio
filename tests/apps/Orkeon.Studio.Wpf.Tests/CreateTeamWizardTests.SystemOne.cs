using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.ViewModels.Teams;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// System One in the creation wizard: what the team to compose asks of the decision models is chosen
/// on the first step and passed to a new creation, and the assistant can compose without asking them.
/// </summary>
public partial class CreateTeamWizardTests
{
    [Fact]
    public async Task A_new_creation_passes_neither_option_until_the_user_asks()
    {
        var (vm, _, _) = Build();
        FillStepOne(vm);
        Assert.Equal(ForgeCrewDecisions.None, vm.SelectedCrewDecision.Mode);
        Assert.Equal(["none", "optional", "required"], vm.CrewDecisionChoices.Select(choice => choice.Mode));

        await Compose(vm);

        Assert.DoesNotContain("--crew-decision", vm.EngineCommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-decision", vm.EngineCommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mode_chosen_and_the_assistants_switch_reach_the_engine_of_a_new_creation()
    {
        var (vm, _, _) = Build();
        FillStepOne(vm);
        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
        vm.DesignWithoutSystemOne = true;

        await Compose(vm);

        Assert.Contains("--crew-decision optional", vm.EngineCommandLine, StringComparison.Ordinal);
        Assert.Contains("--no-decision", vm.EngineCommandLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// While a session holds the screen and nothing can be composed, the list is locked on the mode
    /// the engine states for it. The user's pick is kept apart: it is what « Compose the team » sends
    /// when it would start a new session, and the list shows it again then.
    /// </summary>
    [Fact]
    public async Task The_list_shows_the_sessions_mode_while_it_runs_and_the_pick_when_compose_would_start_anew()
    {
        var (vm, processes, _) = Build();
        var whileRunning = (Mode: "", CanChoose: true);
        processes.WhileRunning = () =>
        {
            processes.Emit(Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","crewDecision":"required"}"""));
            whileRunning = (vm.SelectedCrewDecision.Mode, vm.CanChooseCrewDecision);
        };
        processes.OutputToEmit.Add(Out("""{"v":2,"seq":2,"ts":"t","kind":"session.finished","status":"paused","exitCode":0}"""));
        FillStepOne(vm);

        await Compose(vm);

        Assert.Equal((ForgeCrewDecisions.Required, false), whileRunning);
        // The need is still typed: « Compose the team » would start a new session, with the pick.
        Assert.True(vm.CanChooseCrewDecision);
        Assert.Equal(ForgeCrewDecisions.None, vm.SelectedCrewDecision.Mode);

        // Nothing to compose: the screen is the session's, and so is the list.
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Need = "";
        Assert.False(vm.CanChooseCrewDecision);
        Assert.Equal(ForgeCrewDecisions.Required, vm.SelectedCrewDecision.Mode);
        Assert.Contains(nameof(CreateTeamViewModel.CanChooseCrewDecision), raised);
        Assert.Contains(nameof(CreateTeamViewModel.SelectedCrewDecision), raised);

        // A locked list is not the user's to change.
        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
        Assert.Equal(ForgeCrewDecisions.Required, vm.SelectedCrewDecision.Mode);
    }

    /// <summary>
    /// The mode and the switch belong to a creation: what a session stated, or the user chose, does not
    /// become the next team's.
    /// </summary>
    [Fact]
    public async Task Starting_over_gives_the_next_creation_the_default_mode_and_no_switch()
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange(
        [
            Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","crewDecision":"required"}"""),
            Out("""{"v":2,"seq":2,"ts":"t","kind":"session.finished","status":"paused","exitCode":0}"""),
        ]);
        FillStepOne(vm);
        Assert.True(vm.CanChooseCrewDecision);
        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
        vm.DesignWithoutSystemOne = true;
        await Compose(vm);
        Assert.Equal(ForgeCrewDecisions.Optional, vm.SelectedCrewDecision.Mode);

        vm.RestartCommand.Execute(null);

        Assert.Equal(ForgeCrewDecisions.None, vm.SelectedCrewDecision.Mode);
        Assert.False(vm.DesignWithoutSystemOne);
        Assert.True(vm.CanChooseCrewDecision);
    }

    private static async Task<(string Root, string SessionDir)> WriteSession(string slug, string state, string? crewDecision)
    {
        var root = Path.Combine(Path.GetTempPath(), "orkeon-wiz-s1-" + Guid.NewGuid().ToString("N"));
        var sessionDir = Path.Combine(root, ".orkeon", "forge", slug);
        Directory.CreateDirectory(sessionDir);
        var mode = crewDecision is null ? "" : $",\"crewDecision\":\"{crewDecision}\"";
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "session.json"),
            $$"""{"v":1,"slug":"{{slug}}","title":"Veille","format":"yaml","state":"{{state}}","status":"Active"{{mode}}}""", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "blueprint.json"),
            """{"crew":{"name":"veille"},"agents":[{"key":"a","role":"A","tools":[]}],"tasks":[{"key":"t","description":"d","agent":"a"}]}""", TestContext.Current.CancellationToken);
        return (root, sessionDir);
    }

    /// <summary>
    /// A session resumed from My teams shows the mode its <c>session.json</c> holds, before any engine
    /// run states it; that mode is the session's, and the next creation is composed with the user's
    /// pick, which the resume put back on the engine's default along with the assistant's switch.
    /// </summary>
    [Fact]
    public async Task A_resumed_sessions_mode_is_read_off_its_file_and_never_becomes_the_next_creations()
    {
        var (root, sessionDir) = await WriteSession("veille", "Test", "required");
        try
        {
            var off = AppSettingsDocument.CreateEmpty().Decision.Resolve(_ => null);
            var (vm, processes, _) = Build(machineDecision: () => off);
            vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
            vm.DesignWithoutSystemOne = true;

            await vm.ResumeAsync(new ForgeSolutionSummary { Slug = "veille", State = "Test", Status = "Active", Directory = sessionDir });

            Assert.Empty(processes.Requests);
            Assert.False(vm.CanChooseCrewDecision);
            Assert.Equal(ForgeCrewDecisions.Required, vm.SelectedCrewDecision.Mode);
            Assert.False(vm.DesignWithoutSystemOne);
            // What the session's mode meets on this machine is said for a resumed session too.
            Assert.Contains("its trial will be refused", vm.CrewDecisionNotice, StringComparison.Ordinal);

            // A need typed over it: « Compose the team » starts a new session, on the user's pick.
            FillStepOne(vm);
            Assert.True(vm.CanChooseCrewDecision);
            Assert.Equal(ForgeCrewDecisions.None, vm.SelectedCrewDecision.Mode);
            Assert.Equal("", vm.CrewDecisionNotice);
            await Compose(vm);
            Assert.DoesNotContain("--crew-decision", vm.EngineCommandLine, StringComparison.Ordinal);
            Assert.DoesNotContain("--no-decision", vm.EngineCommandLine, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A session written before the modes states none: the list shows « No » until an engine run states the mode it derives.</summary>
    [Fact]
    public async Task A_resumed_session_older_than_the_modes_shows_no_mode_until_the_engine_states_one()
    {
        var (root, sessionDir) = await WriteSession("veille", "Blueprint", crewDecision: null);
        try
        {
            var (vm, processes, _) = Build();
            var hydrated = "";
            processes.WhileRunning = () => hydrated = vm.SelectedCrewDecision.Mode;
            processes.OutputToEmit.Add(Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","resumed":true,"crewDecision":"required"}"""));

            await vm.ResumeAsync(new ForgeSolutionSummary { Slug = "veille", State = "Blueprint", Status = "Active", Directory = sessionDir });

            Assert.Equal(ForgeCrewDecisions.None, hydrated);
            Assert.Equal(ForgeCrewDecisions.Required, vm.SelectedCrewDecision.Mode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// « Resume » on the session under way is the same creation going on: the engine never stores
    /// <c>--no-decision</c>, so the box keeps its tick and the resumed run carries the switch. A resume
    /// of another session is another creation: the box is unticked.
    /// </summary>
    [Fact]
    public async Task Resume_on_the_session_under_way_keeps_the_assistants_switch()
    {
        var (root, sessionDir) = await WriteSession("veille", "Blueprint", "optional");
        var (otherRoot, otherDir) = await WriteSession("autre", "Test", "none");
        try
        {
            var (vm, processes, _) = Build(workspace: root);
            processes.HonourCancellation = true;
            processes.WhileRunning = () =>
            {
                processes.Emit(Out($$"""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":{{System.Text.Json.JsonSerializer.Serialize(sessionDir)}},"format":"yaml","resumed":false,"crewDecision":"optional"}"""));
                vm.StopCommand.Execute(null);
            };
            FillStepOne(vm);
            vm.DesignWithoutSystemOne = true;
            await Compose(vm);
            Assert.True(vm.CanResumeHere);

            processes.HonourCancellation = false;
            processes.WhileRunning = null;
            await vm.ResumeHereCommand.ExecuteAsync();

            Assert.True(vm.DesignWithoutSystemOne);
            Assert.Equal(["forge", "resume", "veille"], processes.Requests[^1].Arguments.Take(3));
            Assert.Contains("--no-decision", processes.Requests[^1].Arguments);

            await vm.ResumeAsync(new ForgeSolutionSummary { Slug = "autre", State = "Test", Status = "Active", Directory = otherDir });
            Assert.False(vm.DesignWithoutSystemOne);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(otherRoot, recursive: true);
        }
    }

    /// <summary>
    /// « Modify » turns the wizard to another creation, as a resume does: the box nobody ticked for
    /// this team is unticked, the pick goes back to the default, and the list shows the reopened
    /// session's own mode.
    /// </summary>
    [Fact]
    public async Task Modify_unticks_the_switch_and_shows_the_reopened_sessions_mode()
    {
        var (root, teamDir, sessionDir) = await WriteOrphanTeam();
        try
        {
            var (vm, processes, _) = Build(teamsRoot: Path.Combine(root, "teams"));
            vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
            vm.DesignWithoutSystemOne = true;
            processes.WhileRunning = () =>
            {
                WriteRebuiltSession(sessionDir, teamDir);
                var file = Path.Combine(sessionDir, "session.json");
                File.WriteAllText(file, File.ReadAllText(file).Replace("\"format\":\"yaml\"", "\"format\":\"yaml\",\"crewDecision\":\"required\"", StringComparison.Ordinal));
            };
            processes.OutputToEmit.AddRange(RebuiltStream(sessionDir, teamDir));

            await vm.ReopenTeamAsync(TeamCatalog.Describe(teamDir));

            Assert.Equal("veille-docs", vm.SessionSlug);
            Assert.False(vm.DesignWithoutSystemOne);
            Assert.False(vm.CanChooseCrewDecision);
            Assert.Equal(ForgeCrewDecisions.Required, vm.SelectedCrewDecision.Mode);
            // The reopen itself takes a fixed command line: neither option is on it.
            Assert.Equal(["forge", "reopen", teamDir, "--events", "jsonl"], processes.Requests[0].Arguments);

            // The pick went back to the default: a new creation typed over it is composed with « No ».
            vm.Need = "une autre veille";
            Assert.Equal(ForgeCrewDecisions.None, vm.SelectedCrewDecision.Mode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A mode that needs the decision models, chosen on a machine where they are off: said under the
    /// list before anything is composed, since the engine would compose the team and refuse its trial.
    /// </summary>
    [Fact]
    public void A_mode_the_machine_cannot_serve_is_said_before_anything_is_composed()
    {
        var off = AppSettingsDocument.CreateEmpty().Decision.Resolve(_ => null);
        var (vm, _, _) = Build(machineDecision: () => off);
        Assert.Equal("", vm.CrewDecisionNotice);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Contains("its trial will be refused", vm.CrewDecisionNotice, StringComparison.Ordinal);
        Assert.Contains("no decision provider is configured", vm.CrewDecisionNotice, StringComparison.Ordinal);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
        Assert.Contains("the trial will run without them only", vm.CrewDecisionNotice, StringComparison.Ordinal);

        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.None);
        Assert.Equal("", vm.CrewDecisionNotice);

        // With the models on, or their state unknown, nothing is said.
        var on = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{"ApiKey":"in-file"}}}""").Decision.Resolve(_ => null);
        var (served, _, _) = Build(machineDecision: () => on);
        served.SelectedCrewDecision = served.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Equal("", served.CrewDecisionNotice);
        var (unknown, _, _) = Build();
        unknown.SelectedCrewDecision = unknown.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Equal("", unknown.CrewDecisionNotice);
    }

    /// <summary>The reason the notice gives follows what switched the models off: the engine's refusal, the settings' switch.</summary>
    [Fact]
    public void The_notice_says_why_the_decision_models_are_off()
    {
        var refused = AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"Provider":"nowhere"}}}""").Decision.Resolve(_ => null);
        Assert.NotNull(refused.Refusal);
        var (vm, _, _) = Build(machineDecision: () => refused);
        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Contains(refused.Refusal, vm.CrewDecisionNotice, StringComparison.Ordinal);

        var switchedOff = AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"Enabled":false},"Liquid":{"ApiKey":"in-file"}}}""").Decision.Resolve(_ => null);
        Assert.False(switchedOff.Enabled);
        Assert.Null(switchedOff.Refusal);
        var (off, _, _) = Build(machineDecision: () => switchedOff);
        off.SelectedCrewDecision = off.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Optional);
        Assert.Contains("the trial will run without them only", off.CrewDecisionNotice, StringComparison.Ordinal);
        Assert.Contains(EnglishStudioStrings.Instance[StudioStringKeys.RunNoteCauseSettings], off.CrewDecisionNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// A provider in place without its key leaves the circuit on: the engine composes the team and
    /// starts its trial, whose first request to the models fails (« No liquid API key »). The notice
    /// says so for a mode that asks the models, and nothing once the key is there.
    /// </summary>
    [Fact]
    public void A_provider_without_its_key_is_said_for_a_mode_that_asks_the_models()
    {
        var keyless = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{}}}""").Decision.Resolve(_ => null);
        Assert.True(keyless.CircuitOn);
        Assert.Equal("LIQUID_API_KEY", keyless.MissingKeyVariable);
        var (vm, _, _) = Build(machineDecision: () => keyless);
        Assert.Equal("", vm.CrewDecisionNotice);

        foreach (var mode in new[] { ForgeCrewDecisions.Required, ForgeCrewDecisions.Optional })
        {
            vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == mode);
            Assert.Equal(
                EnglishStudioStrings.Instance.Format(StudioStringKeys.RunWarnDecisionKeyMissing, "Liquid", "LIQUID_API_KEY"),
                vm.CrewDecisionNotice);
            Assert.DoesNotContain("will be refused", vm.CrewDecisionNotice, StringComparison.Ordinal);
        }

        var keyed = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{}}}""").Decision.Resolve(name => name == "LIQUID_API_KEY" ? "secret" : null);
        var (served, _, _) = Build(machineDecision: () => keyed);
        served.SelectedCrewDecision = served.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.Equal("", served.CrewDecisionNotice);
    }

    /// <summary>The decision settings change while the wizard is open: the notice is read again when the shell says so.</summary>
    [Fact]
    public void The_notice_follows_the_decision_settings_when_told_they_changed()
    {
        var status = AppSettingsDocument.CreateEmpty().Decision.Resolve(_ => null);
        var (vm, _, _) = Build(machineDecision: () => status);
        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        Assert.NotEqual("", vm.CrewDecisionNotice);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        status = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{"ApiKey":"in-file"}}}""").Decision.Resolve(_ => null);
        vm.RefreshDecisionNotice();

        Assert.Contains(nameof(CreateTeamViewModel.CrewDecisionNotice), raised);
        Assert.Equal("", vm.CrewDecisionNotice);
    }

    private const string OptionalSession =
        """{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","crewDecision":"optional"}""";

    private const string TwoPassVerdict =
        """{"v":2,"seq":9,"ts":"t","kind":"verdict.ready","score":0,"passing":false,"judge":"deterministic","suggestions":[],"findings":[{"id":"F-RUN","severity":"blocking","statement":"The crew did not complete its run.","evidence":"timeout","circuit":"on"},{"id":"F-RUN","severity":"blocking","statement":"The crew did not complete its run.","circuit":"off"},{"id":"F1","severity":"major","acceptance":"A1","statement":"aucune source","circuit":"off"}],"passes":[{"circuit":"on","passing":false,"score":0,"tokens":1200},{"circuit":"off","passing":false,"score":0}]}""";

    /// <summary>
    /// A warning of the assistant reaches the status line as the engine words it, and a trial in two
    /// passes says which pass is under way, which pass each line, each finding and each mail action
    /// belongs to, and each pass's verdict.
    /// </summary>
    [Fact]
    public async Task A_two_pass_trial_shows_its_passes_and_the_assistants_warning()
    {
        var (vm, processes, _) = Build();
        var passLines = new List<string>();
        processes.WhileRunning = () =>
        {
            processes.Emit(Out(OptionalSession));
            processes.Emit(Out("""{"v":2,"seq":2,"ts":"t","kind":"warning","code":"FORGE-DECISION-PASS-SKIPPED","message":"The pass with System One was skipped."}"""));
            processes.Emit(Out(BriefTwoCriteria));
            passLines.Add(vm.TrialPassLine);
            processes.Emit(Out("""{"v":2,"seq":3,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":1,"circuit":"on"}"""));
            passLines.Add(vm.TrialPassLine);
            processes.Emit(Out("""{"v":2,"seq":4,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":true,"durationMs":1200,"deliverableMissing":true,"circuit":"on"}"""));
            processes.Emit(Out("""{"v":2,"seq":5,"ts":"t","kind":"run.finished","run":1,"circuit":"on","mailActions":[{"kind":"send","recipients":["a@example.org"],"subject":"first"}]}"""));
            passLines.Add(vm.TrialPassLine);
            processes.Emit(Out("""{"v":2,"seq":6,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":2,"circuit":"off"}"""));
            passLines.Add(vm.TrialPassLine);
            processes.Emit(Out("""{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":true,"durationMs":900,"circuit":"off"}"""));
            processes.Emit(Out("""{"v":2,"seq":8,"ts":"t","kind":"run.finished","run":1,"circuit":"off","mailActions":[{"kind":"send","recipients":["a@example.org"],"subject":"second"}]}"""));
            passLines.Add(vm.TrialPassLine);
            processes.Emit(Out(TwoPassVerdict));
        };
        FillStepOne(vm);

        await Compose(vm);

        Assert.Equal("FORGE-DECISION-PASS-SKIPPED: The pass with System One was skipped.", vm.StatusMessage);
        Assert.Equal(
            [
                "",
                "Pass 1 of 2: with the decision models",
                // Between the passes the trial is still under way, on the pass that just ended.
                "Pass 1 of 2: with the decision models",
                "Pass 2 of 2: without the decision models",
                "",
            ],
            passLines);
        Assert.Equal("", vm.TrialPassLine);
        Assert.Equal(
            "With the decision models: criteria not met\nWithout the decision models: criteria not met",
            vm.VerdictPassesLine);

        Assert.Equal(2, vm.Activity.Count);
        // The pass without the models wrote its file: that says nothing of the pass with them.
        Assert.True(vm.Activity[0].FileMissing);
        Assert.Contains("with the decision models", vm.Activity[0].Detail, StringComparison.Ordinal);
        Assert.Contains("without the decision models", vm.Activity[1].Detail, StringComparison.Ordinal);

        // Both passes left the same mechanical finding: each line says which pass it comes from.
        Assert.Equal(
            [
                ("The crew did not complete its run.", "timeout · with the decision models"),
                ("The crew did not complete its run.", "without the decision models"),
                ("Le resume cite ses sources", "aucune source · without the decision models"),
                ("Moins d'une page", null),
            ],
            vm.Checklist.Select(line => (line.Statement, line.Detail)));
        // No judge looked: the criterion nobody found fault with is « judge for yourself ».
        Assert.Null(vm.Checklist[3].Passed);
        Assert.Equal(
            ["send → a@example.org — first · with the decision models", "send → a@example.org — second · without the decision models"],
            vm.MailActionLines);
    }

    /// <summary>
    /// The bench that crashes on the first pass of two ends the trial there: no « Pass 1 of 2 » stays
    /// on screen with the engine gone, and the next run of the session composes before it tries.
    /// </summary>
    [Fact]
    public async Task A_two_pass_trial_that_crashes_on_its_first_pass_leaves_no_pass_under_way()
    {
        var (vm, processes, _) = Build();
        processes.ExitCode = 1;
        processes.OutputToEmit.AddRange(
        [
            Out(OptionalSession),
            Out("""{"v":2,"seq":2,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":1,"circuit":"on"}"""),
            Out("""{"v":2,"seq":3,"ts":"t","kind":"run.finished","run":1,"success":false,"circuit":"on"}"""),
            Out("""{"v":2,"seq":4,"ts":"t","kind":"error","code":"FORGE-TRIAL","message":"The test bench crashed: boom","recoverable":false}"""),
            Out("""{"v":2,"seq":5,"ts":"t","kind":"session.finished","status":"failed","exitCode":1}"""),
        ]);
        FillStepOne(vm);

        await Compose(vm);

        Assert.False(vm.RunInProgress);
        Assert.False(vm.TrialInProgress);
        Assert.Equal("", vm.TrialPassLine);
    }

    /// <summary>
    /// A trial stopped in the middle of a pass writes no <c>run.finished</c> and no
    /// <c>session.finished</c>: the wizard tells the model the engine is gone, so nothing reads as a
    /// trial under way.
    /// </summary>
    [Fact]
    public async Task A_trial_stopped_mid_pass_is_over()
    {
        var (vm, processes, _) = Build();
        processes.HonourCancellation = true;
        var underWay = "";
        processes.WhileRunning = () =>
        {
            processes.Emit(Out(OptionalSession));
            processes.Emit(Out("""{"v":2,"seq":2,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":2,"circuit":"off"}"""));
            underWay = vm.TrialPassLine;
            vm.StopCommand.Execute(null);
        };
        FillStepOne(vm);

        await Compose(vm);

        Assert.Equal("Pass 2 of 2: without the decision models", underWay);
        Assert.False(vm.RunInProgress);
        Assert.False(vm.TrialInProgress);
        Assert.Equal("", vm.TrialPassLine);
    }

    /// <summary>
    /// A resume after a budget stop between the passes keeps the first: the engine starts the second
    /// alone, which is the pass under way, and the trial ends with it.
    /// </summary>
    [Fact]
    public async Task A_resume_that_keeps_the_first_pass_shows_the_second_as_the_pass_under_way()
    {
        var (vm, processes, _) = Build();
        var lines = new List<string>();
        processes.WhileRunning = () =>
        {
            processes.Emit(Out(OptionalSession));
            processes.Emit(Out("""{"v":2,"seq":2,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":2,"circuit":"off","cause":"trial"}"""));
            lines.Add(vm.TrialPassLine);
            processes.Emit(Out("""{"v":2,"seq":3,"ts":"t","kind":"run.finished","run":1,"success":true,"circuit":"off"}"""));
            lines.Add(vm.TrialPassLine);
        };
        FillStepOne(vm);

        await Compose(vm);

        Assert.Equal(["Pass 2 of 2: without the decision models", ""], lines);
        Assert.False(vm.RunInProgress);
    }

    /// <summary>
    /// The two warnings of a promotion have their sentence on the adoption line, never on the status
    /// line; and an error keeps the status line against a warning that follows it in the same run.
    /// </summary>
    [Theory]
    [InlineData(ForgeWarningCodes.SessionNotRenamed)]
    [InlineData(ForgeWarningCodes.LauncherTooLong)]
    public async Task A_promotions_warning_is_not_put_on_the_status_line(string code)
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange(
        [
            Out(OptionalSession),
            Out($$"""{"v":2,"seq":2,"ts":"t","kind":"warning","code":"{{code}}","message":"said elsewhere"}"""),
        ]);
        FillStepOne(vm);

        await Compose(vm);

        Assert.DoesNotContain(code, vm.StatusMessage ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("said elsewhere", vm.StatusMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_warning_that_follows_an_error_does_not_take_the_status_line_from_it()
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange(
        [
            Out(OptionalSession),
            Out("""{"v":2,"seq":2,"ts":"t","kind":"error","code":"FORGE-BLUEPRINT-INVALID","message":"the plan names no agent","recoverable":true}"""),
            Out("""{"v":2,"seq":3,"ts":"t","kind":"warning","code":"FORGE-DECISION-MODE-DERIVED","message":"The mode was derived."}"""),
        ]);
        FillStepOne(vm);

        await Compose(vm);

        Assert.Equal("FORGE-BLUEPRINT-INVALID: the plan names no agent", vm.StatusMessage);

        // The next run starts clean: its warning is said.
        processes.OutputToEmit.RemoveAt(1);
        await Compose(vm);
        Assert.Equal("FORGE-DECISION-MODE-DERIVED: The mode was derived.", vm.StatusMessage);
    }

    /// <summary>
    /// The adoption line says what the promotion did not do. A warning of the trial said its piece on
    /// the status line then: appended to « saved in My teams », it read as something the adoption missed.
    /// </summary>
    [Fact]
    public async Task A_warning_of_the_trial_is_not_repeated_on_the_adoption_line()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orkeon-wizard-{Guid.NewGuid():N}");
        try
        {
            var (vm, processes, _) = Build(teamsRoot: root);
            processes.OutputToEmit.AddRange(
            [
                Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/d","format":"yaml","resumed":false,"crewDecision":"optional"}"""),
                Out("""{"v":2,"seq":2,"ts":"t","kind":"warning","code":"FORGE-DECISION-PASS-SKIPPED","message":"The crew is 'optional' and the decision circuit is off: its trial runs without System One only."}"""),
                Out("""{"v":2,"seq":3,"ts":"t","kind":"session.finished","status":"ready","exitCode":0}"""),
            ]);
            FillStepOne(vm);
            await Compose(vm);
            vm.TeamName = "Ma veille";
            var promoted = Path.Combine(root, "ma-veille");
            processes.OutputToEmit.Clear();
            processes.OutputToEmit.AddRange(
            [
                Out($$"""{"v":2,"seq":1,"ts":"t","kind":"promoted","path":{{System.Text.Json.JsonSerializer.Serialize(promoted)}},"launcher":"run.sh"}"""),
                Out("""{"v":2,"seq":2,"ts":"t","kind":"session.finished","status":"ready","exitCode":0}"""),
            ]);

            await vm.SaveTeamCommand.ExecuteAsync();

            Assert.Null(vm.Failure);
            Assert.Equal("Team “Ma veille” is saved in My teams.", vm.StatusMessage);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// What the assistant itself asks of the decision models, as the engine states it for the run: its
    /// calls and what they spent over the session, or that it works without them and why.
    /// </summary>
    [Fact]
    public async Task The_wizard_says_whether_the_assistant_itself_works_with_the_decision_models()
    {
        var (vm, processes, _) = Build();
        Assert.Equal("", vm.AssistantDecisionLine);
        var lines = new List<string>();
        processes.WhileRunning = () =>
        {
            processes.Emit(Out("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","forgeCircuit":"on"}"""));
            lines.Add(vm.AssistantDecisionLine);
            processes.Emit(Out("""{"v":2,"seq":2,"ts":"t","kind":"systemone.answered","stage":"compose","use":"process","circuit":"on","questions":1,"answers":1}"""));
            processes.Emit(Out("""{"v":2,"seq":3,"ts":"t","kind":"systemone.answered","stage":"compose","use":"coverage","circuit":"on","questions":3,"answers":3}"""));
            processes.Emit(Out("""{"v":2,"seq":4,"ts":"t","kind":"cost.updated","tokens":50000,"systemOneTokens":12480}"""));
            lines.Add(vm.AssistantDecisionLine);
        };
        FillStepOne(vm);
        await Compose(vm);

        Assert.Equal(
            [
                "The assistant works with the decision models (calls on this run: 0).",
                "The assistant works with the decision models (calls on this run: 2). Those calls have spent "
                    + 12480.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " tokens over the session.",
            ],
            lines);

        async Task<string> LineOf(string circuitFields)
        {
            var (wizard, engine, _) = Build();
            engine.OutputToEmit.Add(Out($$"""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml"{{circuitFields}}}"""));
            FillStepOne(wizard);
            await Compose(wizard);
            return wizard.AssistantDecisionLine;
        }

        Assert.Equal(
            "The assistant works without the decision models: you asked it to compose without asking them.",
            await LineOf(""","forgeCircuit":"off","forgeCircuitCause":"switch" """));
        Assert.Equal(
            "The assistant works without the decision models: the settings switch them off.",
            await LineOf(""","forgeCircuit":"off","forgeCircuitCause":"settings" """));
        Assert.Equal(
            "The assistant works without the decision models: no decision provider is configured.",
            await LineOf(""","forgeCircuit":"off","forgeCircuitCause":"no-provider" """));
        Assert.Equal(
            "The assistant works without the decision models: the decision provider has no API key: set its environment variable, or its ApiKey in the settings.",
            await LineOf(""","forgeCircuit":"off","forgeCircuitCause":"no-key" """));
        // A cause this build does not know is said as the engine wrote it; none is not invented.
        Assert.Equal(
            "The assistant works without the decision models: maintenance.",
            await LineOf(""","forgeCircuit":"off","forgeCircuitCause":"maintenance" """));
        Assert.Equal("The assistant works without the decision models.", await LineOf(""","forgeCircuit":"off" """));
        // An engine that predates the statement says nothing, and neither does the wizard.
        Assert.Equal("", await LineOf(""));
    }

    /// <summary>
    /// With the decision models switched off in the settings, the assistant cannot ask them: the
    /// switch reads on, is locked, and says why; the engine is told so. What the team asks of them
    /// stays free, and the user's own choice comes back once the settings switch them on again.
    /// </summary>
    [Fact]
    public async Task The_settings_switching_the_models_off_lock_the_assistants_switch_on_and_leave_the_mode_free()
    {
        var machine = AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"Enabled":false},"Liquid":{"ApiKey":"in-file"}}}""").Decision.Resolve(_ => null);
        var (vm, _, _) = Build(machineDecision: () => machine);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(vm.DesignWithoutSystemOne);
        Assert.False(vm.CanChooseDesignWithoutSystemOne);
        Assert.Equal(EnglishStudioStrings.Instance[StudioStringKeys.WizardDesignWithoutSystemOneLocked], vm.DesignWithoutSystemOneLockedLine);
        vm.DesignWithoutSystemOne = false;
        Assert.True(vm.DesignWithoutSystemOne);

        Assert.True(vm.CanChooseCrewDecision);
        FillStepOne(vm);
        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == ForgeCrewDecisions.Required);
        await Compose(vm);
        Assert.Contains("--crew-decision required", vm.EngineCommandLine, StringComparison.Ordinal);
        Assert.Contains("--no-decision", vm.EngineCommandLine, StringComparison.Ordinal);

        // Switched on again: the switch is the user's once more, on the choice they had left (none).
        machine = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{"ApiKey":"in-file"}}}""").Decision.Resolve(_ => null);
        vm.RefreshDecisionNotice();
        Assert.Contains(nameof(CreateTeamViewModel.DesignWithoutSystemOne), raised);
        Assert.Contains(nameof(CreateTeamViewModel.CanChooseDesignWithoutSystemOne), raised);
        Assert.False(vm.DesignWithoutSystemOne);
        Assert.True(vm.CanChooseDesignWithoutSystemOne);
        Assert.Equal("", vm.DesignWithoutSystemOneLockedLine);
    }

    /// <summary>A provider missing, the settings' switch on: the assistant's switch stays the user's.</summary>
    [Fact]
    public void Only_the_settings_switch_locks_the_assistants_switch()
    {
        var noProvider = AppSettingsDocument.CreateEmpty().Decision.Resolve(_ => null);
        var (vm, _, _) = Build(machineDecision: () => noProvider);

        Assert.False(vm.DesignWithoutSystemOne);
        Assert.True(vm.CanChooseDesignWithoutSystemOne);
        vm.DesignWithoutSystemOne = true;
        Assert.True(vm.DesignWithoutSystemOne);
    }
}
