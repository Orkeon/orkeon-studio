using Orkeon.Studio.Config.Presentation;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The <c>Orkeon:Tools:Browser</c> screen: the engine of the <c>browser</c> tool and its window. Every
/// line of text takes one row, so none runs into the row under it.
/// </summary>
internal sealed class BrowserSectionView : SectionView
{
    private const string HeadlessCaption = "Without a window (headless; not with safari)";

    private readonly BrowserForm _form;
    private readonly Label _legacy;
    private readonly ListView _engine;
    private readonly CheckBox _headless;

    /// <summary>Builds the screen over <paramref name="form"/>.</summary>
    public BrowserSectionView(BrowserForm form)
        : base("Browser", scrolls: true)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));

        FormLayout.AddLine(this, 0, BrowserForm.Notice);
        // A file opened on the command line is loaded before the window: the line shows it from the start.
        _legacy = FormLayout.AddChangingLine(this, 1, _form.UsesLegacySection ? BrowserForm.LegacyNotice : "");
        // One more row than the engines: the one the file writes when it is none of them.
        _engine = FormLayout.AddChoiceList(this, 3, 6, "Engine", _form.EngineChoices, _form.EngineChoiceIndex);
        _headless = FormLayout.AddOptionalSwitch(this, 10, HeadlessCaption, _form.Headless);
        FormLayout.TrackUnreadable(_headless, HeadlessCaption, () => _form.HeadlessAsWritten, () => _form.HeadlessAsWritten = null);
    }

    /// <inheritdoc />
    public override void Load()
    {
        // The state first: setting it raises a change, which must not clear the text just loaded.
        var headless = _form.HeadlessAsWritten;
        _headless.Value = FormLayout.ToCheckState(_form.Headless);
        _form.HeadlessAsWritten = headless;
        _headless.Text = FormLayout.SwitchCaption(HeadlessCaption, headless);
        FormLayout.SetItems(_engine, _form.EngineChoices, _form.EngineChoiceIndex);
        FormLayout.SetLine(_legacy, _form.UsesLegacySection ? BrowserForm.LegacyNotice : "");
    }

    /// <summary>After a save: the former section renamed, the line that said so goes.</summary>
    public override void Refresh() =>
        FormLayout.SetLine(_legacy, _form.UsesLegacySection ? BrowserForm.LegacyNotice : "");

    /// <inheritdoc />
    public override void Apply()
    {
        _form.SelectEngine(FormLayout.SelectedIndex(_engine));
        _form.Headless = FormLayout.ToBoolean(_headless.Value);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _legacy.Dispose();
            _engine.Dispose();
            _headless.Dispose();
        }

        base.Dispose(disposing);
    }
}
