using System.Text.Json.Nodes;

namespace Orkeon.Studio.Core.Forge;

/// <summary>One task of a forged blueprint, as an editor reads it (<c>tasks[]</c>, camelCase as the engine writes it).</summary>
/// <param name="Key">The task's <c>key</c>.</param>
/// <param name="Description">What to do: <c>description</c>.</param>
/// <param name="ExpectedOutput">What is expected: <c>expectedOutput</c>; a decision task has none.</param>
/// <param name="AgentKey">The agent that runs it: <c>agent</c>; a decision task has none.</param>
/// <param name="Dependencies">The keys of the tasks it waits for: <c>dependencies</c>.</param>
/// <param name="Deliverable">The virtual path of the file it writes: <c>deliverable</c>.</param>
/// <param name="Tools">The tools it grants on top of its agent's: <c>tools</c>.</param>
/// <param name="IsDecision">Whether it is a decision task (<c>decision</c> present).</param>
/// <param name="When">Its condition (<c>when</c>), as compact JSON; null when it has none.</param>
/// <param name="Questions">The names of a decision task's questions.</param>
public sealed record BlueprintTaskView(
    string Key,
    string Description,
    string? ExpectedOutput,
    string? AgentKey,
    IReadOnlyList<string> Dependencies,
    string? Deliverable,
    IReadOnlyList<string> Tools,
    bool IsDecision,
    string? When,
    IReadOnlyList<string> Questions)
{
    /// <summary>The condition's terms: the answer it reads (<c>triage.category</c>) and what it asks of it, as written.</summary>
    public IReadOnlyList<(string Answer, string Value)> WhenTerms { get; init; } = [];
}

/// <summary>What a task editor writes on a task; the rest of the task (<c>decision</c>, <c>when</c>, any other field) is kept.</summary>
/// <param name="Key">The task to amend; null to add one.</param>
/// <param name="Description">What to do.</param>
/// <param name="ExpectedOutput">What is expected.</param>
/// <param name="AgentKey">The agent that runs it.</param>
/// <param name="Dependencies">The tasks it waits for; none removes the field.</param>
/// <param name="Deliverable">The file it writes; blank removes the field.</param>
/// <param name="Tools">The tools it grants; none removes the field.</param>
public sealed record BlueprintTaskEdit(
    string? Key,
    string Description,
    string ExpectedOutput,
    string AgentKey,
    IReadOnlyList<string> Dependencies,
    string? Deliverable,
    IReadOnlyList<string> Tools);

/// <summary>One agent of a blueprint, as a task editor offers it: its key, and its role to show.</summary>
public sealed record BlueprintAgentRef(string Key, string Role);

/// <summary>
/// Reads the tasks of a forged blueprint and amends them, in <see cref="JsonNode"/> so that every
/// field this class does not know travels untouched. The amended JSON is sent over the engine's
/// <c>edit</c>, which re-validates everything (agents, dependencies, cycles, deliverables, tools):
/// these methods never have to be right about the engine's rules, only faithful to what was asked.
/// Every amending method answers null when the blueprint cannot be read.
/// </summary>
public static class BlueprintTasks
{
    /// <summary>The tasks, in the blueprint's order; a task without a key is skipped.</summary>
    public static IReadOnlyList<BlueprintTaskView> Read(string? blueprintJson)
    {
        if (BlueprintJson.Parse(blueprintJson)?["tasks"] is not JsonArray tasks)
            return [];

        var result = new List<BlueprintTaskView>();
        foreach (var task in tasks.OfType<JsonObject>())
        {
            if (BlueprintJson.Text(task["key"]) is not { Length: > 0 } key)
                continue;

            var decision = task["decision"] as JsonObject;
            var when = task["when"] as JsonObject;
            result.Add(new BlueprintTaskView(
                key,
                BlueprintJson.Text(task["description"]) ?? "",
                BlueprintJson.Text(task["expectedOutput"]),
                BlueprintJson.Text(task["agent"]),
                BlueprintJson.Strings(task["dependencies"]),
                BlueprintJson.Text(task["deliverable"]),
                BlueprintJson.Strings(task["tools"]),
                task["decision"] is not null,
                when?.ToJsonString(),
                decision?["questions"] is JsonObject questions ? [.. questions.Select(question => question.Key)] : [])
            {
                WhenTerms = when is null ? [] : [.. when.Select(term => (term.Key, ValueText(term.Value)))],
            });
        }

        return result;
    }

    /// <summary>The blueprint's agents, in its order; an agent without a key is skipped, one without a role shows its key.</summary>
    public static IReadOnlyList<BlueprintAgentRef> Agents(string? blueprintJson)
    {
        if (BlueprintJson.Parse(blueprintJson)?["agents"] is not JsonArray agents)
            return [];

        return [.. agents.OfType<JsonObject>()
            .Select(agent => (Key: BlueprintJson.Text(agent["key"]), Role: BlueprintJson.Text(agent["role"])))
            .Where(agent => agent.Key is { Length: > 0 })
            .Select(agent => new BlueprintAgentRef(agent.Key!, agent.Role is { Length: > 0 } role ? role : agent.Key!))];
    }

    /// <summary>The key a new task described by <paramref name="description"/> takes: a slug of its first words, unique among the tasks'.</summary>
    public static string NewKey(string? blueprintJson, string? description)
    {
        var words = (description ?? "").Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries).Take(4);
        return BlueprintJson.UniqueKey(string.Join(' ', words), "task", Read(blueprintJson).Select(task => task.Key));
    }

    /// <summary>
    /// Writes <paramref name="edit"/> on its task, or appends a new task under <see cref="NewKey"/>
    /// when <see cref="BlueprintTaskEdit.Key"/> is null or names none. An optional field left empty
    /// is removed, never kept silently.
    /// </summary>
    public static string? Upsert(string? blueprintJson, BlueprintTaskEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (BlueprintJson.Parse(blueprintJson) is not { } blueprint)
            return null;

        if (blueprint["tasks"] is not JsonArray tasks)
        {
            tasks = [];
            blueprint["tasks"] = tasks;
        }

        var target = edit.Key is { } key ? Find(tasks, key) : null;
        if (target is null)
        {
            target = new JsonObject { ["key"] = edit.Key ?? NewKey(blueprintJson, edit.Description) };
            tasks.Add(target);
        }

        target["description"] = edit.Description.Trim();
        target["expectedOutput"] = edit.ExpectedOutput.Trim();
        target["agent"] = edit.AgentKey;
        SetOrRemove(target, "dependencies", edit.Dependencies);
        if (edit.Deliverable?.Trim() is { Length: > 0 } deliverable)
            target["deliverable"] = deliverable;
        else
            target.Remove("deliverable");
        SetOrRemove(target, "tools", edit.Tools);

        return blueprint.ToJsonString();
    }

    /// <summary>
    /// The keys of the tasks whose condition reads the answer of <paramref name="key"/>
    /// (<c>when</c> on <c>key</c> or <c>key.question</c>): removing it would leave them a
    /// condition on nothing, which a screen says rather than erase.
    /// </summary>
    public static IReadOnlyList<string> ConditionedOn(string? blueprintJson, string key) =>
        [.. Read(blueprintJson)
            .Where(task => task.WhenTerms.Any(term =>
                string.Equals(term.Answer, key, StringComparison.Ordinal)
                || term.Answer.StartsWith(key + ".", StringComparison.Ordinal)))
            .Select(task => task.Key)];

    /// <summary>Removes the task <paramref name="key"/> and drops it from the others' dependencies.</summary>
    public static string? Remove(string? blueprintJson, string key)
    {
        if (BlueprintJson.Parse(blueprintJson) is not { } blueprint)
            return null;

        if (blueprint["tasks"] is JsonArray tasks)
        {
            for (var i = tasks.Count - 1; i >= 0; i--)
            {
                if (tasks[i] is JsonObject task && string.Equals(BlueprintJson.Text(task["key"]), key, StringComparison.Ordinal))
                    tasks.RemoveAt(i);
            }

            foreach (var task in tasks.OfType<JsonObject>())
            {
                var dependencies = BlueprintJson.Strings(task["dependencies"]);
                if (dependencies.Contains(key, StringComparer.Ordinal))
                    SetOrRemove(task, "dependencies", [.. dependencies.Where(dependency => !string.Equals(dependency, key, StringComparison.Ordinal))]);
            }
        }

        return blueprint.ToJsonString();
    }

    /// <summary>Moves the task <paramref name="key"/> by <paramref name="delta"/> places, kept within the list.</summary>
    public static string? Move(string? blueprintJson, string key, int delta)
    {
        if (BlueprintJson.Parse(blueprintJson) is not { } blueprint)
            return null;

        if (blueprint["tasks"] is JsonArray tasks && Find(tasks, key) is { } task)
        {
            var from = tasks.IndexOf(task);
            var to = Math.Clamp(from + delta, 0, tasks.Count - 1);
            if (to != from)
            {
                tasks.RemoveAt(from);
                tasks.Insert(to, task);
            }
        }

        return blueprint.ToJsonString();
    }

    private static JsonObject? Find(JsonArray tasks, string key) =>
        tasks.OfType<JsonObject>().FirstOrDefault(task => string.Equals(BlueprintJson.Text(task["key"]), key, StringComparison.Ordinal));

    private static void SetOrRemove(JsonObject target, string name, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
            target[name] = new JsonArray([.. values.Select(value => (JsonNode)value)]);
        else
            target.Remove(name);
    }

    private static string ValueText(JsonNode? value) => value switch
    {
        JsonArray array => string.Join(", ", array.Select(item => BlueprintJson.Text(item) ?? item?.ToJsonString() ?? "")),
        JsonValue when BlueprintJson.Text(value) is { } text => text,
        null => "",
        _ => value.ToJsonString(),
    };
}
