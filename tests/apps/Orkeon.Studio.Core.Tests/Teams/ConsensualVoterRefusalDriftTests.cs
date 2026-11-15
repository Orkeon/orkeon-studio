using System.Text.RegularExpressions;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Teams;

/// <summary>
/// #349: <see cref="CrewFileSystemScopes"/> counts every agent of a consensual crew but its manager
/// as started for sure, so a scope the mounts refuse blocks the run before it starts. That holds
/// only while ONE refused voter fails the whole run in the engine: the voters' answers are awaited
/// together (<c>Task.WhenAll</c>) before, and outside, the guard that turns one voter's failure into
/// a lost voice. This guard reads the engine's source and fails when that changes.
/// </summary>
/// <remarks>
/// Text-level, comments stripped first, like the other source-reading guards of the repository:
/// the strategy needs the whole agent runtime to run, which no hand-written double stands for.
/// </remarks>
public sealed partial class ConsensualVoterRefusalDriftTests
{
    private const string Source = "src/core/Orkeon.Infrastructure/Consensus/ConsensualProcessStrategy.cs";
    private const string Method = "ExecuteWithAllAgentsAsync";

    private const string WayOut =
        " Read again how a consensual run treats a voter the engine refuses to start. If one refused voter"
        + " no longer fails the run (its failure is caught and counted as a lost voice), Studio must stop"
        + " blocking on it: in CrewFileSystemScopes.StartsOf, the consensual branch must start nobody for"
        + " sure (a warning instead of a block), and the tests and docs that say a refused voter stops the"
        + " run follow. Then update this guard.";

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"\bTask<[^;{]*>\s+" + Method + @"\s*\(")]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"\btry\b")]
    private static partial Regex TryPattern();

    [Fact]
    public void A_voter_the_engine_refuses_still_fails_a_consensual_run()
    {
        var path = Path.Combine(OrkeonSources.Root(), Source);
        Assert.True(File.Exists(path), $"{Source} is no longer in the Orkeon checkout." + WayOut);

        var body = BodyOf(LineCommentPattern().Replace(BlockCommentPattern().Replace(File.ReadAllText(path), ""), ""));
        Assert.True(body is not null, $"{Source} no longer declares {Method}." + WayOut);

        var execution = body!.IndexOf(".ExecuteTaskAsync(", StringComparison.Ordinal);
        var together = body.IndexOf("Task.WhenAll(", StringComparison.Ordinal);
        var guard = TryPattern().Match(body);

        Assert.True(execution >= 0, $"{Method} no longer runs each voter through ExecuteTaskAsync." + WayOut);
        Assert.True(together > execution, $"{Method} no longer awaits the voters together with Task.WhenAll." + WayOut);
        Assert.True(
            !guard.Success || guard.Index > together,
            $"{Method} now guards a voter's execution before the answers are awaited together." + WayOut);
    }

    /// <summary>The body of the method's declaration, braces included; null when it is not declared.</summary>
    private static string? BodyOf(string source)
    {
        var declaration = DeclarationPattern().Match(source);
        var open = declaration.Success ? source.IndexOf('{', declaration.Index) : -1;
        if (open < 0)
            return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        return null;
    }
}
