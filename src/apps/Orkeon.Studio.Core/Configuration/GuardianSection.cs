using Microsoft.Extensions.Configuration;
using Orkeon.Infrastructure.Configuration;
using Orkeon.Infrastructure.Decision;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// What the engine's host judges at start in <c>Orkeon:Guardian</c>: the guards of a run, their default
/// policy and the LLM judge (<c>Orkeon:Guardian:Llm</c>). Studio writes none of its keys but the
/// switches <see cref="DecisionSection"/> reads; it says what the start refuses in the section, by
/// the engine's own options classes (<see cref="GuardianOptions"/>, <see cref="GuardianLlmOptions"/>)
/// and its three rules (<c>AddOrkeonGuardian</c>, <c>GuardianLlmPhasesValidator</c>, the latter internal
/// to the engine), restated sentence for sentence and held to the host by <c>EngineStartAndLoadOracleTests</c>.
/// </summary>
public sealed class GuardianSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = "Orkeon:Guardian";

    private const string PhasesPath = GuardianLlmOptions.SectionName + ":Phases";

    private readonly AppSettingsDocument _document;

    internal GuardianSection(AppSettingsDocument document) => _document = document;

    /// <summary>
    /// What the host's start refuses in the section: a file the configuration does not load, when the
    /// key it names is the section's; a value the binder cannot convert; a phase of
    /// <c>Orkeon:Guardian:Llm:Phases</c> a model does not judge, a threshold outside [0, 1], a
    /// <c>MaxContentChars</c> under one, each judged on the values that did convert; and a key that is
    /// no setting of the section, at any depth, or a threshold that is no finite number. Each with the
    /// path of its key, in the engine's sentence and the host's order; empty when the section is accepted.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var configuration = DecisionSection.ToConfiguration(_document.Root);
        var problems = new List<(string, string)>();
        if (_document.LoadRefusal() is { } load && SettingsBinding.IsUnder(load.Path, SectionPath))
            problems.Add(load);
        Read(configuration, problems, out var refused);
        problems.AddRange(refused);
        problems.AddRange(SettingsBinding.UnknownKeys(configuration, SectionPath, typeof(GuardianOptions)));
        return problems;
    }

    /// <summary>
    /// The section as the engine binds it, each value the binder cannot convert added to
    /// <paramref name="unconverted"/>, and in <paramref name="refused"/> what the section's rules
    /// refuse in the values that did convert, in the order the host says them.
    /// </summary>
    internal static GuardianOptions Read(
        IConfiguration configuration, List<(string Path, string Problem)> unconverted, out IReadOnlyList<(string Path, string Problem)> refused)
    {
        var guardian = SettingsBinding.Bind<GuardianOptions>(configuration, SectionPath, unconverted);
        var problems = new List<(string, string)>();

        // The phases are judged as the file writes them, not as the binder read them: it leaves out an
        // item it cannot convert. A number that names a member is that member to the engine.
        var phases = configuration.GetSection(PhasesPath).GetChildren()
            .Select(phase => phase.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !Enum.TryParse<GuardianJudgePhase>(value, ignoreCase: true, out var phase) || !Enum.IsDefined(phase))
            .ToList();
        if (phases.Count > 0)
        {
            problems.Add((PhasesPath,
                $"{PhasesPath}: '{string.Join("', '", phases)}' is not a phase a model judges. "
                + "Only `Input` and `Output` are (`ToolExecution` and `Delegation` are checked by rules); "
                + "remove the other values or leave the list empty to judge both."));
        }

        var llm = guardian.Llm ?? new GuardianLlmOptions();
        if (llm.BlockThreshold is not (>= 0 and <= 1) || llm.WarnThreshold is not (>= 0 and <= 1))
        {
            // One sentence for the two keys: said at the first that breaks it.
            var key = llm.BlockThreshold is not (>= 0 and <= 1) ? "BlockThreshold" : "WarnThreshold";
            problems.Add(($"{GuardianLlmOptions.SectionName}:{key}",
                $"{GuardianLlmOptions.SectionName}:BlockThreshold and WarnThreshold: probabilities in [0, 1]."));
        }

        if (llm.MaxContentChars <= 0)
            problems.Add(($"{GuardianLlmOptions.SectionName}:MaxContentChars", $"{GuardianLlmOptions.SectionName}:MaxContentChars: one character or more."));

        refused = problems;
        return guardian;
    }
}
