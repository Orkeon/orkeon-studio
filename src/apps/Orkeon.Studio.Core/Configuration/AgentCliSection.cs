using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// What the engine's host judges at start in <c>Llm:AgentCli</c>, the section of the experimental
/// <c>agent-cli</c> provider module (Orkeon ADR-020, <see cref="LlmProviderRules.AgentCliKey"/>). The
/// <c>orkeon</c> tool registers the module in every host it builds and validates its options at start,
/// whatever provider a section names: a value the binder cannot convert, an engine the module does not
/// have, a command it cannot run or a limit that bounds nothing stops every run. The options class and
/// its validator are internal or experimental in a package Studio does not reference, so the shape is
/// restated in <see cref="AgentCliSettings"/> and the rules sentence for sentence, held to the engine by
/// <c>AgentCliRulesOracleTests</c>. Studio writes none of these keys.
/// </summary>
public sealed class AgentCliSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = "Llm:AgentCli";

    /// <summary>The engine the module has: Claude Code's <c>claude -p</c>, in stream-json.</summary>
    public const string ClaudeEngine = "claude";

    /// <summary>The least <c>MaxToolResultCharacters</c> takes: room for a result and the note that cuts it.</summary>
    public const int MinToolResultCharacters = 1_000;

    private readonly AppSettingsDocument _document;

    internal AgentCliSection(AppSettingsDocument document) => _document = document;

    /// <summary>
    /// What the host's start refuses in the section: a file the configuration does not load, when the
    /// key it names is the section's; a value the binder cannot convert; then, on the values that did
    /// convert, the module's refusals in the order of its keys; and a key that is no setting of the
    /// section. Each with the path of its key, in the engine's sentence; empty when the section is accepted.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var configuration = DecisionSection.ToConfiguration(_document.Root);
        var problems = new List<(string, string)>();
        if (_document.LoadRefusal() is { } load && SettingsBinding.IsUnder(load.Path, SectionPath))
            problems.Add(load);
        var options = SettingsBinding.Bind<AgentCliSettings>(configuration, SectionPath, problems);
        problems.AddRange(Refusals(options));
        SettingsBinding.AddWalked(problems, SettingsBinding.UnknownKeys(configuration, SectionPath, typeof(AgentCliSettings)));
        return problems;
    }

    /// <summary>
    /// The module's refusals of <paramref name="options"/>, at their keys, in the order of its keys
    /// (<c>AgentCliOptionsValidator.Refusals</c>): each names its key and what it takes, never the value.
    /// </summary>
    public static IReadOnlyList<(string Path, string Problem)> Refusals(AgentCliSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var refusals = new List<(string, string)>();
        if (!string.Equals(options.Engine?.Trim(), ClaudeEngine, StringComparison.OrdinalIgnoreCase))
            refusals.Add(($"{SectionPath}:Engine", $"{SectionPath}:Engine is not an engine of the agent-cli provider: write {ClaudeEngine}, the only one."));
        if (CommandRefusal(options.Command) is { } command)
            refusals.Add(($"{SectionPath}:Command", command));
        if (options.MaxProcesses <= 0)
            refusals.Add(($"{SectionPath}:MaxProcesses", $"{SectionPath}:MaxProcesses takes a whole number of 1 or more: the CLI processes that may live at once."));
        if (options.MaxToolResultCharacters < MinToolResultCharacters)
        {
            refusals.Add(($"{SectionPath}:MaxToolResultCharacters",
                $"{SectionPath}:MaxToolResultCharacters takes a whole number of {MinToolResultCharacters} or more: the longest tool result handed to the CLI, in characters."));
        }

        if (options.AutocompactTokens <= 0)
        {
            refusals.Add(($"{SectionPath}:AutocompactTokens",
                $"{SectionPath}:AutocompactTokens takes a whole number of 1 or more: the context size at which the CLI would compact the conversation itself."));
        }

        return refusals;
    }

    /// <summary>
    /// Why the command is refused, or null: empty, or a <c>.cmd</c> or <c>.bat</c> file, which Windows
    /// runs through <c>cmd.exe</c> and its reading of the JSON arguments of the command line.
    /// </summary>
    private static string? CommandRefusal(string? value)
    {
        var command = value?.Trim();
        if (string.IsNullOrEmpty(command))
            return $"{SectionPath}:Command takes the CLI's executable: a name looked up on the PATH, or a full path.";

        var extension = Path.GetExtension(command);
        return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
            ? $"{SectionPath}:Command takes an executable, not a .cmd or .bat script, which Windows runs through cmd.exe and its parsing of the arguments: name the CLI's .exe (the native installer's claude.exe)."
            : null;
    }
}
