using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Core.Configuration;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The context and skills screen: the compaction of a context, the pruning of its tool results by
/// System One and the skills' library and sizes. Every field is optional, and says the engine's default
/// an empty one stands for: in brackets beside its caption, or on a line of its own when the default is
/// no fixed value (the summary's profile, the model's window) or too long for a caption (the library).
/// Every line of text takes one row, cut rather than wrapped, so none runs into the field under it.
/// </summary>
internal sealed class ContextSectionView : SectionView
{
    private readonly ContextForm _form;
    private readonly List<(TextField Field, Func<string> Read, Action<string> Write)> _fields = [];

    /// <summary>Builds the screen over <paramref name="form"/>.</summary>
    public ContextSectionView(ContextForm form)
        : base("Context & skills", scrolls: true)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));

        FormLayout.AddLine(this, 0, ContextForm.Notice);
        FormLayout.AddLine(this, 1, ContextForm.ShareNotice);
        FormLayout.AddLine(this, 2, ContextForm.SwitchNotice);
        FormLayout.AddLine(this, 3, "Compaction (" + CompactionSection.SectionPath + ")");
        Add(4, Caption("  Compact a context", CompactionSection.DefaultEnabled), () => _form.CompactionEnabled, v => _form.CompactionEnabled = v);
        Add(5, Caption("  Start at share", CompactionSection.DefaultTriggerShare), () => _form.TriggerShare, v => _form.TriggerShare = v);
        Add(6, Caption("  Latest turns kept", CompactionSection.DefaultKeepRecentTurns), () => _form.KeepRecentTurns, v => _form.KeepRecentTurns = v);
        Add(7, "  Summary's model profile", () => _form.Profile, v => _form.Profile = v);
        Add(8, "  Model window (tokens)", () => _form.ContextWindowTokens, v => _form.ContextWindowTokens = v);
        FormLayout.AddLine(this, 9, ContextForm.OwnNotice);
        FormLayout.AddLine(this, 10, ContextForm.WindowNotice);
        FormLayout.AddLine(this, 12, "Pruning (" + CompactionSection.PrunePath + ")");
        Add(13, Caption("  Prune tool results", CompactionSection.DefaultPruneEnabled), () => _form.PruneEnabled, v => _form.PruneEnabled = v);
        Add(14, Caption("  Start at share", CompactionSection.DefaultPruneFillShare), () => _form.PruneFillShare, v => _form.PruneFillShare = v);
        Add(15, Caption("  Latest messages kept", CompactionSection.DefaultPruneKeepRecentMessages), () => _form.PruneKeepRecentMessages, v => _form.PruneKeepRecentMessages = v);
        Add(16, Caption("  Chars kept when cut", CompactionSection.DefaultPruneHeadChars), () => _form.PruneHeadChars, v => _form.PruneHeadChars = v);
        FormLayout.AddLine(this, 18, "Skills (" + SkillsSection.SectionPath + ")");
        FormLayout.AddLine(this, 19, ContextForm.LibraryNotice);
        Add(20, "  Library (virtual path)", () => _form.SkillsLibrary, v => _form.SkillsLibrary = v);
        Add(21, Caption("  Chars of one skill", SkillsSection.DefaultMaxSkillChars), () => _form.MaxSkillChars, v => _form.MaxSkillChars = v);
        Add(22, Caption("  Chars of all skills", SkillsSection.DefaultMaxBlockChars), () => _form.MaxBlockChars, v => _form.MaxBlockChars = v);
    }

    // The engine's default beside the caption, a switch's in the words a field takes.
    private static string Caption(string caption, object engineDefault) =>
        FormLayout.CaptionWithDefault(caption, engineDefault is bool flag ? (flag ? "true" : "false") : engineDefault);

    private void Add(int y, string caption, Func<string> read, Action<string> write) =>
        _fields.Add((FormLayout.AddField(this, y, caption, read()), read, write));

    /// <inheritdoc />
    public override void Load()
    {
        foreach (var (field, read, _) in _fields)
            field.Text = read();
    }

    /// <summary>
    /// The fields follow the document the form was just applied to: a value shows as it was stored, and
    /// a key another screen took away (the window of a model that changed) leaves its field. A field
    /// whose text already says it is left alone, the cursor with it.
    /// </summary>
    public override void Refresh()
    {
        foreach (var (field, read, _) in _fields)
        {
            if (!string.Equals(field.Text, read(), StringComparison.Ordinal))
                field.Text = read();
        }
    }

    /// <inheritdoc />
    public override void Apply()
    {
        foreach (var (field, _, write) in _fields)
            write(field.Text ?? "");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var (field, _, _) in _fields)
                field.Dispose();
        }

        base.Dispose(disposing);
    }
}
