using Orkeon.Compliance.Vfs;
using Orkeon.Domain.Configuration;
using Orkeon.Studio.Core.Targets;

namespace Orkeon.Studio.Core.Teams;

/// <summary>What a crew asks of the decision models (System One).</summary>
public enum CrewDecisionMode
{
    /// <summary>The crew never asks a decision model: no decision task, no <c>system_one_decide</c>.</summary>
    None,

    /// <summary>
    /// The crew runs with the decision models or without them: its decision tasks ask the model when
    /// the circuit is on and answer from their fallback (or let their <c>otherwise</c> task run) when it
    /// is off.
    /// </summary>
    Optional,

    /// <summary>
    /// The crew cannot run without a decision model: an agent, or a task, holds <c>system_one_decide</c>,
    /// a tool a run registers only once a provider is configured; or a decision task has neither a
    /// fallback nor an <c>otherwise</c> task, which a run refuses once the circuit is off.
    /// </summary>
    Required,
}

/// <summary>One decision task of a crew, and what it answers when the circuit is off.</summary>
/// <param name="Task">The task's key in the crew.</param>
/// <param name="Questions">The names of its questions, in the crew's order.</param>
/// <param name="Fallback">The answer of each question when the circuit is off (question to option, level, <c>yes</c> or <c>no</c>).</param>
/// <param name="OtherwiseTask">The key of the task that runs when no sibling condition holds; null when none is declared.</param>
public sealed record CrewDecisionTask(
    string Task,
    IReadOnlyList<string> Questions,
    IReadOnlyDictionary<string, string> Fallback,
    string? OtherwiseTask)
{
    /// <summary>
    /// Whether the task has an answer once the circuit is off: a fallback, or an <c>otherwise</c> task.
    /// The engine loads one that has neither, and refuses the run at its entry when the circuit is off.
    /// </summary>
    public bool AnswersWithoutModel => Fallback.Count > 0 || OtherwiseTask is not null;
}

/// <summary>
/// What a YAML crew asks of the decision models, read with the engine's own loader
/// (<see cref="EngineCrewReader"/>). Until the engine lets a crew state its mode, the mode is deduced:
/// <see cref="CrewDecisionMode.Required"/> when an agent's or a task's tools hold <c>system_one_decide</c>
/// (the run refuses the crew when no provider registers that tool, under the default strict tool
/// resolution) or when a decision task has neither a fallback nor an <c>otherwise</c> task (a run
/// refuses it once the circuit is off), else
/// <see cref="CrewDecisionMode.Optional"/> when the crew declares a decision task, else
/// <see cref="CrewDecisionMode.None"/>. A crew the engine refuses at load, and a <c>.ork.ts</c> crew,
/// read as <see cref="None"/>.
/// </summary>
/// <param name="Mode">What the crew asks of the decision models.</param>
/// <param name="DecisionTasks">Its decision tasks, in the crew's order.</param>
/// <param name="ToolHolders">The keys of the agents and tasks that hold <c>system_one_decide</c>, in the crew's order.</param>
public sealed partial record CrewDecisionUsage(
    CrewDecisionMode Mode,
    IReadOnlyList<CrewDecisionTask> DecisionTasks,
    IReadOnlyList<string> ToolHolders)
{
    /// <summary>The name of the tool that asks the decision model.</summary>
    public const string DecideTool = "system_one_decide";

    /// <summary>A crew that asks nothing of the decision models.</summary>
    public static CrewDecisionUsage None { get; } = new(CrewDecisionMode.None, [], []);

    /// <summary>The decision tasks that have no answer once the circuit is off, in the crew's order.</summary>
    public IReadOnlyList<string> TasksWithoutAnswer =>
        [.. DecisionTasks.Where(task => !task.AnswersWithoutModel).Select(task => task.Task)];

    /// <summary>What the crew <paramref name="target"/> runs asks of the decision models.</summary>
    public static CrewDecisionUsage Of(RunTarget? target) => EngineCrewReader.Read(target, Judge, None);

    /// <summary>The same, of a single-file crew's text.</summary>
    public static CrewDecisionUsage OfCrewFile(string yaml) => EngineCrewReader.ReadText(yaml, Judge, None);

    /// <summary>
    /// What the team or crew folder <paramref name="teamPath"/> asks of the decision models, for a team's
    /// card. The engine's loader reads the crew only when one of its YAML files names a decision or the
    /// tool: a list of cards loads no crew that asks nothing. A crew it cannot read is
    /// <see cref="CrewDecisionMode.None"/>, the Launch screen judging it again before a run.
    /// </summary>
    public static CrewDecisionMode ModeOfTeam(string? teamPath)
    {
        if (string.IsNullOrWhiteSpace(teamPath) || !MentionsDecisions(teamPath))
            return CrewDecisionMode.None;

        var detection = new RunTargetDetector().Detect(teamPath);
        return Of(detection.Target).Mode;
    }

    [SuppressVfsCompliance(
        "EXCEPTION-BOOTSTRAP: Studio is a host application; a team's crew is user-owned storage on the " +
        "physical disk, read as the team catalog reads it.")]
    private static bool MentionsDecisions(string teamPath)
    {
        try
        {
            return CrewLlmDeclarations.YamlFilesOf(teamPath).Any(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains(DecideTool, StringComparison.OrdinalIgnoreCase)
                    || DecisionKey().IsMatch(text);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>What a loaded crew asks of the decision models.</summary>
    private static CrewDecisionUsage Judge(CrewConfiguration crew)
    {
        var holders = new List<string>();
        foreach (var agent in crew.Agents)
        {
            if (HoldsDecideTool(agent.Tools))
                holders.Add(CrewDeliverableWriters.KeyOf(agent));
        }

        var decisions = new List<CrewDecisionTask>();
        foreach (var task in crew.Tasks)
        {
            if (HoldsDecideTool(task.Tools))
                holders.Add(CrewDeliverableWriters.NameOf(task));

            if (task.Decision is not { } decision)
                continue;

            var name = CrewDeliverableWriters.NameOf(task);
            var otherwise = crew.Tasks.FirstOrDefault(other =>
                other.Condition is { IsOtherwise: true } condition
                && string.Equals(condition.DecisionTask, name, StringComparison.Ordinal));
            decisions.Add(new CrewDecisionTask(
                name,
                [.. decision.Questions.Select(question => question.Name)],
                decision.Fallback,
                otherwise is null ? null : CrewDeliverableWriters.NameOf(otherwise)));
        }

        var mode = holders.Count > 0 || decisions.Any(task => !task.AnswersWithoutModel)
            ? CrewDecisionMode.Required
            : decisions.Count > 0 ? CrewDecisionMode.Optional : CrewDecisionMode.None;
        return new CrewDecisionUsage(mode, decisions, holders);
    }

    // A "decision" key, bare or quoted, whatever the space before its colon.
    [System.Text.RegularExpressions.GeneratedRegex("""\bdecision["']?\s*:""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DecisionKey();

    private static bool HoldsDecideTool(IEnumerable<string> tools) =>
        tools.Any(tool => string.Equals(tool, DecideTool, StringComparison.OrdinalIgnoreCase));
}
