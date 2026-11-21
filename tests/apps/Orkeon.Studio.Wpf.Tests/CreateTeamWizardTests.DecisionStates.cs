using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Teams;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The list of modes through the states of the wizard: enabled, it shows the pick, which is what the
/// next « Compose the team » sends; locked, it shows the mode of the session on screen (or the pick
/// just sent, until the session states its own). And what the status line, the activity lines and
/// their pass names keep through the end of a run and a change of language.
/// </summary>
public partial class CreateTeamWizardTests
{
    private const string RequiredSession =
        """{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","crewDecision":"required"}""";

    private const string DryPause = """{"v":2,"seq":2,"ts":"t","kind":"session.finished","status":"paused","exitCode":0}""";

    /// <summary>What the list of modes shows at one moment.</summary>
    private sealed record ListState(bool Enabled, string Shown, string SessionLine, bool CanCompose);

    private static ListState Observe(CreateTeamViewModel vm) =>
        new(vm.CanChooseCrewDecision, vm.SelectedCrewDecision.Mode, vm.SessionCrewDecisionLine, vm.CanCompose);

    private static void Pick(CreateTeamViewModel vm, string mode) =>
        vm.SelectedCrewDecision = vm.CrewDecisionChoices.Single(choice => choice.Mode == mode);

    /// <summary>
    /// Each state: whether the list is enabled, the mode it shows, and the mode of the session the
    /// line under it names (null: no line). In every state that starts from a blank wizard the user
    /// picked « When available », and the engine states the session « Always » unless said otherwise,
    /// so that the pick and the session's mode are told apart.
    /// </summary>
    public static TheoryData<string, bool, string, string?> ListStates => new()
    {
        { "fresh", true, ForgeCrewDecisions.Optional, null },
        { "engine starting", false, ForgeCrewDecisions.Optional, null },
        { "composing", false, ForgeCrewDecisions.Required, null },
        { "dry pause, need typed", true, ForgeCrewDecisions.Optional, ForgeCrewDecisions.Required },
        { "dry pause, session on the pick", true, ForgeCrewDecisions.Optional, null },
        { "dry pause, need cleared", false, ForgeCrewDecisions.Required, null },
        { "resumed", false, ForgeCrewDecisions.Required, null },
        { "resumed, need typed", true, ForgeCrewDecisions.None, ForgeCrewDecisions.Required },
        { "reopened", false, ForgeCrewDecisions.Required, null },
        { "adopted", true, ForgeCrewDecisions.None, null },
        { "started over", true, ForgeCrewDecisions.None, null },
    };

    [Theory]
    [MemberData(nameof(ListStates))]
    public async Task The_list_of_modes_shows_what_the_next_composition_sends_or_the_sessions_mode(
        string state, bool enabled, string shown, string? sessionLineMode)
    {
        var temporary = new List<string>();
        try
        {
            var (vm, processes, seen) = await Reach(state, temporary);

            Assert.Equal(enabled, seen.Enabled);
            Assert.Equal(shown, seen.Shown);
            var label = sessionLineMode is null ? null : vm.CrewDecisionChoices.Single(choice => choice.Mode == sessionLineMode).Label;
            Assert.Equal(
                label is null
                    ? ""
                    : $"The team on screen was composed with « {label} » and keeps it: the choice above is for the next « Compose the team ».",
                seen.SessionLine);

            if (!enabled)
            {
                // A locked list: nothing can be composed, so the pick it hides reaches nothing.
                Assert.False(seen.CanCompose);
                return;
            }

            // An enabled list: the next « Compose the team » sends the mode it shows.
            processes.WhileRunning = null;
            processes.OutputToEmit.Clear();
            FillStepOne(vm);
            Assert.Equal(shown, vm.SelectedCrewDecision.Mode);
            await Compose(vm);
            // A new session, never a resume of the one on screen.
            Assert.Equal("forge", processes.Requests[^1].Arguments[0]);
            Assert.Contains("une veille documentaire", processes.Requests[^1].Arguments);
            Assert.DoesNotContain("resume", processes.Requests[^1].Arguments);
            if (shown == ForgeCrewDecisions.None)
                Assert.DoesNotContain("--crew-decision", processes.Requests[^1].Arguments);
            else
                Assert.Contains($"--crew-decision {shown}", vm.EngineCommandLine, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var directory in temporary.Where(Directory.Exists))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(CreateTeamViewModel Vm, FakeProcessLauncher Processes, ListState Seen)> Reach(
        string state, List<string> temporary)
    {
        switch (state)
        {
            case "fresh":
            {
                var (vm, processes, _) = Build();
                Pick(vm, ForgeCrewDecisions.Optional);
                return (vm, processes, Observe(vm));
            }

            case "engine starting":
            case "composing":
            {
                var (vm, processes, _) = Build();
                ListState? seen = null;
                processes.WhileRunning = () =>
                {
                    if (state == "composing")
                        processes.Emit(Out(RequiredSession));
                    // A pick made while the engine runs would reach nothing: the list refuses it.
                    Pick(vm, ForgeCrewDecisions.None);
                    seen = Observe(vm);
                };
                processes.OutputToEmit.Add(Out(DryPause));
                Pick(vm, ForgeCrewDecisions.Optional);
                FillStepOne(vm);
                await Compose(vm);
                return (vm, processes, seen!);
            }

            case "dry pause, need typed":
            case "dry pause, session on the pick":
            case "dry pause, need cleared":
            {
                var (vm, processes, _) = Build();
                processes.OutputToEmit.AddRange(
                    [Out(state == "dry pause, session on the pick" ? OptionalSession : RequiredSession), Out(DryPause)]);
                Pick(vm, ForgeCrewDecisions.Optional);
                FillStepOne(vm);
                await Compose(vm);
                Assert.True(vm.CanTryTeam);
                if (state == "dry pause, need cleared")
                    vm.Need = "";
                return (vm, processes, Observe(vm));
            }

            case "resumed":
            case "resumed, need typed":
            {
                var (root, sessionDir) = await WriteSession("veille", "Test", "required");
                temporary.Add(root);
                var (vm, processes, _) = Build();
                Pick(vm, ForgeCrewDecisions.Optional);
                await vm.ResumeAsync(new ForgeSolutionSummary { Slug = "veille", State = "Test", Status = "Active", Directory = sessionDir });
                if (state == "resumed, need typed")
                    FillStepOne(vm);
                return (vm, processes, Observe(vm));
            }

            case "reopened":
            {
                var (root, teamDir, sessionDir) = await WriteOrphanTeam();
                temporary.Add(root);
                var (vm, processes, _) = Build(teamsRoot: Path.Combine(root, "teams"));
                Pick(vm, ForgeCrewDecisions.Optional);
                processes.WhileRunning = () =>
                {
                    WriteRebuiltSession(sessionDir, teamDir);
                    var file = Path.Combine(sessionDir, "session.json");
                    File.WriteAllText(file, File.ReadAllText(file).Replace("\"format\":\"yaml\"", "\"format\":\"yaml\",\"crewDecision\":\"required\"", StringComparison.Ordinal));
                };
                processes.OutputToEmit.AddRange(RebuiltStream(sessionDir, teamDir));
                await vm.ReopenTeamAsync(TeamCatalog.Describe(teamDir));
                return (vm, processes, Observe(vm));
            }

            case "adopted":
            {
                var root = Path.Combine(Path.GetTempPath(), $"orkeon-wizard-{Guid.NewGuid():N}");
                temporary.Add(root);
                var (vm, processes, _) = Build(teamsRoot: root);
                processes.OutputToEmit.AddRange(
                    [Out(RequiredSession), Out("""{"v":2,"seq":2,"ts":"t","kind":"session.finished","status":"ready","exitCode":0}""")]);
                Pick(vm, ForgeCrewDecisions.Optional);
                FillStepOne(vm);
                await Compose(vm);
                vm.TeamName = "Ma veille";
                processes.OutputToEmit.Clear();
                processes.OutputToEmit.AddRange(
                [
                    Out($$"""{"v":2,"seq":1,"ts":"t","kind":"promoted","path":{{System.Text.Json.JsonSerializer.Serialize(Path.Combine(root, "ma-veille"))}},"launcher":"run.sh"}"""),
                    Out("""{"v":2,"seq":2,"ts":"t","kind":"session.finished","status":"ready","exitCode":0}"""),
                ]);
                await vm.SaveTeamCommand.ExecuteAsync();
                Assert.Null(vm.Failure);
                return (vm, processes, Observe(vm));
            }

            default:
            {
                Assert.Equal("started over", state);
                var (vm, processes, _) = Build();
                processes.OutputToEmit.AddRange([Out(RequiredSession), Out(DryPause)]);
                Pick(vm, ForgeCrewDecisions.Optional);
                FillStepOne(vm);
                await Compose(vm);
                vm.RestartCommand.Execute(null);
                return (vm, processes, Observe(vm));
            }
        }
    }

    /// <summary>
    /// The pick changed under a session is said at once: the line under the list follows the pick, and
    /// goes when the pick is the session's mode again.
    /// </summary>
    [Fact]
    public async Task The_line_on_the_sessions_mode_follows_the_pick()
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange([Out(OptionalSession), Out(DryPause)]);
        Pick(vm, ForgeCrewDecisions.Optional);
        FillStepOne(vm);
        await Compose(vm);
        Assert.Equal("", vm.SessionCrewDecisionLine);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Pick(vm, ForgeCrewDecisions.Required);

        Assert.Contains(nameof(CreateTeamViewModel.SessionCrewDecisionLine), raised);
        Assert.Contains("« When available: the team runs with or without them »", vm.SessionCrewDecisionLine, StringComparison.Ordinal);

        Pick(vm, ForgeCrewDecisions.Optional);
        Assert.Equal("", vm.SessionCrewDecisionLine);
    }

    /// <summary>
    /// A warning of the run that ends ready stays readable: the sentence that closes the run is
    /// followed by it. The next run starts clean.
    /// </summary>
    [Fact]
    public async Task A_warning_of_the_run_that_ends_ready_stays_on_the_status_line()
    {
        var (vm, processes, _) = Build();
        processes.OutputToEmit.AddRange(
        [
            Out(OptionalSession),
            Out("""{"v":2,"seq":2,"ts":"t","kind":"warning","code":"FORGE-DECISION-PASS-SKIPPED","message":"The pass with System One was skipped."}"""),
            Out("""{"v":2,"seq":3,"ts":"t","kind":"session.finished","status":"ready","exitCode":0}"""),
        ]);
        FillStepOne(vm);

        await Compose(vm);

        Assert.Equal(
            "Your solution is ready. FORGE-DECISION-PASS-SKIPPED: The pass with System One was skipped.",
            vm.StatusMessage);

        processes.OutputToEmit.RemoveAt(1);
        await Compose(vm);
        Assert.Equal("Your solution is ready.", vm.StatusMessage);
    }

    /// <summary>
    /// A trial that dies during the first of its two passes: the engine is gone and names no pass any
    /// more, and the lines it left still say which pass they come from.
    /// </summary>
    [Fact]
    public async Task The_lines_of_a_trial_that_died_in_its_first_pass_keep_the_name_of_their_pass()
    {
        var (vm, processes, _) = Build();
        processes.ExitCode = 1;
        processes.OutputToEmit.AddRange(
        [
            Out(OptionalSession),
            Out("""{"v":2,"seq":2,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":1,"circuit":"on"}"""),
            Out("""{"v":2,"seq":3,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":true,"durationMs":1200,"circuit":"on"}"""),
            Out("""{"v":2,"seq":4,"ts":"t","kind":"run.finished","run":1,"circuit":"on","mailActions":[{"kind":"send","recipients":["a@example.org"],"subject":"first"}]}"""),
        ]);
        FillStepOne(vm);

        await Compose(vm);

        Assert.False(vm.TrialInProgress);
        Assert.Equal("", vm.TrialPassLine);
        Assert.Equal("read · with the decision models", Assert.Single(vm.Activity).Detail);
        Assert.Equal(["send → a@example.org — first · with the decision models"], vm.MailActionLines);
    }

    /// <summary>
    /// A change of language reads the lines again: the name of the pass inside an activity line, a
    /// checklist detail and a mail action is said in the new language, like the sentence around it.
    /// </summary>
    [Fact]
    public async Task A_language_switch_names_the_passes_of_the_lines_on_screen_again()
    {
        var strings = new FakeSwitchableStudioStrings("fr");
        var (vm, processes, _) = Build(strings: strings);
        processes.OutputToEmit.AddRange(
        [
            Out(OptionalSession),
            Out(BriefTwoCriteria),
            Out("""{"v":2,"seq":3,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":1,"circuit":"on"}"""),
            Out("""{"v":2,"seq":4,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":true,"durationMs":1200,"circuit":"on"}"""),
            Out("""{"v":2,"seq":5,"ts":"t","kind":"run.finished","run":1,"circuit":"on","mailActions":[{"kind":"send","recipients":["a@example.org"],"subject":"first"}]}"""),
            Out("""{"v":2,"seq":6,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":2,"circuit":"off"}"""),
            Out("""{"v":2,"seq":7,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":true,"durationMs":900,"circuit":"off"}"""),
            Out("""{"v":2,"seq":8,"ts":"t","kind":"run.finished","run":1,"circuit":"off"}"""),
            Out(TwoPassVerdict),
        ]);
        FillStepOne(vm);
        await Compose(vm);
        Assert.Equal(["read · with the decision models", "read · without the decision models"], vm.Activity.Select(line => line.Detail));
        Assert.Equal("timeout · with the decision models", vm.Checklist[0].Detail);
        var english = vm.Activity[0].Text;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        strings.Switch();

        Assert.Equal(
            ["read · avec les modèles de décision", "read · sans les modèles de décision"],
            vm.Activity.Select(line => line.Detail));
        Assert.NotEqual(english, vm.Activity[0].Text);
        Assert.Equal("timeout · avec les modèles de décision", vm.Checklist[0].Detail);
        Assert.Equal("aucune source · sans les modèles de décision", vm.Checklist[2].Detail);
        Assert.Equal(["send → a@example.org — first · avec les modèles de décision"], vm.MailActionLines);
        Assert.Contains(nameof(CreateTeamViewModel.MailActionLines), raised);
    }
}
