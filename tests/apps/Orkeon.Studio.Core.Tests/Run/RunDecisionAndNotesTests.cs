using Orkeon.Studio.Core.Events;
using Orkeon.Studio.Core.Run;

namespace Orkeon.Studio.Core.Tests.Run;

/// <summary>
/// What a run says of the decision models and of what it did around its agents' model calls: the
/// circuit it follows (<c>run.decision</c>), who answered a decision task (<c>decisionSource</c>), the
/// profile and the thinking level System One picked or did not, the skills carried, a context compacted
/// or pruned. Each is read off its event; an older CLI that writes none leaves them empty.
/// </summary>
public sealed class RunDecisionAndNotesTests
{
    private static RunProgressModel Fold(params string[] lines)
    {
        var model = new RunProgressModel();
        foreach (var line in lines)
        {
            Assert.True(OrkeonEventParser.TryParse(line, out var orkeonEvent), line);
            model.Apply(orkeonEvent!);
        }

        return model;
    }

    [Fact]
    public void A_run_that_says_nothing_of_the_decision_models_has_no_circuit_and_no_note()
    {
        var model = Fold("""{"v":2,"seq":1,"ts":"t","kind":"run.started","target":"crew.yaml"}""");

        Assert.Null(model.DecisionCircuit);
        Assert.Empty(model.AgentNotes);
    }

    [Fact]
    public void The_circuit_is_read_with_the_crews_mode_and_the_cause_it_is_off_for()
    {
        var on = Fold("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"required","circuit":"on"}""");
        Assert.Equal(new RunDecisionCircuit("required", true, null), on.DecisionCircuit);

        var off = Fold("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"optional","circuit":"off","cause":"switch"}""");
        Assert.Equal(new RunDecisionCircuit("optional", false, "switch"), off.DecisionCircuit);
    }

    [Fact]
    public void A_decision_task_says_who_answered_it()
    {
        var model = Fold(
            """{"v":2,"seq":1,"ts":"t","kind":"task.completed","taskId":"pick","success":true,"isDecision":true,"defaulted":false,"decisionSource":"relay"}""",
            """{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"read","success":true}""");

        Assert.Equal("relay", model.Tasks[0].DecisionSource);
        Assert.Null(model.Tasks[1].DecisionSource);
    }

    [Fact]
    public void A_profile_is_picked_only_when_the_run_says_the_model_chose_it()
    {
        var model = Fold(
            """{"v":2,"seq":1,"ts":"t","kind":"agent.route","taskId":"read","agentRole":"Analyst","profile":"strong","reason":"model","picked":"strong","probability":0.82,"ladder":["fast","strong"]}""",
            """{"v":2,"seq":2,"ts":"t","kind":"agent.route","taskId":"write","agentRole":"Writer","profile":"default","reason":"low-confidence","picked":"strong","probability":0.41,"ladder":["fast","strong"]}""");

        var picked = model.AgentNotes[0];
        Assert.Equal(RunAgentNoteKind.Route, picked.Kind);
        Assert.Equal("Analyst", picked.AgentRole);
        Assert.Equal("strong", picked.Value);
        Assert.True(picked.Picked);
        Assert.Equal(0.82, picked.Probability);

        // The model named a profile, too unsure for the run to follow it.
        var held = model.AgentNotes[1];
        Assert.Equal("default", held.Value);
        Assert.False(held.Picked);
        Assert.Equal("low-confidence", held.Reason);
    }

    [Fact]
    public void A_thinking_level_is_read_with_the_level_it_was_raised_from()
    {
        var model = Fold(
            """{"v":2,"seq":1,"ts":"t","kind":"agent.effort","taskId":"read","agentRole":"Analyst","level":"low","reason":"model","picked":"low","probability":0.7,"levels":["low","medium","high"]}""",
            """{"v":2,"seq":2,"ts":"t","kind":"agent.effort","taskId":"read","agentRole":"Analyst","level":"medium","previous":"low","reason":"raised","levels":["low","medium","high"]}""");

        Assert.Equal(RunAgentNoteKind.Effort, model.AgentNotes[0].Kind);
        Assert.True(model.AgentNotes[0].Picked);
        Assert.Null(model.AgentNotes[0].Previous);
        Assert.Equal("medium", model.AgentNotes[1].Value);
        Assert.Equal("low", model.AgentNotes[1].Previous);
        Assert.False(model.AgentNotes[1].Picked);
    }

    [Fact]
    public void Skills_a_compaction_and_a_pruning_are_read_with_their_figures()
    {
        var model = Fold(
            """{"v":2,"seq":1,"ts":"t","kind":"agent.skills","taskId":"read","agentRole":"Analyst","mode":"fallback","skills":["/workspace/.orkeon/skills/triage.md"],"offered":4,"cut":[],"reason":"circuit-off"}""",
            """{"v":2,"seq":2,"ts":"t","kind":"agent.compacted","taskId":"read","agentRole":"Analyst","outcome":"compacted","tokensBefore":152000,"tokensAfter":41000,"window":200000,"windowSource":"table","keptMessages":12,"summaryTokens":900}""",
            """{"v":2,"seq":3,"ts":"t","kind":"agent.pruned","taskId":"read","agentRole":"Analyst","estimatedTokens":90000,"thresholdTokens":80000,"offered":9,"judged":9,"kept":5,"cut":4,"dropped":0,"freedTokens":21000}""");

        var skills = model.AgentNotes[0];
        Assert.Equal(RunAgentNoteKind.Skills, skills.Kind);
        Assert.Equal("fallback", skills.Value);
        Assert.Equal("circuit-off", skills.Reason);
        Assert.Equal(["/workspace/.orkeon/skills/triage.md"], skills.Names);

        var compacted = model.AgentNotes[1];
        Assert.Equal(RunAgentNoteKind.Compacted, compacted.Kind);
        Assert.Equal("compacted", compacted.Value);
        Assert.Equal(152000, compacted.TokensBefore);
        Assert.Equal(41000, compacted.TokensAfter);

        var pruned = model.AgentNotes[2];
        Assert.Equal(RunAgentNoteKind.Pruned, pruned.Kind);
        Assert.Equal(4, pruned.Count);
        Assert.Equal(21000, pruned.TokensAfter);

        // Results dropped left the context as those cut did: both are counted.
        var dropped = Fold("""{"v":2,"seq":1,"ts":"t","kind":"agent.pruned","taskId":"read","agentRole":"Analyst","cut":1,"dropped":3,"freedTokens":9000}""");
        Assert.Equal(4, dropped.AgentNotes[0].Count);
    }

    /// <summary>
    /// The engine says the circuit is off for a decision provider with no API key (Orkeon #451): the cause
    /// is kept as written and has its own sentence, never the unknown fallback.
    /// </summary>
    [Fact]
    public void A_circuit_off_for_a_missing_key_has_its_own_sentence()
    {
        var model = Fold("""{"v":2,"seq":1,"ts":"t","kind":"run.decision","mode":"optional","circuit":"off","cause":"no-key"}""");

        Assert.Equal(new RunDecisionCircuit("optional", false, "no-key"), model.DecisionCircuit);
        Assert.Equal(Orkeon.Studio.Core.Localization.StudioStringKeys.RunNoteCauseNoKey, DecisionCircuitCauseKeys.KeyOf("no-key"));
    }

    /// <summary>
    /// Where a declarative crew picks a profile or a level before any task, or prunes outside one
    /// (Orkeon #460, #461), the event carries no <c>taskId</c>, only the agent's role: the note is kept
    /// under that role. A route can stop at the ladder's <c>floor</c> or <c>ceiling</c>, and a level set
    /// for the first time names no <c>previous</c>.
    /// </summary>
    [Fact]
    public void Notes_without_a_task_are_kept_under_the_agents_role()
    {
        var model = Fold(
            """{"v":2,"seq":1,"ts":"t","kind":"agent.route","agentRole":"Analyst","profile":"fast","reason":"floor","ladder":["fast","strong"]}""",
            """{"v":2,"seq":2,"ts":"t","kind":"agent.route","agentRole":"Writer","profile":"strong","reason":"ceiling","ladder":["fast","strong"]}""",
            """{"v":2,"seq":3,"ts":"t","kind":"agent.effort","agentRole":"Analyst","level":"low","reason":"model","picked":"low","probability":0.7}""",
            """{"v":2,"seq":4,"ts":"t","kind":"agent.pruned","agentRole":"Analyst","cut":2,"dropped":0,"freedTokens":5000}""");

        Assert.Equal(4, model.AgentNotes.Count);
        Assert.All(model.AgentNotes, note => Assert.Null(note.TaskId));
        Assert.Equal(["Analyst", "Writer", "Analyst", "Analyst"], model.AgentNotes.Select(note => note.AgentRole));
        Assert.Equal(("floor", "fast"), (model.AgentNotes[0].Reason, model.AgentNotes[0].Value));
        Assert.Equal(("ceiling", "strong"), (model.AgentNotes[1].Reason, model.AgentNotes[1].Value));
        Assert.Null(model.AgentNotes[2].Previous);
        Assert.Equal(2, model.AgentNotes[3].Count);
        Assert.Empty(model.Tasks);
    }

    [Fact]
    public void Each_of_them_tells_the_screen_it_changed()
    {
        var model = new RunProgressModel();
        var raised = new List<string?>();
        model.Changed += (_, args) => raised.Add(args.Kind);
        foreach (var kind in new[] { "run.decision", "agent.route", "agent.effort", "agent.skills", "agent.compacted", "agent.pruned" })
        {
            Assert.True(OrkeonEventParser.TryParse($$"""{"v":2,"seq":1,"ts":"t","kind":"{{kind}}"}""", out var orkeonEvent));
            model.Apply(orkeonEvent!);
        }

        Assert.Equal(6, raised.Count);
    }
}
