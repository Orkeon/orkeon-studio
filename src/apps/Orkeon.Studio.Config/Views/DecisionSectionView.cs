using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Core.Configuration;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The decision models (System One) screen: the provider from a closed list, the circuit's switch, and
/// for each provider its section, model and address, the profiles and the thinking levels System One
/// picks among, then what a run of the file gets. A provider name
/// the engine does not know is a row of its own, so applying the screen keeps it. Every line of text
/// above the status takes one row, cut rather than wrapped, so none runs into the field under it.
/// </summary>
internal sealed class DecisionSectionView : SectionView
{
    private const string EnabledCaption = "Use the decision models";

    private readonly DecisionForm _form;
    private readonly ListView _provider;
    private readonly CheckBox _enabled;
    private readonly List<(DecisionForm.ProviderFields Fields, CheckBox SetUp, TextField Model, TextField Address)> _providers = [];
    private readonly TextField _routeLadder;
    private readonly TextField _effortLevels;
    private readonly Label _status;

    /// <summary>Builds the screen over <paramref name="form"/>.</summary>
    public DecisionSectionView(DecisionForm form)
        : base("Decision models", scrolls: true)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));
        FormLayout.AddLine(this, 0, "Provider (" + DecisionSection.ProviderPath + ")");
        // One row more than the standard choices: a name the engine does not know shows there as written.
        // The list takes the whole width, under its caption: beside it, its longest row would be cut.
        var rows = DecisionForm.StandardChoices.Count + 1;
        _provider = new ListView { X = FormLayout.Margin, Y = 1, Width = Dim.Fill(FormLayout.Margin), Height = rows };
        Add(_provider);
        FormLayout.SetItems(_provider, _form.ProviderChoices, _form.ProviderChoiceIndex);
        var y = rows + 2;
        // Two rows: the caption of a switch the file writes as no boolean says so, and is longer.
        _enabled = FormLayout.AddOptionalSwitch(this, y, EnabledCaption, _form.Enabled);
        _enabled.Height = 2;
        FormLayout.TrackUnreadable(_enabled, EnabledCaption, () => _form.EnabledAsWritten, () => _form.EnabledAsWritten = null);
        y += 2;
        FormLayout.AddLine(this, y, DecisionForm.SectionsNotice);
        FormLayout.AddLine(this, y + 1, DecisionForm.JsonNotice);
        y += 2;
        foreach (var fields in _form.Providers)
        {
            var setUp = new CheckBox { X = FormLayout.Margin, Y = y, Height = 1, Text = fields.Descriptor.Section + " section" };
            Add(setUp);
            var model = FormLayout.AddField(this, y + 1, "  Model", fields.Model);
            var address = FormLayout.AddField(this, y + 2, "  API address", fields.BaseAddress);
            _providers.Add((fields, setUp, model, address));
            y += 3;
        }

        y++;
        _routeLadder = FormLayout.AddField(this, y, "Profiles to pick among", _form.RouteLadder);
        _effortLevels = FormLayout.AddField(this, y + 1, "Thinking levels", _form.EffortLevels);
        y += 2;
        foreach (var line in (string[])[DecisionForm.PicksNotice, DecisionForm.ListsNotice, DecisionForm.LevelsNotice, DecisionForm.DefaultLevelsNotice])
            FormLayout.AddLine(this, y++, line);

        y++;
        foreach (var line in DecisionForm.KeyLines)
            FormLayout.AddLine(this, y++, line);

        // Last, with nothing under it: the one text of the screen that may take several rows.
        _status = FormLayout.AddText(this, y + 1, _form.Status);
    }

    /// <inheritdoc />
    public override void Load()
    {
        FormLayout.SetItems(_provider, _form.ProviderChoices, _form.ProviderChoiceIndex);
        // The state first: setting it raises a change, which must not clear the text just loaded.
        var enabled = _form.EnabledAsWritten;
        _enabled.Value = FormLayout.ToCheckState(_form.Enabled);
        _form.EnabledAsWritten = enabled;
        _enabled.Text = FormLayout.SwitchCaption(EnabledCaption, enabled);
        foreach (var (fields, setUp, model, address) in _providers)
        {
            setUp.Value = FormLayout.ToCheckState(fields.SetUp);
            model.Text = fields.Model;
            address.Text = fields.BaseAddress;
        }

        _routeLadder.Text = _form.RouteLadder;
        _effortLevels.Text = _form.EffortLevels;
        _status.Text = _form.Status;
    }

    /// <summary>The status follows the document the form was just applied to.</summary>
    public override void Refresh() => _status.Text = _form.Status;

    /// <inheritdoc />
    public override void Apply()
    {
        _form.SelectProvider(FormLayout.SelectedIndex(_provider));
        _form.Enabled = FormLayout.ToBoolean(_enabled.Value);
        foreach (var (fields, setUp, model, address) in _providers)
        {
            fields.SetUp = FormLayout.ToBoolean(setUp.Value) == true;
            fields.Model = model.Text ?? "";
            fields.BaseAddress = address.Text ?? "";
        }

        _form.RouteLadder = _routeLadder.Text ?? "";
        _form.EffortLevels = _effortLevels.Text ?? "";
    }

    /// <summary>Test-only: the status line, what a run of the file gets.</summary>
    internal string StatusText => _status.Text;

    /// <summary>Test-only: how many rows the provider chooser holds, and which one is selected.</summary>
    internal (int Count, int Selected) ProviderRowsForTest => (_provider.Source?.Count ?? 0, FormLayout.SelectedIndex(_provider));

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _provider.Dispose();
            _enabled.Dispose();
            foreach (var (_, setUp, model, address) in _providers)
            {
                setUp.Dispose();
                model.Dispose();
                address.Dispose();
            }

            _routeLadder.Dispose();
            _effortLevels.Dispose();
            _status.Dispose();
        }

        base.Dispose(disposing);
    }
}
