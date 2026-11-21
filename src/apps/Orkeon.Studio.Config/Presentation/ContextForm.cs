using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Presentation;

/// <summary>
/// What a run does around an agent's context, as text fields: the compaction of a context that nears
/// its model's window (<c>Orkeon:Compaction</c>, on by default), the pruning of its older tool results
/// by System One (<c>Orkeon:Compaction:Prune</c>, off by default), and the skills an agent may carry
/// (<c>Orkeon:Skills</c>), with the context window of the default model (<c>Llm:ContextWindowTokens</c>),
/// which a compaction measures against. Every key is read and written where the file spells it, in
/// whatever letter case (<see cref="AppSettingsDocument.PathAsWritten"/>). Only a field that changed
/// since it was loaded is written: one left as loaded keeps its key as the file writes it
/// (<c>"8000"</c> stays a string), and a changed field left blank removes its key, the engine's default
/// then standing. After an apply that wrote, the fields show the document as written. A value the engine's start refuses in a key this screen shows is refused at apply, in
/// the engine's words, and nothing is written; a refusal about any other key of the sections (one that
/// is no setting of them) is the settings check's to report, and blocks no edit here.
/// </summary>
internal sealed class ContextForm : ISettingsForm
{
    /// <inheritdoc />
    public string Title => "Context & skills";

    /// <summary>What a blank field means: the engine's default, which its caption gives in brackets.</summary>
    public const string Notice = "Empty field: the default, in brackets.";

    /// <summary>How a share is written.</summary>
    public const string ShareNotice = "Shares: above 0, at most 1.";

    /// <summary>How a switch is written.</summary>
    public const string SwitchNotice = "Switches: true or false.";

    /// <summary>The two fields whose default is no fixed value.</summary>
    public const string OwnNotice = "No profile: the agent's own model.";

    /// <summary>The window an empty field stands for.</summary>
    public const string WindowNotice = "No window: the model's documented one.";

    /// <summary>The library an empty field stands for: too long for a caption.</summary>
    public const string LibraryNotice = "Default library: " + SkillsSection.DefaultLibrary;

    /// <summary>
    /// Whether a context is compacted: <c>true</c>, <c>false</c>, or blank for the default; <c>false</c>
    /// for a key the file writes without a value, which a run reads as off.
    /// </summary>
    public string CompactionEnabled { get; set; } = "";

    /// <summary>The share of the window that starts a compaction.</summary>
    public string TriggerShare { get; set; } = "";

    /// <summary>The latest turns a compaction leaves as they are.</summary>
    public string KeepRecentTurns { get; set; } = "";

    /// <summary>The model profile that writes the summary; blank, each agent's own model does.</summary>
    public string Profile { get; set; } = "";

    /// <summary>The context window of the default model, in tokens; blank, the window the engine documents for it.</summary>
    public string ContextWindowTokens { get; set; } = "";

    /// <summary>Whether tool results are pruned, as <see cref="CompactionEnabled"/> reads.</summary>
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

    private const string CompactionEnabledPath = CompactionSection.SectionPath + ":Enabled";
    private const string TriggerSharePath = CompactionSection.SectionPath + ":TriggerShare";
    private const string KeepRecentTurnsPath = CompactionSection.SectionPath + ":KeepRecentTurns";
    private const string ProfilePath = CompactionSection.SectionPath + ":Profile";
    private const string ContextWindowTokensPath = LlmSection.SectionPath + ":" + LlmProfilesSection.ContextWindowTokensKey;
    private const string PruneEnabledPath = CompactionSection.PrunePath + ":Enabled";
    private const string PruneFillSharePath = CompactionSection.PrunePath + ":FillShare";
    private const string PruneKeepRecentMessagesPath = CompactionSection.PrunePath + ":KeepRecentMessages";
    private const string PruneHeadCharsPath = CompactionSection.PrunePath + ":HeadChars";
    private const string SkillsLibraryPath = SkillsSection.SectionPath + ":Library";
    private const string MaxSkillCharsPath = SkillsSection.SectionPath + ":MaxSkillChars";
    private const string MaxBlockCharsPath = SkillsSection.SectionPath + ":MaxBlockChars";

    /// <summary>The keys of the engine's sections this screen edits: a refusal about another key blocks no edit here.</summary>
    private static readonly string[] EditedKeys =
    [
        CompactionEnabledPath, TriggerSharePath, KeepRecentTurnsPath, ProfilePath,
        PruneEnabledPath, PruneFillSharePath, PruneKeepRecentMessagesPath, PruneHeadCharsPath,
        SkillsLibraryPath, MaxSkillCharsPath, MaxBlockCharsPath,
    ];

    // The fields as the file gave them: a field left as loaded is not written again.
    private Fields _loaded = new("", "", "", "", "", "", "", "", "", "", "", "");

    private Fields Current => new(
        CompactionEnabled, TriggerShare, KeepRecentTurns, Profile, ContextWindowTokens, PruneEnabled,
        PruneFillShare, PruneKeepRecentMessages, PruneHeadChars, SkillsLibrary, MaxSkillChars, MaxBlockChars);

    /// <inheritdoc />
    public void LoadFrom(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // As the file writes them, so an unreadable value is shown and refused, never erased; and
        // where the file spells them, so a key in another letter case is the one shown.
        string Written(string path) => document.GetWritten(document.PathAsWritten(path));

        // A switch written without a value (a JSON null, an empty object) reads as no boolean and
        // binds to false: shown as the run gets it, never as the default a blank field stands for nor
        // as a text the screen would refuse. A text the start refuses is shown as written. The key
        // stays as written until the field changes.
        var compaction = document.Compaction;
        var refused = compaction.Problems().Select(problem => problem.Path).ToList();
        string Switch(string path, bool? asWritten, bool run, bool engineDefault)
        {
            var written = Written(path);
            return asWritten is null
                && !refused.Contains(path, StringComparer.OrdinalIgnoreCase)
                && (run != engineDefault || !string.IsNullOrWhiteSpace(written))
                ? (run ? "true" : "false")
                : written;
        }

        CompactionEnabled = Switch(CompactionEnabledPath, compaction.Enabled, compaction.IsEnabled, CompactionSection.DefaultEnabled);
        TriggerShare = Written(TriggerSharePath);
        KeepRecentTurns = Written(KeepRecentTurnsPath);
        Profile = FieldText.FromString(compaction.Profile);
        ContextWindowTokens = Written(ContextWindowTokensPath);
        PruneEnabled = Switch(PruneEnabledPath, compaction.PruneEnabled, compaction.IsPruneEnabled, CompactionSection.DefaultPruneEnabled);
        PruneFillShare = Written(PruneFillSharePath);
        PruneKeepRecentMessages = Written(PruneKeepRecentMessagesPath);
        PruneHeadChars = Written(PruneHeadCharsPath);
        SkillsLibrary = FieldText.FromString(document.Skills.Library);
        MaxSkillChars = Written(MaxSkillCharsPath);
        MaxBlockChars = Written(MaxBlockCharsPath);
        _loaded = Current;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ApplyTo(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();

        // Every field is read, changed or not: a value the screen shows and cannot read is refused.
        var enabled = Boolean(CompactionEnabled, CompactionEnabledPath, errors);
        var trigger = Double(TriggerShare, TriggerSharePath, errors);
        var turns = Int32(KeepRecentTurns, KeepRecentTurnsPath, errors);
        var window = Int32(ContextWindowTokens, ContextWindowTokensPath, errors);
        if (window is <= 0)
        {
            errors.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{ContextWindowTokensPath}: {window} is not a number of tokens above zero. Leave the field empty for the model's documented window."));
        }

        var prune = Boolean(PruneEnabled, PruneEnabledPath, errors);
        var fill = Double(PruneFillShare, PruneFillSharePath, errors);
        var messages = Int32(PruneKeepRecentMessages, PruneKeepRecentMessagesPath, errors);
        var head = Int32(PruneHeadChars, PruneHeadCharsPath, errors);
        var skillChars = Int32(MaxSkillChars, MaxSkillCharsPath, errors);
        var blockChars = Int32(MaxBlockChars, MaxBlockCharsPath, errors);
        if (errors.Count > 0)
            return errors;

        var typed = Current;

        void Write(AppSettingsDocument target)
        {
            var compaction = target.Compaction;
            if (typed.CompactionEnabled != _loaded.CompactionEnabled)
                compaction.Enabled = enabled;
            if (typed.TriggerShare != _loaded.TriggerShare)
                compaction.TriggerShare = trigger;
            if (typed.KeepRecentTurns != _loaded.KeepRecentTurns)
                compaction.KeepRecentTurns = turns;
            if (typed.Profile != _loaded.Profile)
                compaction.Profile = FieldText.ToStringOrNull(typed.Profile);
            if (typed.ContextWindowTokens != _loaded.ContextWindowTokens)
                target.SetInt32(target.PathAsWritten(ContextWindowTokensPath), window);
            if (typed.PruneEnabled != _loaded.PruneEnabled)
                compaction.PruneEnabled = prune;
            if (typed.PruneFillShare != _loaded.PruneFillShare)
                compaction.PruneFillShare = fill;
            if (typed.PruneKeepRecentMessages != _loaded.PruneKeepRecentMessages)
                compaction.PruneKeepRecentMessages = messages;
            if (typed.PruneHeadChars != _loaded.PruneHeadChars)
                compaction.PruneHeadChars = head;
            var skills = target.Skills;
            if (typed.SkillsLibrary != _loaded.SkillsLibrary)
                skills.Library = FieldText.ToStringOrNull(typed.SkillsLibrary);
            if (typed.MaxSkillChars != _loaded.MaxSkillChars)
                skills.MaxSkillChars = skillChars;
            if (typed.MaxBlockChars != _loaded.MaxBlockChars)
                skills.MaxBlockChars = blockChars;
        }

        // Judged on a copy by the engine's own rules: what its start refuses in a key of this screen
        // is never written. A refusal about another key (one that is no setting of its section) is
        // the settings check's to report: this screen cannot remove it, and it blocks no edit here.
        var probe = document.Clone();
        Write(probe);
        errors.AddRange(probe.Compaction.Problems().Concat(probe.Skills.Problems())
            .Where(problem => EditedKeys.Contains(problem.Path, StringComparer.OrdinalIgnoreCase))
            .Select(problem => problem.Problem));
        if (errors.Count > 0)
            return errors;

        Write(document);
        // The document as written is the new starting point, and what the fields show: a value as it
        // was stored, and a key another form took away since the load (the window of a model that changed).
        LoadFrom(document);
        return [];
    }

    private sealed record Fields(
        string CompactionEnabled, string TriggerShare, string KeepRecentTurns, string Profile, string ContextWindowTokens,
        string PruneEnabled, string PruneFillShare, string PruneKeepRecentMessages, string PruneHeadChars,
        string SkillsLibrary, string MaxSkillChars, string MaxBlockChars);

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
