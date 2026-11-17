using System.Text.RegularExpressions;

namespace Orkeon.Studio.Core.Process;

/// <summary>The severity a CLI console log line carries, in the order of <c>Microsoft.Extensions.Logging</c>.</summary>
public enum CliLogLevel
{
    /// <summary><c>trce</c>.</summary>
    Trace,

    /// <summary><c>dbug</c>.</summary>
    Debug,

    /// <summary><c>info</c>.</summary>
    Information,

    /// <summary><c>warn</c>.</summary>
    Warning,

    /// <summary><c>fail</c>.</summary>
    Error,

    /// <summary><c>crit</c>.</summary>
    Critical,
}

/// <summary>
/// One line of the CLI's console logger, read for the Run screen's journal. Under <c>--events</c>
/// every log goes to stderr (stdout carries the protocol), so the stream says nothing about severity:
/// the level token of the simple console format (<c>info:</c>, <c>warn:</c>, <c>fail:</c>…) does. The
/// token is the logger's fixed format, never localized. The logger's own timestamp is dropped: the
/// journal already shows when Studio read the line.
/// </summary>
/// <param name="Level">The level the line carries; null when it is not a log line.</param>
/// <param name="Text">The line without the logger's timestamp; the line unchanged when it is not a log line.</param>
public sealed partial record CliLogLine(CliLogLevel? Level, string Text)
{
    /// <summary>
    /// What leads the line a verb prints once its guards have passed, naming the settings file it
    /// runs on: <c>Using settings: &lt;path&gt;</c>. The CLI writes it as is, never localized, and to
    /// stderr, where it carries no level.
    /// </summary>
    public const string SettingsNoticePrefix = "Using settings:";

    /// <summary>
    /// Whether <paramref name="text"/> is that line: good news on stderr - the run is going ahead,
    /// on this file -, not a fault message.
    /// </summary>
    public static bool IsSettingsNotice(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.StartsWith(SettingsNoticePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// What leads a warning the CLI writes straight to stderr, without its logger: <c>WARNING: </c>,
    /// never localized. <c>--validate</c> says that way, exit code 0, a task whose file no agent that
    /// may run it can write (Orkeon #392) and the other findings of a load that does not refuse.
    /// </summary>
    public const string WarningPrefix = "WARNING: ";

    /// <summary>
    /// Reads one line: an optional <c>HH:mm:ss.fff </c> timestamp, then a level token and <c>: </c>;
    /// or a line led by <see cref="WarningPrefix"/>, a warning as it stands.
    /// </summary>
    public static CliLogLine Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.StartsWith(WarningPrefix, StringComparison.Ordinal))
            return new CliLogLine(CliLogLevel.Warning, text);

        var match = LogLine().Match(text);
        if (!match.Success)
            return new CliLogLine(null, text);

        var level = match.Groups["level"].Value switch
        {
            "trce" => CliLogLevel.Trace,
            "dbug" => CliLogLevel.Debug,
            "info" => CliLogLevel.Information,
            "warn" => CliLogLevel.Warning,
            "fail" => CliLogLevel.Error,
            _ => CliLogLevel.Critical,
        };
        return new CliLogLine(level, text[match.Groups["level"].Index..]);
    }

    [GeneratedRegex(@"^(?:\d{2}:\d{2}:\d{2}(?:\.\d{1,7})? )?(?<level>trce|dbug|info|warn|fail|crit): ", RegexOptions.CultureInvariant)]
    private static partial Regex LogLine();
}
