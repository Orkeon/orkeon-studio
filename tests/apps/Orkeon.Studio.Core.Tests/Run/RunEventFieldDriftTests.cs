using System.Text.RegularExpressions;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Run;

/// <summary>
/// Orkeon #392 and the iteration cap: Studio reads the fields <c>task.completed</c> and
/// <c>run.finished</c> carry about a task cut short and a deliverable not written
/// (<c>endedOnIterationCap</c>, <c>deliverableMissing</c>, <c>cappedTasks</c>,
/// <c>missingDeliverables</c>), and reads an absent one as « nothing to say ». A field the engine
/// renames or drops would silence the warning without failing a test: this guard reads the
/// engine's emitters and fails when a field Studio reads is no longer in the event it reads it from.
/// </summary>
/// <remarks>
/// Text-level, comments stripped first, like the other source-reading guards of the repository:
/// the emitters need a whole run to fire. Each row also checks Studio still reads the field where
/// it says, so the list follows the readers. Without an Orkeon checkout it fails, never passes.
/// </remarks>
public sealed partial class RunEventFieldDriftTests
{
    private const string RunObserver = "src/hosting/Orkeon.Hosting/Runs/RunEventObserver.cs";
    private const string RunContext = "src/scripting/Orkeon.Scripting.Cli/Commands/Run/ObservedRunContext.cs";
    private const string ForgeStages = "src/scripting/Orkeon.Scripting.Cli/Commands/Forge/ForgeRunStages.cs";

    private const string RunProgress = "src/apps/Orkeon.Studio.Core/Run/RunProgressModel.cs";
    private const string ForgeSession = "src/apps/Orkeon.Studio.Core/Forge/ForgeSessionModel.cs";

    private const string WayOut =
        " Find where the engine now says it (the event, the field's new name), make Studio read that"
        + " (RunProgressModel for orkeon run, ForgeSessionModel for the forge) and the tests that feed"
        + " those fields follow; if the engine no longer says it at all, Studio's warning must go, docs"
        + " included. Then update this guard.";

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"\bnew\s*\{")]
    private static partial Regex AnonymousObjectPattern();

    /// <summary>One row: the engine's emitter, the event kind as the emitter spells it, the field, the Studio reader.</summary>
    public static TheoryData<string, string, string, string> FieldsStudioReads => new()
    {
        { RunObserver, "RunEventKinds.TaskCompleted", "endedOnIterationCap", RunProgress },
        { RunObserver, "RunEventKinds.TaskCompleted", "deliverableMissing", RunProgress },
        { RunContext, "RunEventKinds.RunFinished", "cappedTasks", RunProgress },
        { RunContext, "RunEventKinds.RunFinished", "missingDeliverables", RunProgress },
        { ForgeStages, "\"task.completed\"", "endedOnIterationCap", ForgeSession },
        { ForgeStages, "\"task.completed\"", "deliverableMissing", ForgeSession },
    };

    [Theory]
    [MemberData(nameof(FieldsStudioReads))]
    public void A_field_Studio_reads_is_still_in_the_event_the_engine_emits(
        string emitter, string kind, string field, string reader)
    {
        var readerSource = File.ReadAllText(Path.Combine(RepositoryRoot(), reader));
        Assert.True(
            readerSource.Contains($"(\"{field}\")", StringComparison.Ordinal),
            $"{reader} no longer reads '{field}': drop the row, or point it at the new reader.");

        var path = Path.Combine(OrkeonSources.Root(), emitter);
        Assert.True(File.Exists(path), $"{emitter} is no longer in the Orkeon checkout." + WayOut);
        var source = LineCommentPattern().Replace(BlockCommentPattern().Replace(File.ReadAllText(path), ""), "");

        var payloads = PayloadsOf(source, kind);
        Assert.True(payloads.Count > 0, $"{emitter} no longer emits {kind} with an anonymous payload." + WayOut);
        var assignment = new Regex(@"[{,]\s*" + Regex.Escape(field) + @"\s*=", RegexOptions.CultureInvariant);
        Assert.True(
            payloads.All(payload => assignment.IsMatch(payload)),
            $"{emitter} emits {kind} without '{field}'." + WayOut);
    }

    /// <summary>
    /// The anonymous payload (<c>new { ... }</c>, braces included) of every <c>Emit(kind, ...)</c>
    /// call of the source, a scope argument between the kind and the payload allowed.
    /// </summary>
    private static List<string> PayloadsOf(string source, string kind)
    {
        var payloads = new List<string>();
        var call = new Regex(@"\.Emit\(\s*" + Regex.Escape(kind) + @"\s*,", RegexOptions.CultureInvariant);
        foreach (Match match in call.Matches(source))
        {
            // The payload of this call: before the statement ends.
            var payload = AnonymousObjectPattern().Match(source, match.Index + match.Length);
            if (!payload.Success || source.IndexOf(';', match.Index, payload.Index - match.Index) >= 0)
                continue;

            var open = payload.Index + payload.Length - 1;
            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                {
                    payloads.Add(source[open..(i + 1)]);
                    break;
                }
            }
        }

        return payloads;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orkeon.Studio.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, $"Could not locate the repo root above {AppContext.BaseDirectory}.");
        return directory!.FullName;
    }
}
