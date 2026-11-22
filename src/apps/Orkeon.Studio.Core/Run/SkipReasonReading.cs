using System.Text.RegularExpressions;

namespace Orkeon.Studio.Core.Run;

/// <summary>What made the engine skip a task, as far as its <c>skipReason</c> says it.</summary>
public enum SkipReasonKind
{
    /// <summary>A text none of the engine's known sentences matches: shown as it came.</summary>
    Other,

    /// <summary>The task's condition did not hold on the answer of the decision task it names.</summary>
    ConditionNotHeld,

    /// <summary>The decision task the condition names did not run, so the condition could not hold.</summary>
    DecisionNotRun,

    /// <summary>A task it depends on did not succeed.</summary>
    DependencyFailed,
}

/// <summary>
/// The <c>skipReason</c> of a skipped task, read back into what a screen can say in the user's
/// language. The engine writes it as an English sentence with internal task ids and the decision's
/// compact JSON; the sentences are copied from its sources and pinned there by a drift test
/// (<see cref="ConditionNotHeldPrefix"/>, <see cref="DecisionNotRunSuffix"/>, <see cref="DependencyFailedMarker"/>).
/// A sentence a newer engine words differently reads <see cref="SkipReasonKind.Other"/>: its raw text is shown.
/// </summary>
/// <param name="Kind">Which of the engine's sentences it is.</param>
/// <param name="DecisionTask">The decision task the condition names, for the first two kinds.</param>
/// <param name="DecisionAnswers">The decision's compact JSON, for <see cref="SkipReasonKind.ConditionNotHeld"/>.</param>
public sealed partial record SkipReasonReading(SkipReasonKind Kind, string? DecisionTask = null, string? DecisionAnswers = null)
{
    /// <summary>The start of the sentence of a condition that did not hold (<c>ConditionGate</c>).</summary>
    public const string ConditionNotHeldPrefix = "condition of task ";

    /// <summary>What follows the task id in that sentence, before the decision task's key.</summary>
    public const string ConditionNotHeldMiddle = " did not hold on decision task ";

    /// <summary>The start of the sentence of a decision that did not run (<c>ConditionGate</c>).</summary>
    public const string DecisionNotRunPrefix = "decision ";

    /// <summary>The end of that sentence.</summary>
    public const string DecisionNotRunSuffix = " did not run";

    /// <summary>The words of the sentence of a failed dependency (<c>CrewRunOutcome.RecordSkipAsync</c>).</summary>
    public const string DependencyFailedMarker = " skipped: it depends on task ";

    /// <summary>Reads a <c>skipReason</c>; null or blank reads <see cref="SkipReasonKind.Other"/>.</summary>
    public static SkipReasonReading Parse(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new(SkipReasonKind.Other);

        if (ConditionNotHeld().Match(reason) is { Success: true } condition)
            return new(SkipReasonKind.ConditionNotHeld, condition.Groups["key"].Value, condition.Groups["json"].Value);

        if (DecisionNotRun().Match(reason) is { Success: true } notRun)
            return new(SkipReasonKind.DecisionNotRun, notRun.Groups["key"].Value);

        return reason.StartsWith("Task ", StringComparison.Ordinal) && reason.Contains(DependencyFailedMarker, StringComparison.Ordinal)
            ? new(SkipReasonKind.DependencyFailed)
            : new(SkipReasonKind.Other);
    }

    [GeneratedRegex(@"^condition of task \S+ did not hold on decision task (?<key>\S+): (?<json>\{.*\})\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConditionNotHeld();

    [GeneratedRegex(@"^decision (?<key>\S+) did not run\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DecisionNotRun();
}

/// <summary>
/// The role the engine gives a decision task it answers itself, with no agent of the crew
/// (<c>DecisionDispatch.Role</c>, internal to the engine): copied here and pinned by a drift test.
/// A screen names it «Decision» in the user's language rather than show the bare word.
/// </summary>
public static class RunDecisionRole
{
    /// <summary>The role's value on a <c>task.started</c> or <c>task.completed</c> line.</summary>
    public const string Name = "decision";

    /// <summary>Whether <paramref name="agentRole"/> is that role.</summary>
    public static bool Is(string? agentRole) => string.Equals(agentRole, Name, StringComparison.Ordinal);
}
