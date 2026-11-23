using System.Text.RegularExpressions;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Run;

/// <summary>
/// The fields Studio reads in the events of a run and of the forge, each held to the engine's emitter:
/// on <c>task.completed</c>, the task and its outcome, a task cut short and a deliverable not written
/// (Orkeon #392: <c>endedOnIterationCap</c>, <c>deliverableMissing</c>) and where a decision task's
/// answer came from (<c>decisionSource</c>); on <c>run.decision</c>, the mode, the circuit and the
/// cause that turned it off; on <c>agent.route</c>, <c>agent.effort</c> and <c>agent.skills</c>, what
/// System One picked for an agent and why; on <c>agent.compacted</c> and <c>agent.pruned</c>, what a
/// context lost; on <c>run.finished</c>, the run's counts (<c>cappedTasks</c>,
/// <c>missingDeliverables</c>); and the forge's own <c>task.completed</c>. Studio reads an absent field
/// as « nothing to say »: one the engine renames or drops would silence a warning or a line of the
/// run's view without failing a test. This guard reads the engine's emitters and fails when a field
/// Studio reads is no longer in the event it reads it from.
/// </summary>
/// <remarks>
/// Text-level, comments stripped first, like the other source-reading guards of the repository:
/// the emitters need a whole run to fire. Each row also checks Studio still reads the field in the
/// code that reads that event, so the list follows the readers. Names only: a field that keeps its
/// name but changes type reads as absent, which the tolerance tests of RunProgressModelTests pin.
/// Without an Orkeon checkout it fails, never passes.
/// </remarks>
public sealed partial class RunEventFieldDriftTests
{
    private const string RunObserver = "src/hosting/Orkeon.Hosting/Runs/RunEventObserver.cs";
    private const string RunContext = "src/scripting/Orkeon.Scripting.Cli/Commands/Run/ObservedRunContext.cs";
    private const string ForgeStages = "src/scripting/Orkeon.Scripting.Cli/Commands/Forge/ForgeRunStages.cs";
    private const string RouteObserver = "src/hosting/Orkeon.Hosting/Runs/RunEventsRouteObserver.cs";
    private const string SkillsObserver = "src/hosting/Orkeon.Hosting/Runs/RunEventsSkillsObserver.cs";
    private const string CompactionObserver = "src/hosting/Orkeon.Hosting/Runs/RunEventsCompactionObserver.cs";
    private const string PruneObserver = "src/hosting/Orkeon.Hosting/Runs/RunEventsPruneObserver.cs";

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

    /// <summary>
    /// One row: the engine's emitter, the event kind as the emitter spells it, the field, the Studio
    /// reader and the start of the code that reads that event there (a method, or a switch arm).
    /// <c>taskId</c> and <c>success</c> are guarded with the flags: a task's last attempt, which
    /// both screens count by, is found by them.
    /// </summary>
    public static TheoryData<string, string, string, string, string> FieldsStudioReads => new()
    {
        { RunObserver, "RunEventKinds.TaskCompleted", "taskId", RunProgress, RunTaskCompletedReader },
        { RunObserver, "RunEventKinds.TaskCompleted", "success", RunProgress, RunTaskCompletedReader },
        { RunObserver, "RunEventKinds.TaskCompleted", "endedOnIterationCap", RunProgress, RunTaskCompletedReader },
        { RunObserver, "RunEventKinds.TaskCompleted", "deliverableMissing", RunProgress, RunTaskCompletedReader },
        { RunObserver, "RunEventKinds.TaskCompleted", "decisionSource", RunProgress, RunTaskCompletedReader },
        { RunObserver, "RunEventKinds.RunDecision", "mode", RunProgress, "private bool ApplyRunDecision(" },
        { RunObserver, "RunEventKinds.RunDecision", "circuit", RunProgress, "private bool ApplyRunDecision(" },
        { RunObserver, "RunEventKinds.RunDecision", "cause", RunProgress, "private bool ApplyRunDecision(" },
        { RunObserver, "RunEventKinds.RunDecision", "detail", RunProgress, "private bool ApplyRunDecision(" },
        { RouteObserver, "RunEventKinds.AgentRoute", "taskId", RunProgress, "private bool ApplyAgentRoute(" },
        { RouteObserver, "RunEventKinds.AgentEffort", "taskId", RunProgress, "private bool ApplyAgentEffort(" },
        { RouteObserver, "RunEventKinds.AgentEffort", "agentRole", RunProgress, "private bool ApplyAgentEffort(" },
        { SkillsObserver, "RunEventKinds.AgentSkills", "taskId", RunProgress, "private bool ApplyAgentSkills(" },
        { SkillsObserver, "RunEventKinds.AgentSkills", "agentRole", RunProgress, "private bool ApplyAgentSkills(" },
        { CompactionObserver, "RunEventKinds.AgentCompacted", "taskId", RunProgress, "private bool ApplyAgentCompacted(" },
        { CompactionObserver, "RunEventKinds.AgentCompacted", "agentRole", RunProgress, "private bool ApplyAgentCompacted(" },
        { PruneObserver, "RunEventKinds.AgentPruned", "taskId", RunProgress, "private bool ApplyAgentPruned(" },
        { PruneObserver, "RunEventKinds.AgentPruned", "agentRole", RunProgress, "private bool ApplyAgentPruned(" },
        { PruneObserver, "RunEventKinds.AgentPruned", "dropped", RunProgress, "private bool ApplyAgentPruned(" },
        { RouteObserver, "RunEventKinds.AgentRoute", "agentRole", RunProgress, "private bool ApplyAgentRoute(" },
        { RouteObserver, "RunEventKinds.AgentRoute", "profile", RunProgress, "private bool ApplyAgentRoute(" },
        { RouteObserver, "RunEventKinds.AgentRoute", "reason", RunProgress, "private bool ApplyAgentRoute(" },
        { RouteObserver, "RunEventKinds.AgentRoute", "probability", RunProgress, "private bool ApplyAgentRoute(" },
        { RouteObserver, "RunEventKinds.AgentEffort", "level", RunProgress, "private bool ApplyAgentEffort(" },
        { RouteObserver, "RunEventKinds.AgentEffort", "previous", RunProgress, "private bool ApplyAgentEffort(" },
        { RouteObserver, "RunEventKinds.AgentEffort", "reason", RunProgress, "private bool ApplyAgentEffort(" },
        { RouteObserver, "RunEventKinds.AgentEffort", "probability", RunProgress, "private bool ApplyAgentEffort(" },
        { SkillsObserver, "RunEventKinds.AgentSkills", "mode", RunProgress, "private bool ApplyAgentSkills(" },
        { SkillsObserver, "RunEventKinds.AgentSkills", "skills", RunProgress, "private bool ApplyAgentSkills(" },
        { SkillsObserver, "RunEventKinds.AgentSkills", "reason", RunProgress, "private bool ApplyAgentSkills(" },
        { CompactionObserver, "RunEventKinds.AgentCompacted", "outcome", RunProgress, "private bool ApplyAgentCompacted(" },
        { CompactionObserver, "RunEventKinds.AgentCompacted", "tokensBefore", RunProgress, "private bool ApplyAgentCompacted(" },
        { CompactionObserver, "RunEventKinds.AgentCompacted", "tokensAfter", RunProgress, "private bool ApplyAgentCompacted(" },
        { PruneObserver, "RunEventKinds.AgentPruned", "cut", RunProgress, "private bool ApplyAgentPruned(" },
        { PruneObserver, "RunEventKinds.AgentPruned", "freedTokens", RunProgress, "private bool ApplyAgentPruned(" },
        { RunContext, "RunEventKinds.RunFinished", "cappedTasks", RunProgress, RunFinishedReader },
        { RunContext, "RunEventKinds.RunFinished", "missingDeliverables", RunProgress, RunFinishedReader },
        { ForgeStages, "\"task.completed\"", "taskId", ForgeSession, ForgeTaskCompletedReader },
        { ForgeStages, "\"task.completed\"", "success", ForgeSession, ForgeTaskCompletedReader },
        { ForgeStages, "\"task.completed\"", "endedOnIterationCap", ForgeSession, ForgeTaskCompletedReader },
        { ForgeStages, "\"task.completed\"", "deliverableMissing", ForgeSession, ForgeTaskCompletedReader },
    };

    private const string RunTaskCompletedReader = "private bool ApplyTaskCompleted(";
    private const string RunFinishedReader = "private bool ApplyRunFinished(";
    private const string ForgeTaskCompletedReader = "case ForgeEventKinds.TaskCompleted:";

    [Theory]
    [MemberData(nameof(FieldsStudioReads))]
    public void A_field_Studio_reads_is_still_in_the_event_the_engine_emits(
        string emitter, string kind, string field, string reader, string readerStart)
    {
        var readerCode = ReaderOf(reader, readerStart);
        Assert.True(
            readerCode.Contains($"(\"{field}\")", StringComparison.Ordinal),
            $"{reader} no longer reads '{field}' where it reads {kind} ({readerStart}): drop the row, or point it at the new reader.");

        var source = EmitterSource(emitter);
        var (calls, payloads) = PayloadsOf(source, kind);
        Assert.True(calls > 0, $"{emitter} no longer emits {kind}." + WayOut);
        // A payload built apart and passed by name is not read here: every call must be one this
        // guard reads, or it would pass on fewer emitters than the engine has.
        Assert.True(
            payloads.Count == calls,
            $"{emitter} emits {kind} {calls} time(s), {payloads.Count} of them with an anonymous payload this guard reads."
            + " Teach the guard the new shape of the call.");
        var assignment = new Regex(@"[{,]\s*" + Regex.Escape(field) + @"\s*=", RegexOptions.CultureInvariant);
        Assert.True(
            payloads.All(payload => assignment.IsMatch(payload)),
            $"{emitter} emits {kind} without '{field}'." + WayOut);
    }

    /// <summary>
    /// The screens' dispatch hands each event to the reader the rows above scope: a kind sent to
    /// another method would leave the rows checking a reader the event never reaches.
    /// </summary>
    [Theory]
    [InlineData("RunEventKinds.TaskCompleted => ApplyTaskCompleted(orkeonEvent)")]
    [InlineData("RunEventKinds.RunFinished => ApplyRunFinished(orkeonEvent)")]
    [InlineData("RunEventKinds.RunDecision => ApplyRunDecision(orkeonEvent)")]
    [InlineData("RunEventKinds.AgentRoute => ApplyAgentRoute(orkeonEvent)")]
    [InlineData("RunEventKinds.AgentEffort => ApplyAgentEffort(orkeonEvent)")]
    [InlineData("RunEventKinds.AgentSkills => ApplyAgentSkills(orkeonEvent)")]
    [InlineData("RunEventKinds.AgentCompacted => ApplyAgentCompacted(orkeonEvent)")]
    [InlineData("RunEventKinds.AgentPruned => ApplyAgentPruned(orkeonEvent)")]
    public void The_run_events_reach_the_readers_the_rows_check(string dispatch)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), RunProgress));
        Assert.Contains(dispatch, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run's counts are written with <c>?? 0</c>: the event sink drops a null field
    /// (<c>WhenWritingNull</c>), and a count dropped reads as « this CLI does not count » — Studio
    /// would then count the lines itself, silently, where the engine meant zero.
    /// </summary>
    [Theory]
    [InlineData("cappedTasks")]
    [InlineData("missingDeliverables")]
    public void The_run_counts_are_never_written_null(string field)
    {
        var (_, payloads) = PayloadsOf(EmitterSource(RunContext), "RunEventKinds.RunFinished");
        var value = new Regex(@"[{,]\s*" + Regex.Escape(field) + @"\s*=\s*([^,}]+)", RegexOptions.CultureInvariant);
        Assert.All(payloads, payload =>
        {
            var match = value.Match(payload);
            Assert.True(match.Success, $"{RunContext} emits run.finished without '{field}'." + WayOut);
            Assert.True(
                match.Groups[1].Value.TrimEnd().EndsWith("?? 0", StringComparison.Ordinal),
                $"{RunContext} writes '{field}' as '{match.Groups[1].Value.Trim()}': without '?? 0' a null drops the field."
                + " Make sure a missing count still reaches Studio as 0, then update this guard.");
        });
    }

    private static string EmitterSource(string emitter)
    {
        var path = Path.Combine(OrkeonSources.Root(), emitter);
        Assert.True(File.Exists(path), $"{emitter} is no longer in the Orkeon checkout." + WayOut);
        return LineCommentPattern().Replace(BlockCommentPattern().Replace(File.ReadAllText(path), ""), "");
    }

    /// <summary>
    /// The code of <paramref name="reader"/> from <paramref name="start"/> to the next member or
    /// switch arm: the field must be read where the event is read, not anywhere in the file.
    /// </summary>
    private static string ReaderOf(string reader, string start)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), reader));
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{reader} no longer has '{start}': point the rows at where it now reads the event.");
        var body = from + start.Length;
        var next = NextMemberOrArmPattern().Match(source, body);
        return next.Success ? source[body..next.Index] : source[body..];
    }

    [GeneratedRegex(@"\n[ \t]*(?:case\s|default:|private\s|public\s|internal\s|protected\s)")]
    private static partial Regex NextMemberOrArmPattern();

    /// <summary>
    /// Every <c>Emit(kind, ...)</c> call of the source, and the anonymous payload (<c>new { ... }</c>,
    /// braces included) of each one that has one, a scope argument between the kind and the payload allowed.
    /// </summary>
    private static (int Calls, List<string> Payloads) PayloadsOf(string source, string kind)
    {
        var payloads = new List<string>();
        var call = new Regex(@"\.Emit\(\s*" + Regex.Escape(kind) + @"\s*,", RegexOptions.CultureInvariant);
        var calls = call.Matches(source);
        foreach (Match match in calls)
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

        return (calls.Count, payloads);
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
