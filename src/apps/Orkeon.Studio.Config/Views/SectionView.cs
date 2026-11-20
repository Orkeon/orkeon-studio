using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// A screen of the editor. Every one of them does the same two things and nothing more:
/// show what its form holds, and hand the typed text back to it. The document, the rules
/// and the validation live in <c>Orkeon.Studio.Core</c>.
/// </summary>
internal abstract class SectionView : FrameView
{
    /// <summary>Creates a hidden, full-size section; the window shows one at a time.</summary>
    protected SectionView(string title)
    {
        Title = title;
        Width = Dim.Fill();
        Height = Dim.Fill();
        Visible = false;
    }

    /// <summary>Form to widgets.</summary>
    public abstract void Load();

    /// <summary>Widgets to form.</summary>
    public abstract void Apply();

    /// <summary>
    /// After the forms were written into the document: shows again what a screen derives from it (a
    /// status line), leaving the fields as typed. Nothing by default.
    /// </summary>
    public virtual void Refresh()
    {
    }
}
