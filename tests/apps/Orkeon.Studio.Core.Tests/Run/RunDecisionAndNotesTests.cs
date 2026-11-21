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
