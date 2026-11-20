using System.Globalization;
using Orkeon.Studio.Core.Run;
using Orkeon.Studio.Wpf.ViewModels.Launch;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>A decision task's row on the run panel says what it chose, how likely and how sure.</summary>
public sealed class RunTaskDecisionTests
{
    private static RunTaskViewModel Row(string? answers, bool defaulted = false) => new(
        new RunTaskProgress("pick", "judge", true, 10, 0, 0, IsDecision: true, Defaulted: defaulted, DecisionAnswers: answers));

    [Fact]
    public void Each_answer_is_one_line_with_whole_percentages()
    {
        var row = Row("""
            {"route":{"chosen":"billing","probabilities":{"billing":0.825},"confidence":0.9},
             "risk":{"score":3.4,"probabilities":{},"confidence":0.7},
             "ready":{"chosen":"no","probability":0.444,"probabilities":{},"confidence":0.556}}
            """);

        Assert.True(row.HasDecisionDetails);
        Assert.Equal(
            "route = billing · probability 83% · confidence 90%\n"
            + $"risk = score {3.4.ToString("0.##", CultureInfo.CurrentCulture)} · confidence 70%\n"
            + "ready = no · probability 56%",
            row.DecisionDetails);
        Assert.Equal("decision", row.DecisionLabel);
    }

    [Fact]
    public void A_defaulted_decision_keeps_its_label_and_shows_its_fallback()
    {
        var row = Row("""{"go":{"chosen":"no","probabilities":{},"confidence":0},"defaulted":true}""", defaulted: true);

        Assert.Equal("decision (defaulted)", row.DecisionLabel);
        Assert.Equal("go = no · confidence 0%", row.DecisionDetails);
    }

    [Fact]
    public void A_defaulted_question_with_no_fallback_says_no_answer_without_figures()
    {
        var row = Row("""{"go":{"probabilities":{},"confidence":0},"route":{"chosen":"billing","probabilities":{},"confidence":0},"defaulted":true}""", defaulted: true);

        Assert.Equal("go = no answer\nroute = billing · confidence 0%", row.DecisionDetails);
        Assert.DoesNotContain("—", row.DecisionDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void Answers_it_cannot_read_show_nothing()
    {
        var row = Row("""{"approved":true}""");

        Assert.False(row.HasDecisionDetails);
        Assert.Equal(string.Empty, row.DecisionDetails);
    }
}
