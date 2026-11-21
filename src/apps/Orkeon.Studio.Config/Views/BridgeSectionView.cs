using Orkeon.Studio.Config.Presentation;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The tool bridge screen: the caller's side, the serving side, then what a run of the file bridges
/// and whether its token is in place.
/// </summary>
internal sealed class BridgeSectionView : SectionView
{
    private readonly BridgeForm _form;
    private readonly List<(TextField Field, Func<string> Read, Action<string> Write)> _fields = [];
    private readonly Label _status;

    /// <summary>Builds the screen over <paramref name="form"/>.</summary>
    public BridgeSectionView(BridgeForm form)
        : base("Tool bridge")
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));

        FormLayout.AddNote(this, 0, BridgeForm.Notice);
        FormLayout.AddNote(this, 1, BridgeForm.JsonNotice);
        FormLayout.AddNote(this, 2, "Tools that run on another machine (Orkeon:Tools:Bridge)");
        Add(3, "  Address of the bridge", () => _form.Address, v => _form.Address = v);
        Add(4, "  Tools to bridge", () => _form.Allow, v => _form.Allow = v);
        Add(5, "  Tools never bridged", () => _form.Deny, v => _form.Deny = v);
        Add(6, "  Token variable", () => _form.TokenEnvVar, v => _form.TokenEnvVar = v);
        Add(7, "  Host header", () => _form.HostHeader, v => _form.HostHeader = v);
        FormLayout.AddNote(this, 9, "Tools this machine runs for another: orkeon bridge serve (Orkeon:Bridge)");
        Add(10, "  Address it listens on", () => _form.ServePrefix, v => _form.ServePrefix = v);
        Add(11, "  Tools served", () => _form.ServeAllow, v => _form.ServeAllow = v);
        Add(12, "  Tools never served", () => _form.ServeDeny, v => _form.ServeDeny = v);
        Add(13, "  Token variable", () => _form.ServeTokenEnvVar, v => _form.ServeTokenEnvVar = v);
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

    /// <summary>The status follows the document the form was just applied to.</summary>
    public override void Refresh() => _status.Text = _form.Status;

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
