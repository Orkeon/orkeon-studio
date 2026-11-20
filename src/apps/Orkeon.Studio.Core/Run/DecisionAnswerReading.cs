using System.Text.Json;

namespace Orkeon.Studio.Core.Run;

/// <summary>
/// One answer of a decision task, as its <c>task.completed</c> line carries it: the engine's compact JSON
/// (<c>DecisionOutcome.ToCompactJson</c>) holds, per question, the chosen option (<c>yes</c> or <c>no</c> for a
/// yes/no question), the probability of each option, the yes probability of a yes/no question, the weighted
/// score of a score question, and the confidence. The probabilities and the confidence are shares in [0, 1];
/// a score is a position among the question's levels, from 0, and a fallback score is its level's index.
/// </summary>
/// <param name="Question">The question's name, as the task's YAML writes it.</param>
/// <param name="Chosen">The option chosen, or null (a score question).</param>
/// <param name="Likelihood">
/// How likely the chosen option is: its probability, or for a yes/no question the yes probability when
/// <c>yes</c> is chosen and its complement when <c>no</c> is; the yes probability when nothing is chosen;
/// null when the line says neither.
/// </param>
/// <param name="Score">The weighted score of a score question (a position among its levels), else null.</param>
/// <param name="Confidence">How sure the model was.</param>
public sealed record DecisionAnswerReading(string Question, string? Chosen, double? Likelihood, double? Score, double? Confidence)
{
    /// <summary>The key the engine adds to a defaulted outcome: no question takes that name.</summary>
    public const string DefaultedKey = "defaulted";

    /// <summary>
    /// Reads the answers of a decision task in the order the line writes them; an empty list for a null,
    /// blank or unreadable text, or one that is no object: the raw text stays the task's to show.
    /// </summary>
    public static IReadOnlyList<DecisionAnswerReading> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var answers = new List<DecisionAnswerReading>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == DefaultedKey || property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                answers.Add(Read(property.Name, property.Value));
            }

            return answers;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DecisionAnswerReading Read(string question, JsonElement answer)
    {
        var chosen = answer.TryGetProperty("chosen", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        double? likelihood = null;
        if (chosen is not null
            && answer.TryGetProperty("probabilities", out var probabilities)
            && probabilities.ValueKind == JsonValueKind.Object
            && probabilities.TryGetProperty(chosen, out var p))
        {
            likelihood = Number(p);
        }

        // A yes/no answer: the engine writes the chosen side and the yes probability, no option table.
        if (likelihood is null && Field(answer, "probability") is { } yes)
        {
            likelihood = chosen switch
            {
                null or "yes" => yes,
                "no" => 1 - yes,
                _ => null,
            };
        }

        return new DecisionAnswerReading(question, chosen, likelihood, Field(answer, "score"), Field(answer, "confidence"));
    }

    private static double? Field(JsonElement answer, string name) =>
        answer.TryGetProperty(name, out var value) ? Number(value) : null;

    private static double? Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}
