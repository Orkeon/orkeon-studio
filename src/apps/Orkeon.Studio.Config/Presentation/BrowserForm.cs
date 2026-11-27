using System.Globalization;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Presentation;

/// <summary>
/// The <c>Orkeon:Tools:Browser</c> section (Orkeon #440): the engine the <c>browser</c> tool drives and
/// whether it shows a window. The default engine and a visible window are written as no key; a value
/// written moves a lone former <c>Orkeon:Tools:Firefox</c> section under the new name first
/// (<see cref="BrowserToolsSection"/>), so the file never holds the two the run refuses together.
/// </summary>
internal sealed class BrowserForm : ISettingsForm
{
    /// <inheritdoc />
    public string Title => "Browser";

    /// <summary>Shown above the fields: what the section is for.</summary>
    public const string Notice = "The browser the browser tool drives.";

    /// <summary>Shown when the file still names the section by its former name.</summary>
    public const string LegacyNotice = "In Orkeon:Tools:Firefox: a change renames it.";

    // The path of Headless as the file holds it: under the former section while it is the one read.
    private string _headlessPath = BrowserToolsSection.SectionPath + ":Headless";

    /// <summary>
    /// The engines the chooser shows: the five the runtime knows, then the one the file writes when it
    /// is none of them, kept as written for the validator to judge.
    /// </summary>
    public IReadOnlyList<string> EngineChoices { get; private set; } = BrowserToolsSection.Engines;

    /// <summary>The engine chosen, in the runtime's spelling.</summary>
    public string Engine { get; set; } = BrowserToolsSection.DefaultEngine;

    /// <summary>Whether the browser starts without a window; null when the key is absent.</summary>
    public bool? Headless { get; set; }

    /// <summary>The text <c>Headless</c> holds when it reads as no boolean, else null (see <see cref="FieldText.UnreadableSwitch"/>).</summary>
    public string? HeadlessAsWritten { get; set; }

    /// <summary>Whether the file names the section by its former name alone.</summary>
    public bool UsesLegacySection { get; private set; }

    /// <summary>Index of <see cref="Engine"/> in <see cref="EngineChoices"/>.</summary>
    public int EngineChoiceIndex =>
        Math.Max(0, EngineChoices.ToList().FindIndex(e => string.Equals(e, Engine, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Selects an engine by its index in <see cref="EngineChoices"/>.</summary>
    public void SelectEngine(int index) =>
        Engine = index >= 0 && index < EngineChoices.Count ? EngineChoices[index] : BrowserToolsSection.DefaultEngine;

    /// <inheritdoc />
    public void LoadFrom(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var section = document.BrowserTools;
        var written = section.EffectiveEngine;
        var known = BrowserToolsSection.Engines.FirstOrDefault(e => string.Equals(e, written, StringComparison.OrdinalIgnoreCase));
        EngineChoices = known is null ? [.. BrowserToolsSection.Engines, written] : BrowserToolsSection.Engines;
        Engine = known ?? written;
        Headless = section.Headless;
        _headlessPath = section.HeadlessPath;
        HeadlessAsWritten = FieldText.UnreadableSwitch(document, _headlessPath);
        UsesLegacySection = section.LegacyExists && !section.Exists;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ApplyTo(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<string>();
        FieldText.RefuseUnreadableSwitch(HeadlessAsWritten, _headlessPath, errors);
        if (string.Equals(Engine, BrowserToolsSection.SafariEngine, StringComparison.OrdinalIgnoreCase) && Headless is true)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"{_headlessPath}: Safari has no headless mode, and the run refuses it. Turn it off or pick another engine."));
        }

        if (errors.Count > 0)
            return errors;

        var section = document.BrowserTools;
        // The runtime reads the engine without regard to case: "Chrome" as written stays as written.
        if (!string.Equals(section.EffectiveEngine, Engine, StringComparison.OrdinalIgnoreCase))
            section.Engine = Engine;
        if (section.Headless != Headless)
            section.Headless = Headless;
        UsesLegacySection = section.LegacyExists && !section.Exists;

        return [];
    }
}
