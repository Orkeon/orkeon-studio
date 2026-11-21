using Orkeon.Application.Interfaces.Ports;
using Orkeon.Infrastructure.LLMs.Decision;
using Orkeon.Infrastructure.LLMs.Decision.Stages;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The rules of the decision profiles (<c>Orkeon:Decision:Profiles</c>), as the engine's host judges
/// them while it is composed (<c>DecisionProfileServiceCollectionExtensions.Validate</c>, internal to
/// the engine): a reserved name, a missing target, no stage, a stage of an unknown kind, a route with
/// no profile, a guard with no risk, a verify with nowhere to escalate, a probability outside [0, 1],
/// a phase or steps that are none, a profile that names another decision profile. Restated here,
/// sentence for sentence and in the engine's order, and pinned against the original by
/// <c>DecisionProfileRulesOracleTests</c>, which runs both over the same settings. The values
/// themselves are converted by the configuration binder, as the engine's are.
/// </summary>
internal static class DecisionProfileRules
{
    private const string RouteKind = DecisionProfileServiceCollectionExtensions.RouteKind;
    private const string GuardKind = DecisionProfileServiceCollectionExtensions.GuardKind;
    private const string VerifyKind = DecisionProfileServiceCollectionExtensions.VerifyKind;

    private static readonly string[] KnownKinds = [RouteKind, GuardKind, VerifyKind];

    /// <summary>
    /// What the host refuses in the bound profiles, each with the path of its key. The engine stops on
    /// the first: the first here is its sentence, and the following ones are what it says once the
    /// first is mended. Empty when every profile is accepted.
    /// </summary>
    public static IEnumerable<(string Path, string Problem)> Problems(DecisionProfilesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (name, bound) in options.Profiles)
        {
            var profile = bound ?? new DecisionProfileOptions();
            var path = $"{DecisionProfilesOptions.SectionName}:Profiles:{name}";
            if (LlmProfiles.IsAuto(name))
                yield return (path, LlmProfiles.AutoReservedMessage(path));
            if (Named(options, path + ":Target", profile.Target, required: true) is { } target)
                yield return target;
            if (profile.Stages.Count == 0)
                yield return ($"{path}:Stages", $"{path}:Stages: a decision profile needs at least one stage.");
            for (var i = 0; i < profile.Stages.Count; i++)
            {
                foreach (var problem in StageProblems(options, $"{path}:Stages:{i}", profile.Stages[i] ?? new DecisionStageOptions()))
                    yield return problem;
            }
        }
    }

    private static IEnumerable<(string Path, string Problem)> StageProblems(DecisionProfilesOptions options, string path, DecisionStageOptions stage)
    {
        switch (KindOf(stage.Kind))
        {
            case RouteKind:
                if (stage.Routes.Count == 0)
                    yield return ($"{path}:Routes", $"{path}:Routes: a route stage needs at least one profile.");
                foreach (var route in stage.Routes.Keys)
                {
                    if (Named(options, $"{path}:Routes:{route}", route, required: true) is { } named)
                        yield return named;
                }

                if (Named(options, path + ":Fallback", stage.Fallback, required: false) is { } fallback)
                    yield return fallback;
                if (Probability(path + ":MinConfidence", stage.MinConfidence) is { } confidence)
                    yield return confidence;
                break;
            case GuardKind:
                if (stage.Risks.Count == 0)
                    yield return ($"{path}:Risks", $"{path}:Risks: a guard stage needs at least one risk.");
                if (!IsOneOf<DecisionStagePhase>(stage.Phase))
                    yield return ($"{path}:Phase", $"{path}:Phase: '{stage.Phase}' is not request, response or both.");
                if (Probability(path + ":BlockThreshold", stage.BlockThreshold) is { } block)
                    yield return block;
                if (Probability(path + ":WarnThreshold", stage.WarnThreshold) is { } warn)
                    yield return warn;
                break;
            case VerifyKind:
                if (Named(options, path + ":EscalateTo", stage.EscalateTo, required: true) is { } escalate)
                    yield return escalate;
                if (Probability(path + ":MinProbability", stage.MinProbability) is { } probability)
                    yield return probability;
                if (!IsOneOf<DecisionVerifySteps>(stage.Steps))
                    yield return ($"{path}:Steps", $"{path}:Steps: '{stage.Steps}' is not final or all.");
                break;
            default:
                yield return ($"{path}:Kind", $"{path}:Kind: '{stage.Kind}' is not a stage; expected {RouteKind}, {GuardKind} or {VerifyKind}.");
                break;
        }
    }

    // A profile a decision profile names: present when required, and never a decision profile.
    private static (string Path, string Problem)? Named(DecisionProfilesOptions options, string key, string? name, bool required)
    {
        if (string.IsNullOrWhiteSpace(name))
            return required ? (key, $"{key}: a profile name is required.") : null;

        return options.Profiles.ContainsKey(name.Trim())
            ? (key, $"{key}: '{name}' is a decision profile; a decision profile names LLM profiles only.")
            : null;
    }

    // The stage kind a text names, trimmed, whatever its letter case; itself when it names none.
    private static string KindOf(string? kind)
    {
        var trimmed = (kind ?? string.Empty).Trim();
        return KnownKinds.FirstOrDefault(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
    }

    // The number is written as the engine writes it: in the culture of the process that says it.
    private static (string Path, string Problem)? Probability(string key, double value) =>
        value is < 0 or > 1 || double.IsNaN(value) ? (key, $"{key}: {value} is not a probability between 0 and 1.") : null;

    // A text left blank, or one that names a member of the engine's enumeration, trimmed, in any letter case.
    private static bool IsOneOf<TEnum>(string? text)
        where TEnum : struct, Enum =>
        string.IsNullOrWhiteSpace(text) || (Enum.TryParse<TEnum>(text.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed));
}
