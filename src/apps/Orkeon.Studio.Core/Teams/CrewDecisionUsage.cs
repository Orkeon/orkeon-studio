using Orkeon.Compliance.Vfs;
using Orkeon.Constants.Llm;
using Orkeon.Domain.Configuration;
using Orkeon.Studio.Core.Targets;

namespace Orkeon.Studio.Core.Teams;

/// <summary>What a crew asks of the decision models (System One).</summary>
public enum CrewDecisionMode
{
    /// <summary>
    /// The crew never asks a decision model: it says so (<c>decision.mode: none</c>), or it has no
    /// decision task, no <c>system_one_decide</c> and nothing System One picks.
    /// </summary>
    None,

    /// <summary>
    /// The crew runs with the decision models or without them. Declared (<c>decision.mode: optional</c>),
    /// each decision task is answered by its <c>without:</c> relay when the circuit is off. Deduced, its
    /// decision tasks answer from their fallback (or let their <c>otherwise</c> task run), and what System
    /// One picks (a profile, a thinking level, skills, pruning) falls back to what the crew or the host states.
    /// </summary>
    Optional,

    /// <summary>
    /// The crew cannot run without a decision model. Declared (<c>decision.mode: required</c>), a run is
    /// refused before its first task once the circuit is off. Deduced: an agent, or a task, holds
    /// <c>system_one_decide</c>, a tool the engine withholds once the circuit is off; or a decision task has
    /// neither a fallback nor an <c>otherwise</c> task, which a run refuses once the circuit is off.
    /// </summary>
    Required,
}

/// <summary>What a crew lets System One pick, beside its decision tasks and <c>system_one_decide</c>.</summary>
public enum CrewSystemOneUseKind
{
    /// <summary>The profile of an agent or a task (<c>llm.profile: auto</c>), picked on the host's ladder.</summary>
    RouteAuto,

    /// <summary>The thinking level of an agent or a task (<c>thinking: { effort: auto }</c>).</summary>
    EffortAuto,

    /// <summary>Whether an agent or a task thinks at all (<c>thinking: { enabled: auto }</c>).</summary>
    ThinkingAuto,

    /// <summary>The skills an agent carries (<c>skills: { auto: true }</c>).</summary>
    SkillsAuto,

    /// <summary>The tool results an agent's loop prunes (<c>compaction: { prune: true }</c>).</summary>
    Prune,

    /// <summary>
    /// The decision stages of a profile an agent or a task names (<c>llm.profile</c>,
    /// <c>llm_override.profile</c>), when the settings define that name as a decision profile
    /// (<c>Orkeon:Decision:Profiles</c>): only the settings tell, so the launch adds it (<c>DecisionPreflight</c>).
    /// </summary>
    DecisionProfile,
}

/// <summary>A profile an agent or a task names, other than the default and <c>auto</c>.</summary>
/// <param name="Holder">The key of the agent or the task.</param>
/// <param name="Profile">The profile's name, as written.</param>
public sealed record CrewNamedProfile(string Holder, string Profile);

/// <summary>One thing a crew lets System One pick.</summary>
/// <param name="Kind">What is picked.</param>
/// <param name="Holder">The key of the agent or the task that asks for it.</param>
public sealed record CrewSystemOneUse(CrewSystemOneUseKind Kind, string Holder);

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
    /// The key of the agent that answers in place of the model when the circuit is off (the task's
    /// <c>without:</c> relay, of a crew with <c>decision.mode: optional</c>); null when none is declared.
    /// </summary>
    public string? RelayAgent { get; init; }

    /// <summary>
    /// Whether the task has an answer once the circuit is off: a relay, a fallback, or an <c>otherwise</c>
    /// task. The engine loads one that has none, and refuses the run at its entry when the circuit is off.
    /// </summary>
    public bool AnswersWithoutModel => RelayAgent is not null || Fallback.Count > 0 || OtherwiseTask is not null;
}

/// <summary>
/// What a YAML crew asks of the decision models, read with the engine's own loader
/// (<see cref="EngineCrewReader"/>). The mode is the one the crew states (<c>decision: { mode }</c>,
/// <see cref="Declared"/>). A crew that states none is deduced:
/// <see cref="CrewDecisionMode.Required"/> when an agent's or a task's tools hold <c>system_one_decide</c>
/// (the engine withholds the tool once the circuit is off, and refuses the crew at load under the default
/// strict tool resolution) or when a decision task has neither a fallback nor an <c>otherwise</c> task
/// (a run refuses it once the circuit is off), else
/// <see cref="CrewDecisionMode.Optional"/> when the crew declares a decision task or lets System One
/// pick something (<see cref="OtherUses"/>), else
/// <see cref="CrewDecisionMode.None"/>. Of a crew the engine refuses at load, and of a <c>.ork.ts</c>
/// crew, nothing is known (<see cref="Unknown"/>).
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

    /// <summary>
    /// Whether the crew states its mode (<c>decision: { mode }</c>): the engine then applies the mode's
    /// own rules. False when <see cref="Mode"/> is deduced from what the crew holds.
    /// </summary>
    public bool Declared { get; init; }

    /// <summary>
    /// What the crew lets System One pick, in the crew's order. With the circuit off each falls back to
    /// what the crew or the host states; the engine still refuses some at load whatever the circuit: a
    /// profile to pick (<see cref="CrewSystemOneUseKind.RouteAuto"/>) on a host whose ladder does not
    /// serve it, and that one or a decision profile in a crew that states <c>decision.mode: none</c>
    /// (<c>DecisionPreflight</c>).
    /// </summary>
    public IReadOnlyList<CrewSystemOneUse> OtherUses { get; init; } = [];

    /// <summary>
    /// The profiles the crew's agents and tasks name, in the crew's order. One of them may be a decision
    /// profile of the settings the run reads, which is then a use of System One: the crew cannot tell.
    /// </summary>
    public IReadOnlyList<CrewNamedProfile> NamedProfiles { get; init; } = [];

    /// <summary>
    /// Whether nothing is known of what the crew asks: the engine refuses it at load (its error is the
    /// launch's to report), or it is a script, which Studio does not read. <see cref="Mode"/> is then
    /// <see cref="CrewDecisionMode.None"/> for want of anything better, and a screen must not conclude
    /// from it that the crew goes without the decision models.
    /// </summary>
    public bool Unknown { get; init; }

    /// <summary>A crew nothing is known of (<see cref="Unknown"/>).</summary>
    private static CrewDecisionUsage Unreadable { get; } = new(CrewDecisionMode.None, [], []) { Unknown = true };

    /// <summary>The decision tasks that have no answer once the circuit is off, in the crew's order.</summary>
    public IReadOnlyList<string> TasksWithoutAnswer =>
        [.. DecisionTasks.Where(task => !task.AnswersWithoutModel).Select(task => task.Task)];

    /// <summary>What the crew <paramref name="target"/> runs asks of the decision models.</summary>
    public static CrewDecisionUsage Of(RunTarget? target) =>
        target is { Dialect: not RunTargetDialect.Yaml }
            ? Unreadable
            : EngineCrewReader.Read(target, Judge, None, Unreadable);

    /// <summary>
    /// What a single-file crew's text asks of the decision models. Unlike <see cref="Of"/>, a text the
    /// engine refuses reads <see cref="None"/>, not <see cref="Unreadable"/>: the caller (the forge,
    /// which wrote the text) has nothing to launch and reports the refusal itself.
    /// </summary>
    public static CrewDecisionUsage OfCrewFile(string yaml) => EngineCrewReader.ReadText(yaml, Judge, None);

    /// <summary>
    /// What the team or crew folder <paramref name="teamPath"/> asks of the decision models, for a team's
    /// card. The engine's loader reads the crew only when one of its YAML files names a decision, the
    /// tool, or a word System One's picks are asked with (<c>auto</c>, <c>prune</c>): a list of cards loads no crew that asks nothing. A crew it cannot read is
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
                    || DecisionKey().IsMatch(text)
                    || PickedBySystemOne().IsMatch(text);
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
        var others = new List<CrewSystemOneUse>();
        var named = new List<CrewNamedProfile>();
        // The engine routes an agent's tasks: a manager or a without: relay that no task is assigned to
        // runs on the default profile even on llm.profile: auto, and needs no ladder (CrewFactory.UnroutedAgents).
        var relays = crew.Tasks.Select(task => task.Decision?.Without?.Agent).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var assigned = crew.Tasks.Select(task => task.AssignedAgentId).Where(id => id is not null).ToHashSet();
        foreach (var agent in crew.Agents)
        {
            var key = CrewDeliverableWriters.KeyOf(agent);
            if (HoldsDecideTool(agent.Tools))
                holders.Add(key);

            var unrouted = !assigned.Contains(agent.Id)
                && (agent.Id.Equals(crew.ManagerAgentId) || relays.Contains(agent.Key ?? agent.Role));
            AddLlmUses(others, key, unrouted ? null : agent.LlmConfig?.Profile, agent.LlmConfig?.Thinking);
            AddNamedProfile(named, key, agent.LlmConfig?.Profile);
            if (agent.Skills is { Auto: true })
                others.Add(new CrewSystemOneUse(CrewSystemOneUseKind.SkillsAuto, key));
            if (agent.Compaction is { Prune: true })
                others.Add(new CrewSystemOneUse(CrewSystemOneUseKind.Prune, key));
        }

        var decisions = new List<CrewDecisionTask>();
        foreach (var task in crew.Tasks)
        {
            var name = CrewDeliverableWriters.NameOf(task);
            if (HoldsDecideTool(task.Tools))
                holders.Add(name);

            AddLlmUses(others, name, task.LlmOverride?.Profile, task.LlmOverride?.Thinking);
            AddNamedProfile(named, name, task.LlmOverride?.Profile);
            if (task.Decision is not { } decision)
                continue;

            var otherwise = crew.Tasks.FirstOrDefault(other =>
                other.Condition is { IsOtherwise: true } condition
                && string.Equals(condition.DecisionTask, name, StringComparison.Ordinal));
            decisions.Add(new CrewDecisionTask(
                name,
                [.. decision.Questions.Select(question => question.Name)],
                decision.Fallback,
                otherwise is null ? null : CrewDeliverableWriters.NameOf(otherwise))
            {
                RelayAgent = decision.Without?.Agent,
            });
        }

        var declared = crew.DecisionMode != Orkeon.Domain.Crew.CrewDecisionMode.Unspecified;
        var mode = crew.DecisionMode switch
        {
            Orkeon.Domain.Crew.CrewDecisionMode.None => CrewDecisionMode.None,
            Orkeon.Domain.Crew.CrewDecisionMode.Optional => CrewDecisionMode.Optional,
            Orkeon.Domain.Crew.CrewDecisionMode.Required => CrewDecisionMode.Required,
            _ when holders.Count > 0 || decisions.Any(task => !task.AnswersWithoutModel) => CrewDecisionMode.Required,
            _ when decisions.Count > 0 || others.Count > 0 => CrewDecisionMode.Optional,
            _ => CrewDecisionMode.None,
        };
        return new CrewDecisionUsage(mode, decisions, holders) { Declared = declared, OtherUses = others, NamedProfiles = named };
    }

    private static void AddNamedProfile(List<CrewNamedProfile> named, string holder, string? profile)
    {
        var name = profile?.Trim();
        if (!string.IsNullOrEmpty(name)
            && !string.Equals(name, LlmProfileNames.Auto, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, LlmProfileNames.Default, StringComparison.OrdinalIgnoreCase))
        {
            named.Add(new CrewNamedProfile(holder, name));
        }
    }

    /// <summary>What an agent's or a task's model block lets System One pick: its profile, its thinking.</summary>
    private static void AddLlmUses(
        List<CrewSystemOneUse> uses, string holder, string? profile, Orkeon.Domain.SharedKernel.ValueObjects.LlmThinkingConfig? thinking)
    {
        if (string.Equals(profile?.Trim(), LlmProfileNames.Auto, StringComparison.OrdinalIgnoreCase))
            uses.Add(new CrewSystemOneUse(CrewSystemOneUseKind.RouteAuto, holder));
        if (thinking is { AutoEffort: not null, HasAuto: true })
            uses.Add(new CrewSystemOneUse(CrewSystemOneUseKind.EffortAuto, holder));
        if (thinking is { AutoEnabled: true })
            uses.Add(new CrewSystemOneUse(CrewSystemOneUseKind.ThinkingAuto, holder));
    }

    // A "decision" key, bare or quoted, whatever the space before its colon.
    [System.Text.RegularExpressions.GeneratedRegex("""\bdecision["']?\s*:""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DecisionKey();

    // What System One picks is asked with the word "auto" (a profile, a thinking level, skills) or "prune".
    [System.Text.RegularExpressions.GeneratedRegex("""\b(auto|prune)\b""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex PickedBySystemOne();

    private static bool HoldsDecideTool(IEnumerable<string> tools) =>
        tools.Any(tool => string.Equals(tool, DecideTool, StringComparison.OrdinalIgnoreCase));
}
