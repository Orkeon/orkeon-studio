using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Presentation;

/// <summary>
/// What a run does around an agent's context, as text fields: the compaction of a context that nears
/// its model's window (<c>Orkeon:Compaction</c>, on by default), the pruning of its older tool results
/// by System One (<c>Orkeon:Compaction:Prune</c>, off by default), and the skills an agent may carry
/// (<c>Orkeon:Skills</c>), with the context window of the default model (<c>Llm:ContextWindowTokens</c>),
/// which a compaction measures against. A field left blank removes its key and the engine's default stands. A value
/// the engine's start refuses is refused at apply, in the engine's words, and nothing is written.
/// </summary>
internal sealed class ContextForm : ISettingsForm
{
    /// <inheritdoc />
    public string Title => "Context & skills";

    /// <summary>What a blank field means, and how a share is written.</summary>
    public const string Notice = "Empty field: the engine's default. Shares: above 0, at most 1.";

    /// <summary>How a switch is written, and what each one is by default.</summary>
    public const string SwitchNotice = "Switches: true or false. By default compaction is on, pruning off.";

    /// <summary>Whether a context is compacted: <c>true</c>, <c>false</c>, or blank for the default.</summary>
    public string CompactionEnabled { get; set; } = "";

    /// <summary>The share of the window that starts a compaction.</summary>
    public string TriggerShare { get; set; } = "";

    /// <summary>The latest turns a compaction leaves as they are.</summary>
    public string KeepRecentTurns { get; set; } = "";

    /// <summary>The model profile that writes the summary; blank, each agent's own model does.</summary>
    public string Profile { get; set; } = "";

    /// <summary>The context window of the default model, in tokens; blank, the window the engine documents for it.</summary>
    public string ContextWindowTokens { get; set; } = "";

    /// <summary>Whether tool results are pruned: <c>true</c>, <c>false</c>, or blank for the default.</summary>
    public string PruneEnabled { get; set; } = "";

    /// <summary>The share of the window that starts a pruning.</summary>
    public string PruneFillShare { get; set; } = "";

    /// <summary>The latest messages a pruning never judges.</summary>
    public string PruneKeepRecentMessages { get; set; } = "";

    /// <summary>The characters kept of a tool result that is cut.</summary>
    public string PruneHeadChars { get; set; } = "";

    /// <summary>The virtual folder the skills are read from.</summary>
    public string SkillsLibrary { get; set; } = "";

    /// <summary>The characters kept of one skill.</summary>
    public string MaxSkillChars { get; set; } = "";

    /// <summary>The characters of the whole block of skills in a prompt.</summary>
    public string MaxBlockChars { get; set; } = "";

    /// <inheritdoc />
    public void LoadFrom(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // As the file writes them, so an unreadable value is shown and refused, never erased.
        CompactionEnabled = document.GetWritten($"{CompactionSection.SectionPath}:Enabled");
        TriggerShare = document.GetWritten($"{CompactionSection.SectionPath}:TriggerShare");
        KeepRecentTurns = document.GetWritten($"{CompactionSection.SectionPath}:KeepRecentTurns");
        Profile = FieldText.FromString(document.Compaction.Profile);
        ContextWindowTokens = document.GetWritten($"{LlmSection.SectionPath}:{LlmProfilesSection.ContextWindowTokensKey}");
        PruneEnabled = document.GetWritten($"{CompactionSection.PrunePath}:Enabled");
        PruneFillShare = document.GetWritten($"{CompactionSection.PrunePath}:FillShare");
        PruneKeepRecentMessages = document.GetWritten($"{CompactionSection.PrunePath}:KeepRecentMessages");
        PruneHeadChars = document.GetWritten($"{CompactionSection.PrunePath}:HeadChars");
        SkillsLibrary = FieldText.FromString(document.Skills.Library);
        MaxSkillChars = document.GetWritten($"{SkillsSection.SectionPath}:MaxSkillChars");
        MaxBlockChars = document.GetWritten($"{SkillsSection.SectionPath}:MaxBlockChars");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ApplyTo(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();

        var enabled = Boolean(CompactionEnabled, $"{CompactionSection.SectionPath}:Enabled", errors);
        var trigger = Double(TriggerShare, $"{CompactionSection.SectionPath}:TriggerShare", errors);
        var turns = Int32(KeepRecentTurns, $"{CompactionSection.SectionPath}:KeepRecentTurns", errors);
        var window = Int32(ContextWindowTokens, $"{LlmSection.SectionPath}:{LlmProfilesSection.ContextWindowTokensKey}", errors);
        if (window is <= 0)
        {
            errors.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{LlmSection.SectionPath}:{LlmProfilesSection.ContextWindowTokensKey}: {window} is not a number of tokens above zero. Leave the field empty for the model's documented window."));
        }

        var prune = Boolean(PruneEnabled, $"{CompactionSection.PrunePath}:Enabled", errors);
        var fill = Double(PruneFillShare, $"{CompactionSection.PrunePath}:FillShare", errors);
        var messages = Int32(PruneKeepRecentMessages, $"{CompactionSection.PrunePath}:KeepRecentMessages", errors);
        var head = Int32(PruneHeadChars, $"{CompactionSection.PrunePath}:HeadChars", errors);
        var skillChars = Int32(MaxSkillChars, $"{SkillsSection.SectionPath}:MaxSkillChars", errors);
        var blockChars = Int32(MaxBlockChars, $"{SkillsSection.SectionPath}:MaxBlockChars", errors);
        if (errors.Count > 0)
            return errors;

        void Write(AppSettingsDocument target)
        {
            var compaction = target.Compaction;
            compaction.Enabled = enabled;
            compaction.TriggerShare = trigger;
            compaction.KeepRecentTurns = turns;
            compaction.Profile = FieldText.ToStringOrNull(Profile);
            target.Llm.ContextWindowTokens = window;
            compaction.PruneEnabled = prune;
            compaction.PruneFillShare = fill;
            compaction.PruneKeepRecentMessages = messages;
            compaction.PruneHeadChars = head;
            var skills = target.Skills;
            skills.Library = FieldText.ToStringOrNull(SkillsLibrary);
            skills.MaxSkillChars = skillChars;
            skills.MaxBlockChars = blockChars;
        }

        // Judged on a copy by the engine's own rules: what its start refuses is never written.
        var probe = document.Clone();
        Write(probe);
        errors.AddRange(probe.Compaction.Problems().Concat(probe.Skills.Problems()).Select(problem => problem.Problem));
        if (errors.Count > 0)
            return errors;

        Write(document);
        return [];
    }

    private static bool? Boolean(string text, string fieldName, List<string> errors)
    {
        if (FieldText.TryReadBoolean(text, fieldName, out var value, out var error))
            return value;

        errors.Add(error!);
        return null;
    }

    private static int? Int32(string text, string fieldName, List<string> errors)
    {
        if (FieldText.TryReadInt32(text, fieldName, out var value, out var error))
            return value;

        errors.Add(error!);
        return null;
    }

    private static double? Double(string text, string fieldName, List<string> errors)
    {
        if (FieldText.TryReadDouble(text, fieldName, out var value, out var error))
            return value;

        errors.Add(error!);
        return null;
    }
}
