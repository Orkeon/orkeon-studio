using Orkeon.Studio.Core.Run;
using Orkeon.Studio.Wpf.ViewModels.Launch;

namespace Orkeon.Studio.Wpf.Tests.Launch;

/// <summary>
/// The progress panel of a watched run (BUS-06). The folding itself is Core's; what is tested
/// here is the projection a screen binds to — and the two refusals that keep it honest.
/// </summary>
public class RunProgressViewModelTests
{
    private static RunProgressViewModel Watching(Func<string, string, bool>? answer = null)
    {
        var panel = new RunProgressViewModel();
        panel.Reset(answer ?? ((_, _) => true));
        return panel;
    }

    [Fact]
    public void A_line_the_panel_cannot_read_is_handed_back_to_the_caller()
    {
        // The caller shows it raw. A line nobody can read must never be lost.
        var panel = Watching();

        Assert.False(panel.TryApply("this is not a protocol line"));
        Assert.False(panel.TryApply(""));
        Assert.True(panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"cost.updated","tokens":10}"""));
    }

    [Fact]
    public void Tasks_and_cost_reach_the_bindable_surface()
    {
        var panel = Watching();

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"t1","agentRole":"analyst","success":true,"durationMs":1500,"tokens":340}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"cost.updated","tokens":340,"usd":0.004}""");

        var task = Assert.Single(panel.Tasks);
        Assert.Equal("analyst", task.Title);
        Assert.True(task.Success);
        Assert.Contains("1", task.Duration, StringComparison.Ordinal);
        Assert.NotEmpty(task.Tokens);
        Assert.Contains("340", panel.CostSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_without_a_duration_shows_a_dash_not_zero_seconds()
    {
        var panel = Watching();

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"t1","agentRole":"analyst","success":true}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"t2","agentRole":"writer","success":false,"skipped":true,"durationMs":0}""");

        Assert.All(panel.Tasks, task => Assert.Equal("—", task.Duration));
    }

    [Fact]
    public void A_running_task_has_a_row_until_it_finishes()
    {
        // STUDIO-17: the row the screen lacked — between the start and the close, the panel
        // used to show either "nothing reported yet" or only the tasks already done.
        var panel = Watching();
        panel.TryApply("""{"v":2,"seq":1,"ts":"2026-09-19T10:31:02Z","kind":"task.started","taskId":"t1","agentRole":"analyst"}""");

        var running = Assert.Single(panel.RunningTasks);
        Assert.Equal("analyst", running.Title);
        Assert.StartsWith("since ", running.Since, StringComparison.Ordinal);
        Assert.EndsWith(":02", running.Since, StringComparison.Ordinal);
        Assert.True(panel.HasRunningTasks);
        Assert.Equal("0 task(s) finished, 1 in progress.", panel.Summary);

        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"t1","agentRole":"analyst","success":true,"durationMs":1500,"tokens":340,"toolCalls":0}""");

        Assert.Empty(panel.RunningTasks);
        Assert.False(panel.HasRunningTasks);
        Assert.Equal("1 task(s) finished.", panel.Summary);
        Assert.Equal("0 tool call(s)", Assert.Single(panel.Tasks).ToolCalls);
    }

    [Fact]
    public void A_start_with_no_readable_time_shows_no_time()
    {
        var panel = Watching();
        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"task.started","taskId":"t1"}""");

        var running = Assert.Single(panel.RunningTasks);
        Assert.Equal("t1", running.Title);
        Assert.Empty(running.Since);
    }

    [Fact]
    public void The_tool_at_work_is_a_line_of_its_own()
    {
        var panel = Watching();
        Assert.False(panel.HasActivity);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","correlationId":"k-1","kind":"tool.called","toolName":"pdf_reader"}""");
        Assert.True(panel.HasActivity);
        Assert.Equal("Tool pdf_reader running…", panel.ActivityLine);

        panel.TryApply("""{"v":2,"seq":2,"ts":"t","correlationId":"k-1","kind":"tool.returned","toolName":"pdf_reader","success":true,"durationMs":3}""");
        Assert.False(panel.HasActivity);
        Assert.Empty(panel.ActivityLine);
    }

    [Fact]
    public void A_silent_run_says_so_instead_of_implying_progress()
    {
        // "No news" is not "going well", and the summary has to keep them apart.
        var panel = Watching();

        Assert.Equal("Nothing reported yet.", panel.Summary);
        Assert.Empty(panel.CostSummary);
        Assert.False(panel.IsAsking);
    }

    [Fact]
    public void A_question_is_answered_on_screen_and_then_stops_being_asked()
    {
        var sent = new List<(string Id, string Value)>();
        var panel = Watching((id, value) => { sent.Add((id, value)); return true; });

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","correlationId":"c-1","kind":"input.needed","inputKind":"text","prompt":"Your name?"}""");

        Assert.True(panel.IsAsking);
        Assert.Equal("Your name?", panel.QuestionPrompt);

        panel.AnswerText = "Ada";
        panel.AnswerCommand.Execute(null);

        Assert.Equal(("c-1", "Ada"), Assert.Single(sent));
        Assert.False(panel.IsAsking);
        Assert.Empty(panel.AnswerText);
    }

    [Fact]
    public void An_answer_that_could_not_be_sent_leaves_the_question_open()
    {
        // Clearing it anyway would pretend the answer landed. The run is still waiting.
        var panel = Watching((_, _) => false);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","correlationId":"c-1","kind":"input.needed","inputKind":"confirm","prompt":"Continue?"}""");
        panel.AnswerCommand.Execute(null);

        Assert.True(panel.IsAsking);
    }

    [Fact]
    public void A_choice_sends_the_option_the_user_clicked()
    {
        var sent = new List<(string Id, string Value)>();
        var panel = Watching((id, value) => { sent.Add((id, value)); return true; });

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","correlationId":"c-1","kind":"input.needed","inputKind":"choice","prompt":"Format?","choices":["markdown","json"]}""");

        Assert.True(panel.IsChoice);
        Assert.Equal(["markdown", "json"], panel.QuestionChoices);

        panel.ChooseCommand.Execute("json");

        Assert.Equal(("c-1", "json"), Assert.Single(sent));
        Assert.False(panel.IsAsking);
    }

    [Fact]
    public void A_confirmation_left_blank_reads_as_yes_because_the_user_pressed_the_button()
    {
        // Pressing "Answer" on a yes/no question with nothing typed is an act, not silence —
        // unlike a closed channel, which BUS-04 refuses.
        var sent = new List<(string Id, string Value)>();
        var panel = Watching((id, value) => { sent.Add((id, value)); return true; });

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","correlationId":"c-1","kind":"input.needed","inputKind":"confirm","prompt":"Continue?"}""");
        panel.AnswerCommand.Execute(null);

        Assert.Equal("yes", Assert.Single(sent).Value);
    }

    [Fact]
    public void Resetting_forgets_the_previous_run()
    {
        var panel = Watching();
        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"t1","success":true,"durationMs":10}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"hub.message","correlationId":"r-1","expectsReply":true,"payload":{}}""");
        panel.TryApply("""{"v":2,"seq":3,"ts":"t","kind":"task.started","taskId":"t2","agentRole":"writer"}""");
        Assert.Single(panel.Tasks);
        Assert.Single(panel.RunningTasks);
        Assert.True(panel.HasAgentRequest);

        panel.Reset((_, _) => true);

        Assert.Empty(panel.Tasks);
        Assert.Empty(panel.RunningTasks);
        Assert.Empty(panel.CostSummary);
        Assert.False(panel.IsAsking);
        Assert.False(panel.HasAgentRequest);
        Assert.Empty(panel.HubMessages);
    }

    [Fact]
    public void An_agent_request_is_replied_on_screen_and_then_stops_waiting()
    {
        var sent = new List<(string Id, string Payload)>();
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true, (id, payload) => { sent.Add((id, payload)); return true; });

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"hub.message","correlationId":"r-1","expectsReply":true,"from":"agent://crew/worker","payload":{"question":"go?"}}""");

        Assert.True(panel.HasAgentRequest);
        Assert.Equal("agent://crew/worker", panel.AgentRequestFrom);
        Assert.Contains("go?", panel.AgentRequestPayload, StringComparison.Ordinal);

        panel.ReplyText = "yes, go";
        panel.ReplyCommand.Execute(null);

        Assert.Equal(("r-1", "yes, go"), Assert.Single(sent));
        Assert.False(panel.HasAgentRequest);
        Assert.Empty(panel.ReplyText);
    }

    [Fact]
    public void A_reply_that_could_not_be_sent_leaves_the_request_open()
    {
        // Same rule as an unanswerable question: a write nobody read must not pretend.
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true, (_, _) => false);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"hub.message","correlationId":"r-1","expectsReply":true,"payload":{}}""");
        panel.ReplyCommand.Execute(null);

        Assert.True(panel.HasAgentRequest);
    }

    [Fact]
    public void Watch_only_shows_the_request_but_offers_no_reply()
    {
        // No reply channel: the request is visible (honesty) but the command is inert —
        // accepting a reply that goes nowhere would be worse than refusing it.
        var panel = Watching();   // Reset(answer) with no reply channel

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"hub.message","correlationId":"r-1","expectsReply":true,"payload":{}}""");

        Assert.True(panel.HasAgentRequest);
        Assert.False(panel.ReplyCommand.CanExecute(null));
    }

    [Fact]
    public void Hub_traffic_is_listed_instead_of_vanishing()
    {
        // A post used to be parsed and then shown nowhere — the agent believed it was read.
        var panel = Watching();

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"hub.message","from":"agent://crew/worker","payload":{"note":"fyi"}}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"hub.message","topic":"news","from":"crew://crew","payload":{"n":1}}""");

        Assert.True(panel.HasHubMessages);
        Assert.Equal(2, panel.HubMessages.Count);
        Assert.Contains("agent://crew/worker", panel.HubMessages[0], StringComparison.Ordinal);
        Assert.StartsWith("[news] ", panel.HubMessages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_run_says_it_failed()
    {
        var panel = Watching();

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"error","code":"llm.unreachable","message":"no endpoint","recoverable":false}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"run.finished","success":false,"exitCode":1}""");

        Assert.True(panel.HasError);
        Assert.Contains("llm.unreachable", panel.LastError, StringComparison.Ordinal);
        Assert.Equal("Finished with a failure.", panel.Summary);
    }

    /// <summary>
    /// The status bar reads the live state off the panel's model (STUDIO-34). The model is
    /// replaced on every reset, so the replacement is announced: a reader holding the old one
    /// would watch a run that is over.
    /// </summary>
    [Fact]
    public void The_panel_hands_out_its_model_and_says_when_a_reset_replaces_it()
    {
        var panel = new RunProgressViewModel();
        var before = panel.Model;
        var raised = new List<string?>();
        panel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        panel.Reset(answer: null);

        Assert.NotSame(before, panel.Model);
        Assert.Contains(nameof(RunProgressViewModel.Model), raised);
    }

    [Fact]
    public void The_model_reads_the_clock_the_panel_was_given()
    {
        var clock = new Doubles.StubTimeProvider { Now = new DateTimeOffset(2026, 9, 24, 10, 30, 0, TimeSpan.Zero) };
        var panel = new RunProgressViewModel(timeProvider: clock);
        panel.Reset(answer: null);

        panel.TryApply("""{"v":2,"seq":1,"ts":"2026-09-24T10:29:30Z","kind":"run.started"}""");

        Assert.Equal(TimeSpan.FromSeconds(30), panel.Model.Elapsed);
    }

    [Fact]
    public void The_decision_models_and_what_was_done_around_the_agents_are_listed_one_line_each()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);
        Assert.False(panel.HasAgentNotes);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"optional","circuit":"off","cause":"switch"}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"agent.route","taskId":"read","agentRole":"Analyst","profile":"strong","reason":"model","picked":"strong","probability":0.82}""");
        panel.TryApply("""{"v":2,"seq":3,"ts":"t","kind":"agent.route","taskId":"write","agentRole":"Writer","profile":"default","reason":"circuit-off"}""");
        panel.TryApply("""{"v":2,"seq":4,"ts":"t","kind":"agent.effort","taskId":"read","agentRole":"Analyst","level":"medium","previous":"low","reason":"raised"}""");
        panel.TryApply("""{"v":2,"seq":5,"ts":"t","kind":"agent.skills","taskId":"read","agentRole":"Analyst","mode":"fallback","skills":[],"reason":"circuit-off"}""");
        panel.TryApply("""{"v":2,"seq":6,"ts":"t","kind":"agent.compacted","taskId":"read","agentRole":"Analyst","outcome":"failed","tokensBefore":152000,"tokensAfter":152000}""");

        Assert.True(panel.HasAgentNotes);
        Assert.Equal(
            [
                "Decision models off: this run goes without them (team mode: optional).",
                "Analyst: System One picked the profile 'strong' (82 %).",
                "Writer: profile 'default', not picked by System One (circuit-off).",
                "Analyst: thinking level raised from low to medium after a failed attempt.",
                "Analyst: no skill (circuit-off).",
                "Analyst: the context could not be compacted (failed).",
            ],
            panel.AgentNotes);

        // A new run starts from an empty panel.
        panel.Reset((_, _) => true);
        Assert.False(panel.HasAgentNotes);
    }

    /// <summary>
    /// A pick made outside any task (Orkeon #460) and a pruning of a script's own loop (#461) come
    /// without a <c>taskId</c>: each line names the agent's role. A route held to a bound of the ladder
    /// says so, and a level set for the first time is no raise.
    /// </summary>
    [Fact]
    public void Notes_without_a_task_name_the_agent_and_a_clamped_route_says_its_bound()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        Assert.True(panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"agent.route","agentRole":"Analyst","profile":"fast","reason":"floor","picked":"tiny","probability":0.9}"""));
        Assert.True(panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"agent.route","agentRole":"Writer","profile":"strong","reason":"ceiling"}"""));
        Assert.True(panel.TryApply("""{"v":2,"seq":3,"ts":"t","kind":"agent.effort","agentRole":"Analyst","level":"low","reason":"model","picked":"low","probability":0.7}"""));
        Assert.True(panel.TryApply("""{"v":2,"seq":4,"ts":"t","kind":"agent.pruned","agentRole":"Analyst","cut":2,"dropped":0,"freedTokens":5000}"""));

        Assert.Equal(
            [
                "Analyst: profile 'fast', the ladder's floor System One's pick is held to.",
                "Writer: profile 'strong', the ladder's ceiling System One's pick is held to.",
                "Analyst: System One picked the thinking level low (70 %).",
                "Analyst: 2 tool results pruned, about " + 5000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " tokens freed.",
            ],
            panel.AgentNotes);
    }

    [Fact]
    public void A_decision_task_its_relay_answered_says_so()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"pick","success":true,"isDecision":true,"defaulted":false,"decisionSource":"relay"}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"again","success":true,"isDecision":true,"defaulted":false,"decisionSource":"model"}""");

        Assert.Equal("decision (relay)", panel.Tasks[0].DecisionLabel);
        Assert.Equal("decision", panel.Tasks[1].DecisionLabel);
    }

    [Fact]
    public void The_engines_own_decision_reads_Decision_and_says_no_plain_label_twice()
    {
        var panel = Watching();

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"task.started","taskId":"pick","agentRole":"decision"}""");
        Assert.Equal("Decision", Assert.Single(panel.RunningTasks).Title);

        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"pick","agentRole":"decision","success":true,"isDecision":true,"defaulted":false,"decisionSource":"model"}""");
        panel.TryApply("""{"v":2,"seq":3,"ts":"t","kind":"task.completed","taskId":"late","agentRole":"decision","success":true,"isDecision":true,"defaulted":true}""");
        panel.TryApply("""{"v":2,"seq":4,"ts":"t","kind":"task.completed","taskId":"relayed","agentRole":"judge","success":true,"isDecision":true,"defaulted":false,"decisionSource":"relay"}""");

        Assert.Equal(["Decision", "Decision", "judge"], panel.Tasks.Select(task => task.Title));
        Assert.Equal(["", "decision (defaulted)", "decision (relay)"], panel.Tasks.Select(task => task.DecisionLabel));
    }

    [Theory]
    [InlineData(
        """condition of task 01M82GYVEZ2D5B4TB5BBXTJ3Q3 did not hold on decision task controle-couverture: {\"couverture\":{\"chosen\":\"complet\",\"probabilities\":{\"complet\":0.92,\"partiel\":0.08},\"confidence\":0.84}}""",
        "Skipped: decision controle-couverture answered couverture = complet")]
    [InlineData("decision controle-couverture did not run", "Skipped: decision controle-couverture was not taken")]
    [InlineData(
        "Task 01M8A (writer) skipped: it depends on task 01M8B, which did not succeed",
        "Skipped: a task it depends on did not succeed")]
    [InlineData("something a newer engine says", "Skipped: something a newer engine says")]
    public void A_skipped_task_says_why_in_words_and_keeps_the_engines_sentence_for_its_tooltip(string reason, string said)
    {
        var panel = Watching();

        panel.TryApply($$"""{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"t","agentRole":"writer","success":false,"skipped":true,"skipReason":"{{reason}}"}""");

        var task = Assert.Single(panel.Tasks);
        Assert.Equal(said, task.SkipReason);
        Assert.Equal(reason.Replace("\\\"", "\"", StringComparison.Ordinal), task.SkipReasonRaw);
    }

    [Fact]
    public void A_task_that_ran_has_no_skip_reason_nor_raw_sentence()
    {
        var panel = Watching();

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"t","agentRole":"writer","success":true,"skipReason":"stale"}""");

        Assert.Equal(string.Empty, Assert.Single(panel.Tasks).SkipReason);
        Assert.Equal(string.Empty, panel.Tasks[0].SkipReasonRaw);
    }

    /// <summary>
    /// Every run states its circuit, a crew that asks nothing of System One included: the line is held
    /// until the run does something it explains, so such a crew's panel stays closed.
    /// </summary>
    [Fact]
    public void The_circuit_of_a_crew_that_states_no_mode_is_said_only_once_the_run_explains_something_with_it()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"unspecified","circuit":"off","cause":"no-provider"}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"read","success":true}""");
        Assert.False(panel.HasAgentNotes);

        panel.TryApply("""{"v":2,"seq":3,"ts":"t","kind":"agent.pruned","taskId":"write","agentRole":"Writer","cut":1,"dropped":2,"freedTokens":900}""");

        Assert.Equal(
            [
                "Decision models off: no decision provider is configured (team mode: unspecified).",
                "Writer: 3 tool results pruned, about 900 tokens freed.",
            ],
            panel.AgentNotes);
    }

    [Fact]
    public void A_long_run_keeps_the_latest_notes_only()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        for (var turn = 1; turn <= RunProgressViewModel.MaxAgentNotes + 25; turn++)
            panel.TryApply($$"""{"v":2,"seq":{{turn}},"ts":"t","kind":"agent.pruned","taskId":"t","agentRole":"A","cut":{{turn}},"dropped":0,"freedTokens":1}""");

        Assert.Equal(RunProgressViewModel.MaxAgentNotes, panel.AgentNotes.Count);
        Assert.StartsWith("A: " + (RunProgressViewModel.MaxAgentNotes + 25).ToString("N0", System.Globalization.CultureInfo.CurrentCulture), panel.AgentNotes[^1], StringComparison.Ordinal);
    }

    /// <summary>A crew that states it runs with the models has its circuit said at once, before any note explains anything with it.</summary>
    [Theory]
    [InlineData("optional")]
    [InlineData("required")]
    public void The_circuit_of_a_crew_that_asks_for_the_models_is_said_at_once(string mode)
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        panel.TryApply($$"""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"{{mode}}","circuit":"off","cause":"settings"}""");

        Assert.True(panel.HasAgentNotes);
        Assert.Equal([$"Decision models off: the settings switch them off (team mode: {mode})."], panel.AgentNotes);
    }

    [Fact]
    public void A_run_with_the_decision_models_says_so()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"required","circuit":"on"}""");

        Assert.Equal(["Decision models on (team mode: required)."], panel.AgentNotes);
    }

    /// <summary>A decision task is something the circuit explains: the line held for a crew that states no mode is said with it.</summary>
    [Fact]
    public void A_decision_task_releases_the_circuit_line_held_for_a_crew_that_states_no_mode()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"unspecified","circuit":"off","cause":"switch"}""");
        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"read","success":true}""");
        Assert.Empty(panel.AgentNotes);

        panel.TryApply("""{"v":2,"seq":3,"ts":"t","kind":"task.completed","taskId":"pick","success":true,"isDecision":true,"defaulted":true}""");

        Assert.Equal(["Decision models off: this run goes without them (team mode: unspecified)."], panel.AgentNotes);

        // Said once: a second decision task adds nothing.
        panel.TryApply("""{"v":2,"seq":4,"ts":"t","kind":"task.completed","taskId":"again","success":true,"isDecision":true,"defaulted":true}""");
        Assert.Single(panel.AgentNotes);
    }

    [Fact]
    public void A_new_run_drops_the_circuit_line_the_previous_one_still_held()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);
        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"unspecified","circuit":"off","cause":"no-provider"}""");
        Assert.Empty(panel.AgentNotes);

        panel.Reset((_, _) => true);
        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"agent.pruned","taskId":"write","agentRole":"Writer","cut":1,"dropped":0,"freedTokens":900}""");

        // The note of the new run stands alone: the held line was the previous run's.
        Assert.Equal(["Writer: 1 tool results pruned, about 900 tokens freed."], panel.AgentNotes);
    }

    [Fact]
    public void A_compaction_that_succeeded_says_what_it_freed_in_the_cultures_figures()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"agent.compacted","taskId":"read","agentRole":"Analyst","outcome":"compacted","tokensBefore":152000,"tokensAfter":41000}""");

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Assert.Equal(
            [$"Analyst: context compacted, from {152000.ToString("N0", culture)} to {41000.ToString("N0", culture)} tokens."],
            panel.AgentNotes);
    }

    /// <summary>
    /// The circuit line explains every « circuit-off » or « relay » note below it: the cap that keeps
    /// the latest notes never evicts it.
    /// </summary>
    [Fact]
    public void A_long_run_keeps_its_circuit_line_above_the_latest_notes()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);
        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"optional","circuit":"off","cause":"switch"}""");

        for (var turn = 1; turn <= RunProgressViewModel.MaxAgentNotes + 25; turn++)
            panel.TryApply($$"""{"v":2,"seq":{{turn + 1}},"ts":"t","kind":"agent.pruned","taskId":"t","agentRole":"A","cut":{{turn}},"dropped":0,"freedTokens":1}""");

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Assert.Equal(RunProgressViewModel.MaxAgentNotes, panel.AgentNotes.Count);
        Assert.Equal("Decision models off: this run goes without them (team mode: optional).", panel.AgentNotes[0]);
        // The oldest notes went, from under it: the second line is the 27th pruning.
        Assert.StartsWith("A: " + 27.ToString("N0", culture), panel.AgentNotes[1], StringComparison.Ordinal);
        Assert.StartsWith("A: " + (RunProgressViewModel.MaxAgentNotes + 25).ToString("N0", culture), panel.AgentNotes[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The circuit line is the first of the list wherever the run was when it stated its circuit: a
    /// statement that follows notes goes above them, the cap never evicts it, and a second statement
    /// takes the place of the first.
    /// </summary>
    [Fact]
    public void A_circuit_stated_after_notes_is_put_first_and_stays_there()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);
        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"agent.pruned","taskId":"t","agentRole":"Early","cut":1,"dropped":0,"freedTokens":1}""");
        Assert.Single(panel.AgentNotes);

        panel.TryApply("""{"v":2,"seq":2,"ts":"t","kind":"run.decision","mode":"optional","circuit":"off","cause":"switch"}""");

        Assert.Equal(2, panel.AgentNotes.Count);
        Assert.Equal("Decision models off: this run goes without them (team mode: optional).", panel.AgentNotes[0]);
        Assert.StartsWith("Early: ", panel.AgentNotes[1], StringComparison.Ordinal);

        for (var turn = 1; turn <= RunProgressViewModel.MaxAgentNotes + 25; turn++)
            panel.TryApply($$"""{"v":2,"seq":{{turn + 2}},"ts":"t","kind":"agent.pruned","taskId":"t","agentRole":"A","cut":{{turn}},"dropped":0,"freedTokens":1}""");

        Assert.Equal(RunProgressViewModel.MaxAgentNotes, panel.AgentNotes.Count);
        Assert.Equal("Decision models off: this run goes without them (team mode: optional).", panel.AgentNotes[0]);
        Assert.StartsWith("A: ", panel.AgentNotes[1], StringComparison.Ordinal);

        panel.TryApply("""{"v":2,"seq":900,"ts":"t","kind":"run.decision","mode":"optional","circuit":"on"}""");
        Assert.Equal(RunProgressViewModel.MaxAgentNotes, panel.AgentNotes.Count);
        Assert.Equal("Decision models on (team mode: optional).", panel.AgentNotes[0]);
        Assert.DoesNotContain("Decision models off: this run goes without them (team mode: optional).", panel.AgentNotes);
    }

    /// <summary>
    /// Every cause the engine defines for a circuit that is off is said in a sentence, the trial's
    /// included; a word it does not define is shown as written.
    /// </summary>
    [Theory]
    [InlineData("switch", "this run goes without them")]
    [InlineData("settings", "the settings switch them off")]
    [InlineData("no-provider", "no decision provider is configured")]
    [InlineData("no-key", "the decision provider has no API key: set its environment variable, or its ApiKey in the settings")]
    [InlineData("trial", "this pass of the trial runs without them")]
    [InlineData("maintenance", "maintenance")]
    public void The_cause_of_a_circuit_off_is_said_in_a_sentence(string cause, string said)
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        panel.TryApply($$"""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"optional","circuit":"off","cause":"{{cause}}"}""");

        Assert.Equal([$"Decision models off: {said} (team mode: optional)."], panel.AgentNotes);
    }

    /// <summary>
    /// The engine's own sentence of an off circuit (<c>detail</c>, Orkeon #467) follows Studio's, in
    /// English as written: it names the variable and the setting the key is read from.
    /// </summary>
    [Fact]
    public void The_engines_detail_of_a_circuit_off_follows_the_causes_sentence()
    {
        var panel = new RunProgressViewModel();
        panel.Reset((_, _) => true);

        panel.TryApply("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"required","circuit":"off","cause":"no-key","detail":"no Liquid API key: set the environment variable LIQUID_API_KEY, or Orkeon:Liquid:ApiKey"}""");

        Assert.Equal(
            [
                "Decision models off: the decision provider has no API key: set its environment variable, or its ApiKey in the settings"
                    + ": no Liquid API key: set the environment variable LIQUID_API_KEY, or Orkeon:Liquid:ApiKey (team mode: required).",
            ],
            panel.AgentNotes);
    }

    /// <summary>The causes the theory above walks are the engine's, all of them.</summary>
    [Fact]
    public void The_causes_said_are_all_the_engine_defines()
    {
        var engine = typeof(Orkeon.Constants.Protocol.DecisionCircuitCauses)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["no-key", "no-provider", "settings", "switch", "trial"], engine);
    }
}

/// <summary>
/// The argv the launch form composes once the screen watches a run rather than tailing it.
/// </summary>
public class LaunchOptionsObservationTests
{
    [Fact]
    public void Watching_is_the_default_and_streaming_is_not()
    {
        var options = new LaunchOptionsViewModel();

        Assert.True(options.WatchProgress);
        Assert.False(options.StreamGeneratedText);
        Assert.True(options.CanStreamGeneratedText);
    }

    [Fact]
    public void Turning_watching_off_takes_streaming_with_it()
    {
        var options = new LaunchOptionsViewModel { WatchProgress = false, StreamGeneratedText = true };

        Assert.False(options.CanStreamGeneratedText);

        var built = options.ToOptions();
        Assert.False(built.Events);
        Assert.False(built.Stream);
        Assert.Null(built.ClientName);
    }

    [Fact]
    public void A_watched_run_names_studio_on_the_hub()
    {
        var built = new LaunchOptionsViewModel { StreamGeneratedText = true }.ToOptions();

        Assert.True(built.Events);
        Assert.True(built.Stream);
        Assert.Equal(LaunchOptionsViewModel.StudioClientName, built.ClientName);
    }

    [Fact]
    public void A_dry_run_asks_for_no_stream_at_all()
    {
        // Validation reports a verdict, not progress: a stream with nothing in it would only
        // make the screen look like it is watching something.
        var built = new LaunchOptionsViewModel().ToOptions(validate: true);

        Assert.False(built.Events);
        Assert.False(built.Stream);
        Assert.Null(built.ClientName);
    }
}
