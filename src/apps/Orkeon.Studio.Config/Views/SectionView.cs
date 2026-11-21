using System.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// One section screen of the editor. A screen is as tall as its rows, not as the terminal: when the
/// rows do not fit, the frame scrolls (a vertical scroll bar shows) and follows the focus, so a field
/// below the fold is reached with Tab like any other.
/// </summary>
internal abstract class SectionView : FrameView
{
    protected SectionView(string title)
    {
        Title = title;
        Width = Dim.Fill();
        Height = Dim.Fill();
        Visible = false;
        ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        SubViewsLaidOut += (_, _) => FitContent();
    }

    /// <summary>Pushes the form's state into the widgets.</summary>
    public abstract void Load();

    /// <summary>Reads the widgets back into the form.</summary>
    public abstract void Apply();

    /// <summary>Refreshes what the screen derives from the document, after an apply.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>The row under the screen's last widget: how tall its content is.</summary>
    internal int ContentRows => SubViews.Count == 0 ? 0 : SubViews.Max(view => view.Frame.Bottom);

    /// <summary>
    /// Scrolls so that <paramref name="view"/> is in sight: up to its first row when it sits above the
    /// part shown, down to its last when it sits below.
    /// </summary>
    internal void BringIntoView(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var shown = Viewport;
        if (view.Frame.Y < shown.Y)
            Viewport = shown with { Y = view.Frame.Y };
        else if (view.Frame.Bottom > shown.Y + shown.Height)
            Viewport = shown with { Y = view.Frame.Bottom - shown.Height };
    }

    /// <inheritdoc />
    protected override void OnSubViewAdded(View view)
    {
        base.OnSubViewAdded(view);
        view.HasFocusChanged += (_, _) =>
        {
            if (view.HasFocus)
                BringIntoView(view);
        };
    }

    // The content is as wide as the frame shows and as tall as the rows: taller than the frame, it scrolls.
    private void FitContent()
    {
        var size = new Size(Viewport.Width, Math.Max(ContentRows, Viewport.Height));
        if (GetContentSize() != size)
            SetContentSize(size);
    }
}
