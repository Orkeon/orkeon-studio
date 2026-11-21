using System.Globalization;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// The <c>Orkeon:Compaction</c> form: whether a context that nears its model's window is compacted
/// into a summary (on by default), when and how much of it is kept, and whether System One first
/// prunes the older tool results (<c>Orkeon:Compaction:Prune</c>, off by default). Each field carries
/// the engine's default as a watermark, and a switch set back to its default removes its key. What the
/// engine's start refuses is said under the fields, in the engine's words.
/// </summary>
public sealed class CompactionSectionViewModel : DocumentSectionViewModel
{
    /// <summary>Binds the form to the <c>Orkeon:Compaction</c> section of the document.</summary>
    public CompactionSectionViewModel(Func<AppSettingsDocument> document, Action onChanged)
        : base(document, onChanged)
    {
    }

    private CompactionSection Section => Document.Compaction;

    /// <inheritdoc />
    public override bool Exists => Section.Exists;

    /// <summary>Whether a context is compacted; set back to the engine's default, the key is removed.</summary>
    public bool Enabled
    {
        get => Section.IsEnabled;
        set => SetValue(Enabled, value, v => Section.Enabled = v == CompactionSection.DefaultEnabled ? null : v);
    }

    /// <summary>The share of the window that starts a compaction, as typed (a point or a comma for the decimals).</summary>
    public string TriggerShare
    {
        get => ShareText(Section.TriggerShare);
        set => SetShare(TriggerShare, value, v => Section.TriggerShare = v);
    }

    /// <summary>The watermark of <see cref="TriggerShare"/>.</summary>
    public static string TriggerShareDefault => Invariant(CompactionSection.DefaultTriggerShare);

    /// <summary>The latest turns a compaction leaves as they are.</summary>
    public int? KeepRecentTurns
    {
        get => Section.KeepRecentTurns;
        set => SetValue(Section.KeepRecentTurns, value, v => Section.KeepRecentTurns = v);
    }

    /// <summary>
    /// The context window of the default model, in tokens (<c>Llm:ContextWindowTokens</c>): what a
    /// compaction measures against. Empty, the engine takes the window it documents for the model.
    /// </summary>
    public int? ContextWindowTokens
    {
        get => Document.Llm.ContextWindowTokens;
        set => SetValue(Document.Llm.ContextWindowTokens, value, v => Document.Llm.ContextWindowTokens = v);
    }

    /// <summary>The watermark of <see cref="KeepRecentTurns"/>.</summary>
    public static string KeepRecentTurnsDefault => Invariant(CompactionSection.DefaultKeepRecentTurns);

    /// <summary>The model profile that writes the summary; empty, each agent's own model does.</summary>
    public string Profile
    {
        get => Section.Profile ?? string.Empty;
        set => SetValue(Profile, value?.Trim() ?? string.Empty, v => Section.Profile = v);
    }

    /// <summary>Whether tool results are pruned; set back to the engine's default, the key is removed.</summary>
    public bool PruneEnabled
    {
        get => Section.IsPruneEnabled;
        set => SetValue(PruneEnabled, value, v => Section.PruneEnabled = v == CompactionSection.DefaultPruneEnabled ? null : v);
    }

    /// <summary>The share of the window that starts a pruning, as typed.</summary>
    public string PruneFillShare
    {
        get => ShareText(Section.PruneFillShare);
        set => SetShare(PruneFillShare, value, v => Section.PruneFillShare = v);
    }

    /// <summary>The watermark of <see cref="PruneFillShare"/>.</summary>
    public static string PruneFillShareDefault => Invariant(CompactionSection.DefaultPruneFillShare);

    /// <summary>The latest messages a pruning never judges.</summary>
    public int? PruneKeepRecentMessages
    {
        get => Section.PruneKeepRecentMessages;
        set => SetValue(Section.PruneKeepRecentMessages, value, v => Section.PruneKeepRecentMessages = v);
    }

    /// <summary>The watermark of <see cref="PruneKeepRecentMessages"/>.</summary>
    public static string PruneKeepRecentMessagesDefault => Invariant(CompactionSection.DefaultPruneKeepRecentMessages);

    /// <summary>The characters kept at the head of a tool result that is cut.</summary>
    public int? PruneHeadChars
    {
        get => Section.PruneHeadChars;
        set => SetValue(Section.PruneHeadChars, value, v => Section.PruneHeadChars = v);
    }

    /// <summary>The watermark of <see cref="PruneHeadChars"/>.</summary>
    public static string PruneHeadCharsDefault => Invariant(CompactionSection.DefaultPruneHeadChars);

    /// <summary>
    /// What the engine's start refuses in the section, one line each; empty when it is accepted. A window
    /// that is not above zero is said here too: the start refuses it with the model's section.
    /// </summary>
    public string ProblemsText => string.Join("\n", Section.Problems().Select(problem => problem.Problem).Concat(WindowProblem()));

    /// <summary>
    /// The engine's refusal of a summary profile these settings do not define, as a warning: a run
    /// launched from Studio is given Studio's model profiles besides. Empty when there is nothing to say.
    /// </summary>
    public string ProfileWarning => Section.UnknownProfile()?.Problem ?? string.Empty;

    /// <summary>
    /// Reads the verdict again after an edit made elsewhere in the document: a profile added, renamed or
    /// removed changes what the summary's profile names.
    /// </summary>
    public void RefreshVerdict() => OnPropertiesChanged(nameof(ProblemsText), nameof(ProfileWarning));

    // The one sentence of Core, which the validator says too.
    private IEnumerable<string> WindowProblem() =>
        LlmSection.ContextWindowProblem(LlmSection.SectionPath, Document.Llm.ContextWindowTokens) is { } problem ? [problem] : [];

    /// <inheritdoc />
    protected override void OnSectionChanged() => OnPropertiesChanged(
        nameof(Exists), nameof(Enabled), nameof(TriggerShare), nameof(KeepRecentTurns), nameof(Profile), nameof(ContextWindowTokens),
        nameof(PruneEnabled), nameof(PruneFillShare), nameof(PruneKeepRecentMessages), nameof(PruneHeadChars),
        nameof(ProblemsText), nameof(ProfileWarning));

    // A share is typed with a point or a comma; an unreadable one is left as it was, the field showing
    // the value kept.
    private void SetShare(string current, string? typed, Action<double?> write, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        var text = (typed ?? string.Empty).Trim().Replace(',', '.');
        if (text.Length == 0)
        {
            SetValue(current, string.Empty, _ => write(null), propertyName);
            return;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var share))
            SetValue(current, Invariant(share), _ => write(share), propertyName);
        else
            OnPropertyChanged(propertyName);
    }

    private static string ShareText(double? share) => share is { } value ? Invariant(value) : string.Empty;

    private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
