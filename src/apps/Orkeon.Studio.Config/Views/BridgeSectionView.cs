using Orkeon.Studio.Config.Presentation;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The tool bridge screen: three lines on how its fields are written, the caller's side, the serving
/// side, then what a run of the file bridges and whether its token is in place. Every line above the
/// status takes one row, cut rather than wrapped, so no text runs into the field under it.
/// </summary>
internal sealed class BridgeSectionView : SectionView
{
    private readonly BridgeForm _form;
    private readonly List<(TextField Field, Func<string> Read, Action<string> Write)> _fields = [];
    private readonly Label _status;

    /// <summary>Builds the screen over <paramref name="form"/>.</summary>
    public BridgeSectionView(BridgeForm form)
        : base("Tool bridge", scrolls: true)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));

        FormLayout.AddLine(this, 0, BridgeForm.Notice);
        FormLayout.AddLine(this, 1, BridgeForm.TokenNotice);
        FormLayout.AddLine(this, 2, BridgeForm.JsonNotice);
        FormLayout.AddLine(this, 3, BridgeForm.CallerHeading);
        Add(4, "  Address of the bridge", () => _form.Address, v => _form.Address = v);
        Add(5, "  Tools to bridge", () => _form.Allow, v => _form.Allow = v);
        Add(6, "  Tools never bridged", () => _form.Deny, v => _form.Deny = v);
        Add(7, "  Token variable", () => _form.TokenEnvVar, v => _form.TokenEnvVar = v);
        Add(8, "  Host header", () => _form.HostHeader, v => _form.HostHeader = v);
        FormLayout.AddLine(this, 9, BridgeForm.ServerHeading);
        Add(10, "  Address it listens on", () => _form.ServePrefix, v => _form.ServePrefix = v);
        Add(11, "  Tools served", () => _form.ServeAllow, v => _form.ServeAllow = v);
        Add(12, "  Tools never served", () => _form.ServeDeny, v => _form.ServeDeny = v);
        Add(13, "  Token variable", () => _form.ServeTokenEnvVar, v => _form.ServeTokenEnvVar = v);
        // Last, with nothing under it: the one text of the screen that may take several rows. Sixteen
        // rows in all while it takes one: what a terminal of 24 rows shows without scrolling.
        _status = FormLayout.AddText(this, 15, _form.Status);
    }

    private void Add(int y, string caption, Func<string> read, Action<string> write) =>
        _fields.Add((FormLayout.AddField(this, y, caption, read()), read, write));

    /// <inheritdoc />
    public override void Load()
    {
        foreach (var (field, read, _) in _fields)
            field.Text = read();
        _status.Text = _form.Status;
    }

    /// <summary>
    /// The fields and the status follow the document the form was just applied to: a list shows as it
    /// was stored (<c>a;b</c> as <c>a, b</c>), a value trimmed as kept. A field whose text already says it
    /// is left alone, the cursor with it.
    /// </summary>
    public override void Refresh()
    {
        foreach (var (field, read, _) in _fields)
        {
            if (!string.Equals(field.Text, read(), StringComparison.Ordinal))
                field.Text = read();
        }

        _status.Text = _form.Status;
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
            _status.Dispose();
        }

        base.Dispose(disposing);
    }
}
