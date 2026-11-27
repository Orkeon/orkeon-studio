using Orkeon.Studio.Config.Presentation;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The <c>Orkeon:Tools:Browser</c> screen: the engine of the <c>browser</c> tool, its window, the profile
/// it starts on, whether it stays open between tasks and whether agents at once share it. Every line of
/// text takes one row, so none runs into the row under it.
/// </summary>
internal sealed class BrowserSectionView : SectionView
{
    private const string HeadlessCaption = "Without a window (headless; not with safari)";
    private const string KeepOpenCaption = "Keep open between tasks (Lifetime: Host)";
    private const string SharedCaption = "Agents at once share it (SharedInstance)";

    private readonly BrowserForm _form;
    private readonly Label _legacy;
    private readonly ListView _engine;
    private readonly CheckBox _headless;
    private readonly TextField _profile;
    private readonly CheckBox _keepOpen;
    private readonly Label _unknownLifetime;
    private readonly CheckBox _shared;

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
        _profile = FormLayout.AddField(this, 12, "Profile (blank: disposable)", _form.Profile);
        _keepOpen = FormLayout.AddOptionalSwitch(this, 14, KeepOpenCaption, _form.KeepsBrowserOpen);
        _unknownLifetime = FormLayout.AddChangingLine(this, 15, UnknownLifetimeLine);
        // The first change of state replaces a lifetime the run refuses, as a switch's unreadable text.
        _keepOpen.ValueChanged += (_, _) =>
        {
            _form.LifetimeAsWritten = null;
            FormLayout.SetLine(_unknownLifetime, "");
        };
        _shared = FormLayout.AddOptionalSwitch(this, 17, SharedCaption, _form.SharedInstance);
        FormLayout.TrackUnreadable(_shared, SharedCaption, () => _form.SharedInstanceAsWritten, () => _form.SharedInstanceAsWritten = null);
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
        _profile.Text = _form.Profile;
        var lifetime = _form.LifetimeAsWritten;
        _keepOpen.Value = FormLayout.ToCheckState(_form.KeepsBrowserOpen);
        _form.LifetimeAsWritten = lifetime;
        FormLayout.SetLine(_unknownLifetime, UnknownLifetimeLine);
        var shared = _form.SharedInstanceAsWritten;
        _shared.Value = FormLayout.ToCheckState(_form.SharedInstance);
        _form.SharedInstanceAsWritten = shared;
        _shared.Text = FormLayout.SwitchCaption(SharedCaption, shared);
    }

    private string UnknownLifetimeLine => _form.LifetimeAsWritten is null ? "" : BrowserForm.UnknownLifetimeNotice;

    /// <summary>After a save: the former section renamed, the line that said so goes, the profile shows as kept.</summary>
    public override void Refresh()
    {
        FormLayout.SetLine(_legacy, _form.UsesLegacySection ? BrowserForm.LegacyNotice : "");
        FormLayout.SetLine(_unknownLifetime, UnknownLifetimeLine);
        if (!string.Equals(_profile.Text, _form.Profile, StringComparison.Ordinal))
            _profile.Text = _form.Profile;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _form.SelectEngine(FormLayout.SelectedIndex(_engine));
        _form.Headless = FormLayout.ToBoolean(_headless.Value);
        _form.Profile = _profile.Text ?? "";
        _form.KeepsBrowserOpen = FormLayout.ToBoolean(_keepOpen.Value);
        _form.SharedInstance = FormLayout.ToBoolean(_shared.Value);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _legacy.Dispose();
            _engine.Dispose();
            _headless.Dispose();
            _profile.Dispose();
            _keepOpen.Dispose();
            _unknownLifetime.Dispose();
            _shared.Dispose();
        }

        base.Dispose(disposing);
    }
}
