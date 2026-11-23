using Orkeon.Studio.Core.Events;

namespace Orkeon.Studio.Core.Tests.Events;

/// <summary>
/// A run's stdout mixes events and plain log lines: the parser keeps the events and refuses the rest
/// without throwing, a log line refused before any JSON parse is tried on it.
/// </summary>
public sealed class OrkeonEventParserTests
{
    [Theory]
    [InlineData("22:00:01 [INF] crew started")]
    [InlineData("Task 01M8A (writer) skipped: it depends on task 01M8B, which did not succeed")]
    [InlineData("Yes")]
    [InlineData("[1, 2]")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("""{"kind":"task.completed"}""")]
    public void A_line_that_is_no_event_is_refused(string? line)
    {
        Assert.False(OrkeonEventParser.TryParse(line, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void An_event_is_read_even_with_leading_spaces()
    {
        Assert.True(OrkeonEventParser.TryParse("""  {"v":2,"seq":7,"ts":"t","kind":"cost.updated"}""", out var parsed));
        Assert.Equal(("cost.updated", 7L), (parsed!.Kind, parsed.Seq));
    }
}
