using Orkeon.Studio.Core.Run;

namespace Orkeon.Studio.Core.Tests.Run;

/// <summary>The answers of a decision task, read from the engine's compact JSON.</summary>
public sealed class DecisionAnswerReadingTests
{
    [Fact]
    public void Each_kind_of_question_gives_its_value_its_likelihood_and_its_confidence()
    {
        var answers = DecisionAnswerReading.Parse("""
            {"route":{"chosen":"billing","probabilities":{"billing":0.82,"tech":0.18},"confidence":0.9},
             "risk":{"score":3.4,"probabilities":{},"confidence":0.7},
             "ready":{"chosen":"yes","probability":0.64,"probabilities":{},"confidence":0.64},
             "defaulted":true}
            """);

        Assert.Equal(
            [
                new DecisionAnswerReading("route", "billing", 0.82, null, 0.9),
                new DecisionAnswerReading("risk", null, null, 3.4, 0.7),
                new DecisionAnswerReading("ready", "yes", 0.64, null, 0.64),
            ],
            answers);
    }

    [Fact]
    public void A_no_is_as_likely_as_the_yes_probability_leaves()
    {
        // The engine's yes/no shape: the side chosen, the yes probability, no option table.
        var answer = Assert.Single(DecisionAnswerReading.Parse("""{"go":{"chosen":"no","probability":0.2,"probabilities":{},"confidence":0.8}}"""));

        Assert.Equal("no", answer.Chosen);
        Assert.Equal(0.8, answer.Likelihood!.Value, precision: 10);
    }

    [Fact]
    public void A_chosen_option_without_its_probability_says_no_likelihood()
    {
        var answer = Assert.Single(DecisionAnswerReading.Parse("""{"route":{"chosen":"tech","probabilities":{},"confidence":1}}"""));

        Assert.Equal("tech", answer.Chosen);
        Assert.Null(answer.Likelihood);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"approved":true}""")]
    public void Unreadable_answers_read_as_none(string? json)
    {
        Assert.Empty(DecisionAnswerReading.Parse(json));
    }
}
