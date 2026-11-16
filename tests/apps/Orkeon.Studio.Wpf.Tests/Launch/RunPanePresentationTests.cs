using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Launch;

namespace Orkeon.Studio.Wpf.Tests.Launch;

/// <summary>
/// rc.5 harmonisation, lot L08: how the Command and Run tabs show what the launcher already knows —
/// the command split at its options (B09), the agent cards (D03, D05), the finished subtitle
/// (D01), the stale sentences that go once the run was launched (D03) and the journal fold (R16).
/// </summary>
public sealed class RunPanePresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkeon-run-pane-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static (LaunchTabViewModel Tab, FakeProcessLauncher Launcher) Build(FakeTargetProbe probe)
    {
        var launcher = new FakeProcessLauncher();
        var tab = new LaunchTabViewModel(new LaunchTabDependencies
        {
            ProcessRunner = new OrkeonProcessRunner(
                launcher, new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
            TargetProbe = probe,
            Directories = new FakeDirectoryProbe(),
            HistoryStore = new FakeLaunchHistoryStore(),
            SettingsStore = new FakeAppSettingsStore(),
        });
        return (tab, launcher);
    }

    private string WriteCrew(params string[] roles)
    {
        Directory.CreateDirectory(_root);
        var crew = Path.Combine(_root, "team.yaml");
        File.WriteAllText(crew, "agents:\n" + string.Concat(roles.Select(role => $"  - role: {role}\n")));
        return crew;
    }

    private static ProcessOutputLine Completed(string role) => ProcessOutputLine.Now(
        ProcessOutputChannel.StandardOutput,
        $$"""{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"t-{{role}}","agentRole":"{{role}}","success":true,"durationMs":900}""");

    private static ProcessOutputLine Capped(string taskId, string role) => ProcessOutputLine.Now(
        ProcessOutputChannel.StandardOutput,
        $$"""{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"{{taskId}}","agentRole":"{{role}}","success":true,"durationMs":900,"endedOnIterationCap":true}""");

    [Fact]
    public void The_command_breaks_only_between_two_options()
    {
        var segments = LaunchTabViewModel.SplitAtOptions(
            @"orkeon run C:\teams\tri --mount C:\data:/factures:rw --allow-external-mounts --events jsonl");

        Assert.Equal(
            [@"orkeon run C:\teams\tri", @"--mount C:\data:/factures:rw", "--allow-external-mounts", "--events jsonl"],
            segments);
    }

    [Fact]
    public void A_quoted_value_is_never_split_and_no_command_is_no_segment()
    {
        Assert.Equal(
            ["orkeon run \"C:\\my --team\"", "--events jsonl"],
            LaunchTabViewModel.SplitAtOptions("orkeon run \"C:\\my --team\" --events jsonl"));
        Assert.Empty(LaunchTabViewModel.SplitAtOptions(null));
        Assert.Empty(LaunchTabViewModel.SplitAtOptions("  "));
    }

    [Fact]
    public void The_shown_command_breaks_only_before_an_option_and_copies_back_verbatim()
    {
        var (tab, _) = Build(new FakeTargetProbe().WithFile("/crews/team.yaml"));

        tab.Target.Select("/crews/team.yaml");

        var display = tab.CommandLineDisplayText;
        Assert.NotNull(tab.CommandLinePreview);
        Assert.Equal(tab.CommandLinePreview, LaunchTabViewModel.FromNoBreakDisplay(display));
        // Every plain space left is a break opportunity, and each one stands before an option.
        var breaks = display.Split(' ');
        Assert.Equal(LaunchTabViewModel.SplitAtOptions(tab.CommandLinePreview).Count, breaks.Length);
        Assert.All(breaks.Skip(1), part => Assert.StartsWith("-", part, StringComparison.Ordinal));
    }

    [Fact]
    public void Inside_an_option_its_value_never_breaks()
    {
        var display = LaunchTabViewModel.ToNoBreakDisplay(@"orkeon run C:\t --mount C:\d:/f:rw --events jsonl");

        Assert.Equal(2, display.Count(c => c == ' '));
        Assert.Contains("-\u2060-\u2060mount\u00A0C:\\\u2060d:/\u2060f:rw", display, StringComparison.Ordinal);
        Assert.Equal(@"orkeon run C:\t --mount C:\d:/f:rw --events jsonl", LaunchTabViewModel.FromNoBreakDisplay(display));
        Assert.Equal(string.Empty, LaunchTabViewModel.ToNoBreakDisplay(null));
    }

    [Fact]
    public void Before_a_launch_the_cards_name_the_agents_and_keep_their_identifier()
    {
        var crew = WriteCrew("analyste", "Writer");
        var (tab, _) = Build(new FakeTargetProbe().WithFile(crew));

        tab.Target.Select(crew);

        Assert.Equal(["Analyste", "Writer"], tab.AgentCards.Select(card => card.DisplayName));
        Assert.Equal(["analyste", "Writer"], tab.AgentCards.Select(card => card.Identifier));
        Assert.True(tab.AgentCards[0].HasIdentifier);
        Assert.False(tab.AgentCards[1].HasIdentifier);
        Assert.All(tab.AgentCards, card => Assert.Equal("waiting", card.State));
    }

    [Fact]
    public async Task The_runs_spelling_of_an_agent_is_one_card_and_an_agent_never_started_reads_not_run()
    {
        var crew = WriteCrew("analyste", "redacteur", "relecteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(Completed("Analyste"));
        launcher.OutputToEmit.Add(Completed("Rédacteur"));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Analyste", "Rédacteur", "Relecteur"], tab.AgentCards.Select(card => card.DisplayName));
        Assert.Equal(["analyste", "redacteur", "relecteur"], tab.AgentCards.Select(card => card.Identifier));
        Assert.Equal(["done", "done", "notrun"], tab.AgentCards.Select(card => card.State));
        // The lanes themselves are untouched: the cards are a reading of them.
        Assert.Equal(4, tab.AgentLanes.Count);
    }

    [Fact]
    public async Task A_success_with_an_agent_not_run_says_so_in_its_badge_with_the_warning_tone()
    {
        var crew = WriteCrew("analyste", "redacteur", "relecteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(Completed("Analyste"));
        launcher.OutputToEmit.Add(Completed("Rédacteur"));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Succeeded · 1 agent not run", tab.RunBadgeText);
        Assert.Equal("warn", tab.RunBadgeTone);
    }

    [Fact]
    public async Task A_success_with_a_task_ended_on_its_iteration_cap_says_so_in_its_badge_and_on_the_task()
    {
        var crew = WriteCrew("analyste", "redacteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(Completed("Analyste"));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"t-redacteur","agentRole":"Rédacteur","success":true,"durationMs":900,"endedOnIterationCap":true}"""));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Succeeded · 1 task cut short", tab.RunBadgeText);
        Assert.Equal("warn", tab.RunBadgeTone);
        Assert.Equal([false, true], tab.Progress.Tasks.Select(task => task.EndedOnIterationCap));
        Assert.Equal(string.Empty, tab.Progress.Tasks[0].CapNote);
        Assert.StartsWith("Task cut short:", tab.Progress.Tasks[1].CapNote, StringComparison.Ordinal);
        // The novice has no tasks' table: the agent's card is where it reads.
        Assert.Equal(["done", "capped"], tab.AgentCards.Select(card => card.State));
        Assert.Equal("Cut short: check its files", tab.AgentCards[1].StatusText);
    }

    [Fact]
    public async Task An_agent_whose_earlier_task_ended_on_its_iteration_cap_stays_marked_after_a_later_plain_success()
    {
        var crew = WriteCrew("redacteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"t-1","agentRole":"Rédacteur","success":true,"durationMs":900,"endedOnIterationCap":true}"""));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"t-2","agentRole":"Rédacteur","success":true,"durationMs":900}"""));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("capped", Assert.Single(tab.AgentCards).State);
        Assert.Equal("Succeeded · 1 task cut short", tab.RunBadgeText);
    }

    [Fact]
    public async Task The_badge_says_both_an_agent_not_run_and_a_task_cut_short()
    {
        var crew = WriteCrew("analyste", "redacteur", "relecteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(Completed("Analyste"));
        launcher.OutputToEmit.Add(Capped("t-redacteur", "Rédacteur"));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["done", "capped", "notrun"], tab.AgentCards.Select(card => card.State));
        Assert.Equal("Succeeded · 1 agent not run · 1 task cut short", tab.RunBadgeText);
        Assert.Equal("warn", tab.RunBadgeTone);
    }

    [Fact]
    public async Task The_runs_own_count_of_capped_tasks_is_the_badges_even_over_the_lines_read()
    {
        var crew = WriteCrew("redacteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(Completed("Rédacteur"));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":3,"ts":"t","kind":"run.finished","success":true,"exitCode":0,"tokens":10,"durationMs":5,"cappedTasks":2}"""));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Succeeded · 2 tasks cut short", tab.RunBadgeText);
        Assert.Equal("warn", tab.RunBadgeTone);
    }

    [Fact]
    public async Task Without_a_count_from_the_run_a_retry_that_went_through_leaves_a_plain_success()
    {
        var crew = WriteCrew("redacteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(Capped("t-1", "Rédacteur"));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"t-1","agentRole":"Rédacteur","success":true,"durationMs":900}"""));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("done", Assert.Single(tab.AgentCards).State);
        Assert.Equal("ok", tab.RunBadgeTone);
    }

    [Fact]
    public async Task An_agent_the_run_spells_two_ways_stays_marked_when_only_its_first_spelling_was_cut_short()
    {
        var crew = WriteCrew("redacteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(Capped("t-1", "Redacteur"));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"t-2","agentRole":"Rédacteur","success":true,"durationMs":900}"""));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        var card = Assert.Single(tab.AgentCards);
        Assert.Equal("capped", card.State);
        Assert.Equal("Cut short: check its files", card.StatusText);
    }

    [Fact]
    public async Task A_task_whose_last_attempt_went_through_is_not_said_cut_short()
    {
        var crew = WriteCrew("redacteur");
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile(crew));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"t-1","agentRole":"Rédacteur","success":true,"durationMs":900,"endedOnIterationCap":true}"""));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"t-1","agentRole":"Rédacteur","success":true,"durationMs":900,"endedOnIterationCap":false}"""));
        launcher.OutputToEmit.Add(ProcessOutputLine.Now(
            ProcessOutputChannel.StandardOutput,
            """{"v":2,"seq":3,"ts":"t","kind":"run.finished","success":true,"exitCode":0,"tokens":10,"durationMs":5,"cappedTasks":0}"""));
        tab.Target.Select(crew);

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("done", Assert.Single(tab.AgentCards).State);
        Assert.Equal("ok", tab.RunBadgeTone);
    }

    [Fact]
    public async Task While_the_run_lives_its_announcement_and_the_ready_verdict_are_not_said()
    {
        var (tab, launcher) = Build(new FakeTargetProbe().WithFile("/crews/team.yaml"));
        tab.Target.Select("/crews/team.yaml");
        Assert.True(tab.ShowsValidationSummary);
        Assert.False(tab.ShowsFinishedSubtitle);

        bool? statusWhileRunning = null, verdictWhileRunning = null, subtitleWhileRunning = null;
        string? messageWhileRunning = null;
        launcher.WhileRunning = () =>
        {
            messageWhileRunning = tab.StatusMessage;
            statusWhileRunning = tab.ShowsStatusMessage;
            verdictWhileRunning = tab.ShowsValidationSummary;
            subtitleWhileRunning = tab.ShowsFinishedSubtitle;
        };

        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(messageWhileRunning));
        Assert.False(statusWhileRunning);
        Assert.False(verdictWhileRunning);
        Assert.False(subtitleWhileRunning);
        Assert.False(tab.ShowsValidationSummary);
        Assert.True(tab.ShowsFinishedSubtitle);
        Assert.Equal(0, tab.ExitCode);
    }

    [Fact]
    public void The_journal_is_open_until_its_fold_closes_it()
    {
        var (tab, _) = Build(new FakeTargetProbe());
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(tab.IsJournalOpen);
        tab.IsJournalOpen = false;

        Assert.False(tab.IsJournalOpen);
        Assert.Contains(nameof(LaunchTabViewModel.IsJournalOpen), raised);
    }
}
