using Orkeon.Studio.Config.Presentation;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The context and skills screen: the compaction of a context, the pruning of its tool results and the
/// skills' library and sizes, every field optional.
/// </summary>
internal sealed class ContextSectionView : SectionView
{
    private readonly ContextForm _form;
    private readonly List<(TextField Field, Func<string> Read, Action<string> Write)> _fields = [];

    /// <summary>Builds the screen over <paramref name="form"/>.</summary>
    public ContextSectionView(ContextForm form)
        : base("Context & skills")
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));

        FormLayout.AddNote(this, 0, ContextForm.Notice);
        FormLayout.AddNote(this, 1, ContextForm.SwitchNotice);
        FormLayout.AddNote(this, 2, "Compaction (Orkeon:Compaction)");
        Add(3, "  Compact a context", () => _form.CompactionEnabled, v => _form.CompactionEnabled = v);
        Add(4, "  Start at share of window", () => _form.TriggerShare, v => _form.TriggerShare = v);
        Add(5, "  Latest turns kept", () => _form.KeepRecentTurns, v => _form.KeepRecentTurns = v);
        Add(6, "  Summary's model profile", () => _form.Profile, v => _form.Profile = v);
        Add(7, "  Model window (tokens)", () => _form.ContextWindowTokens, v => _form.ContextWindowTokens = v);
        FormLayout.AddNote(this, 9, "Pruning of tool results by System One (Orkeon:Compaction:Prune)");
        Add(10, "  Prune tool results", () => _form.PruneEnabled, v => _form.PruneEnabled = v);
        Add(11, "  Start at share of window", () => _form.PruneFillShare, v => _form.PruneFillShare = v);
        Add(12, "  Latest messages kept", () => _form.PruneKeepRecentMessages, v => _form.PruneKeepRecentMessages = v);
        Add(13, "  Characters kept when cut", () => _form.PruneHeadChars, v => _form.PruneHeadChars = v);
        FormLayout.AddNote(this, 15, "Skills (Orkeon:Skills)");
        Add(16, "  Library (virtual path)", () => _form.SkillsLibrary, v => _form.SkillsLibrary = v);
        Add(17, "  Characters of one skill", () => _form.MaxSkillChars, v => _form.MaxSkillChars = v);
        Add(18, "  Characters of all skills", () => _form.MaxBlockChars, v => _form.MaxBlockChars = v);
    }

    private void Add(int y, string caption, Func<string> read, Action<string> write) =>
        _fields.Add((FormLayout.AddField(this, y, caption, read()), read, write));

    /// <inheritdoc />
    public override void Load()
    {
        foreach (var (field, read, _) in _fields)
            field.Text = read();
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
