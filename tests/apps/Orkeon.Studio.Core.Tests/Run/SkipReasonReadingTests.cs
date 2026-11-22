using Orkeon.Studio.Core.Run;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Run;

/// <summary>
/// The <c>skipReason</c> of a skipped task, read back into what a screen says: the four sentences the
/// engine writes, and the role it gives the decision tasks it answers itself, are copies of its sources
/// and pinned there.
/// </summary>
public sealed class SkipReasonReadingTests
{
    [Fact]
    public void A_condition_that_did_not_hold_names_its_decision_and_carries_its_answers()
    {
        var reading = SkipReasonReading.Parse(
            """condition of task 01M82GYVEZ2D5B4TB5BBXTJ3Q3 did not hold on decision task controle-couverture: {"couverture":{"chosen":"complet"},"ton":{"chosen":"neutre"}}""");

        Assert.Equal(SkipReasonKind.ConditionNotHeld, reading.Kind);
        Assert.Equal("controle-couverture", reading.DecisionTask);
        Assert.Equal(["couverture", "ton"], DecisionAnswerReading.Parse(reading.DecisionAnswers).Select(answer => answer.Question));
    }

    [Fact]
    public void A_decision_that_did_not_run_names_it()
    {
        var reading = SkipReasonReading.Parse("decision pick did not run");

        Assert.Equal(new SkipReasonReading(SkipReasonKind.DecisionNotRun, "pick"), reading);
    }

    [Fact]
    public void A_failed_dependency_is_read_without_its_ids()
    {
        Assert.Equal(
            SkipReasonKind.DependencyFailed,
            SkipReasonReading.Parse("Task 01M8A (writer) skipped: it depends on task 01M8B, which did not succeed").Kind);
    }

    [Fact]
    public void A_task_whose_every_dependency_was_condition_skipped_is_read_as_such()
    {
        Assert.Equal(
            new SkipReasonReading(SkipReasonKind.DependenciesConditionSkipped),
            SkipReasonReading.Parse("every dependency was condition skipped"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("the decision went elsewhere")]
    [InlineData("decision did not run")]
    public void Any_other_sentence_is_shown_as_it_came(string? reason)
    {
        Assert.Equal(SkipReasonKind.Other, SkipReasonReading.Parse(reason).Kind);
    }

    /// <summary>The sentences are the engine's: a rewording there fails here rather than fall back to raw text unnoticed.</summary>
    [Fact]
    public void The_sentences_are_the_engines()
    {
        var gate = Read("src", "core", "Orkeon.Infrastructure", "Crew", "Strategies", "ConditionGate.cs");
        Assert.Contains(
            "$\"" + SkipReasonReading.ConditionNotHeldPrefix + "{task.Id}" + SkipReasonReading.ConditionNotHeldMiddle + "{condition.DecisionTask}: {answer.ToCompactJson()}\"",
            gate,
            StringComparison.Ordinal);
        Assert.Contains(
            "$\"" + SkipReasonReading.DecisionNotRunPrefix + "{condition.DecisionTask}" + SkipReasonReading.DecisionNotRunSuffix + "\"",
            gate,
            StringComparison.Ordinal);
        Assert.Contains("\"" + SkipReasonReading.DependenciesConditionSkippedSentence + "\"", gate, StringComparison.Ordinal);

        var outcome = Read("src", "core", "Orkeon.Infrastructure", "Crew", "Strategies", "CrewRunOutcome.cs");
        Assert.Contains(
            "$\"Task {task.Id} ({agentRole})" + SkipReasonReading.DependencyFailedMarker + "{blockedBy}, which did not succeed\"",
            outcome,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_decision_role_is_the_engines()
    {
        var dispatch = Read("src", "core", "Orkeon.Infrastructure", "Crew", "Strategies", "DecisionDispatch.cs");

        Assert.Contains($"const string Role = \"{RunDecisionRole.Name}\";", dispatch, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([OrkeonSources.Root(), .. parts]));
}
