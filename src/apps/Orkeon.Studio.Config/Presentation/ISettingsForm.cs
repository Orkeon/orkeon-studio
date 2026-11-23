using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Presentation;

/// <summary>
/// One editable section, as text. A form is the whole of what a screen knows: it moves
/// values between an <see cref="AppSettingsDocument"/> and the strings a terminal field
/// holds, and nothing else. Every rule it applies belongs to <c>Orkeon.Studio.Core</c>.
/// </summary>
internal interface ISettingsForm
{
    /// <summary>Section title, as shown in the navigation list.</summary>
    string Title { get; }

    /// <summary>Fills the fields from the document.</summary>
    void LoadFrom(AppSettingsDocument document);

    /// <summary>
    /// Writes the fields back into the document. <paramref name="document"/> is the one the form was
    /// last loaded from: a form may remember the fields as loaded and write only those that changed
    /// since (<see cref="BridgeForm"/>, <see cref="ContextForm"/>, <see cref="DecisionForm"/>), and
    /// after an apply that wrote it takes the document as written for its new starting point, so a
    /// second apply of the same fields writes nothing. Applied to another document, such a form
    /// would leave there the keys it believes unchanged: load it from that document first.
    /// </summary>
    /// <returns>
    /// Field-level errors (a number field holding letters, a value the engine's start refuses). An
    /// empty list means every changed value was written; a non-empty one means nothing was written
    /// for those fields, and the form still holds them as typed for a correction.
    /// </returns>
    IReadOnlyList<string> ApplyTo(AppSettingsDocument document);
}
