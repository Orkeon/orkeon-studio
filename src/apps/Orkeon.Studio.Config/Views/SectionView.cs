using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// One section screen of the editor: it shows what its form holds and hands the typed text back to it.
/// A screen is a stop of the window's Tab order like any field, so Tab walks into it, through its
/// rows and out to the window's buttons. A screen that is a list of rows (<c>scrolls</c>) is as tall
/// as its rows, not as the terminal: when they do not fit, the frame scrolls (a vertical scroll bar
/// shows), follows the focus and answers the mouse wheel. A screen laid out against the frame's own
/// height (a list that fills it, buttons anchored to its end) does not scroll: its content is the frame.
/// </summary>
internal abstract class SectionView : FrameView
{
    private readonly bool _scrolls;
    private Terminal.Gui.App.IMouse? _mouse;

    /// <summary>Creates a hidden, full-size section; the window shows one at a time.</summary>
    /// <param name="title">The frame's title, and the section's line in the navigation list.</param>
    /// <param name="scrolls">
    /// True for a screen whose rows are all placed at a fixed row: its content is then as tall as its
    /// rows. Never for a screen with a child that fills the frame or is anchored to its end: such a
    /// child is placed against the content's height, which would then follow from it.
    /// </param>
    protected SectionView(string title, bool scrolls = false)
    {
        Title = title;
        Width = Dim.Fill();
        Height = Dim.Fill();
        Visible = false;
        // A stop, not a group of its own: Tab leaves the screen after its last row instead of cycling in it.
        TabStop = TabBehavior.TabStop;
        // Left for another view of the window, the screen forgets which field it was on: Tab then comes
        // back in at its first field and Shift+Tab at its last, instead of the one last used. A dialog
        // is another window: on its way back the focus returns where it was.
        HasFocusChanged += (_, args) =>
        {
            if (!HasFocus && args.NewFocused is { } next && ReferenceEquals(WindowOf(next), WindowOf(this)))
                ClearFocus();
        };
        _scrolls = scrolls;
        if (!scrolls)
            return;

        ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        SubViewsLaidOut += (_, _) => FitContent();
        // The wheel is heard from the application, once the screen runs under one: a field under the
        // pointer keeps every mouse event for itself, so nothing bound on the screen would ever see it.
        Initialized += (_, _) => ListenToWheel();
    }

    /// <summary>Pushes the form's state into the widgets.</summary>
    public abstract void Load();

    /// <summary>Reads the widgets back into the form.</summary>
    public abstract void Apply();

    /// <summary>
    /// After the forms were written into the document: shows again what a screen derives from it (a
    /// status line), leaving the fields as typed. Nothing by default.
    /// </summary>
    public virtual void Refresh()
    {
    }

    /// <summary>
    /// The row under the last widget placed at a fixed row: how tall the content of a screen that
    /// scrolls is. A widget placed against the content's own height (one that fills it, or is anchored
    /// to its end) is never counted: its place follows from the content's height, not the reverse.
    /// </summary>
    internal int ContentRows => SubViews
        .Where(view => view.Y is PosAbsolute && !view.Height.Has<DimFill>(out _))
        .Select(view => view.Frame.Bottom)
        .DefaultIfEmpty(0)
        .Max();

    /// <summary>
    /// Scrolls so that <paramref name="view"/> is in sight: up to its first row when it sits above the
    /// part shown, down to its last when it sits below. Nothing on a screen that does not scroll.
    /// </summary>
    internal void BringIntoView(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!_scrolls)
            return;

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
        if (!_scrolls)
            return;

        view.HasFocusChanged += (_, _) =>
        {
            if (view.HasFocus)
                BringIntoView(view);
        };
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _mouse is not null)
        {
            _mouse.MouseEvent -= OnApplicationMouse;
            _mouse = null;
        }

        base.Dispose(disposing);
    }

    private void ListenToWheel()
    {
        if (_mouse is not null || App?.Mouse is not { } mouse)
            return;

        _mouse = mouse;
        mouse.MouseEvent += OnApplicationMouse;
    }

    // One notch of the wheel over the screen shown, a row of text or a field alike, scrolls it by one
    // row, when it has rows out of sight. Over a list of the screen the wheel is the list's, for its own
    // rows; under a dialog, the pointer is over the dialog, not over the screen.
    private void OnApplicationMouse(object? sender, Mouse mouse)
    {
        var rows = mouse.Flags.HasFlag(MouseFlags.WheeledDown) ? 1 : mouse.Flags.HasFlag(MouseFlags.WheeledUp) ? -1 : 0;
        if (rows == 0 || !Visible || !IsOverARow(mouse.View))
            return;

        if (ScrollVertical(rows) == true)
            mouse.Handled = true;
    }

    private bool IsOverARow(View? under)
    {
        for (var view = under; view is not null; view = view.SuperView)
        {
            if (ReferenceEquals(view, this))
                return true;
            if (view is ListView)
                return false;
        }

        return false;
    }

    private static View WindowOf(View view)
    {
        while (view.SuperView is { } parent)
            view = parent;
        return view;
    }

    // The content is as wide as the frame shows and as tall as the rows: taller than the frame, it scrolls.
    private void FitContent()
    {
        var size = new Size(Viewport.Width, Math.Max(ContentRows, Viewport.Height));
        if (GetContentSize() != size)
            SetContentSize(size);
    }
}
