using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Tests.Views;

/// <summary>
/// The section screens as a terminal shows them, under a running application: a screen laid out against
/// its frame keeps that layout whatever the passes and the resizes, a screen of rows scrolls when they
/// do not fit and only then, Tab walks from the section list through the fields to the buttons, the
/// part shown following the focus, the wheel scrolls, and no text runs into the row under it.
/// </summary>
[Collection(TerminalGuiTests.Name)]
public sealed class SectionScreenLayoutTests
{
    private const int Llm = 0;
    private const int RateLimiting = 1;
    private const int Rag = 2;
    private const int Decision = 3;
    private const int Context = 4;
    private const int Bridge = 5;
    private const int Mounts = 6;
    private const int Logging = 7;
    private const int LlmLogging = 8;
    private const int SectionCount = 10;

    // Where each widget sits. A text that wraps keeps the height of the pass before a resize (Terminal.Gui
    // sizes it against the width it had), so its height says nothing of the screen's layout: left out.
    private static List<Rectangle> Frames(View screen) =>
        [.. screen.SubViews.Select(view => view is Label ? view.Frame with { Height = 1 } : view.Frame)];

    /// <summary>
    /// The mounts and the logging screens fill their frame (a list that takes what is left, buttons and a
    /// status anchored to the end): their content is the frame, at every pass and after every resize.
    /// </summary>
    [Theory]
    [InlineData(Mounts)]
    [InlineData(Logging)]
    public void A_screen_laid_out_against_its_frame_keeps_its_layout_through_passes_and_resizes(int section)
    {
        using var editor = new HeadlessEditor(80, 24, section);
        var screen = editor.Section;
        var first = Frames(screen);
        Assert.All(screen.SubViews.OfType<Button>(), button => Assert.True(
            button.Frame.Bottom <= screen.Viewport.Height, $"button at {button.Frame} in a frame of {screen.Viewport.Height} rows"));

        foreach (var (columns, rows) in (ReadOnlySpan<(int, int)>)[(81, 24), (82, 24), (80, 24), (80, 25), (80, 24), (140, 45), (80, 24)])
        {
            editor.Resize(columns, rows);
            editor.Layout();
            editor.Layout();
            Assert.Equal(screen.Viewport.Size, screen.GetContentSize());
            Assert.False(screen.VerticalScrollBar.Visible, $"a scroll bar at {columns}x{rows}");
        }

        Assert.Equal(first, Frames(screen));
        Assert.Equal(0, screen.Viewport.Y);
    }

    /// <summary>Every screen, in a terminal where its rows fit, shows no scroll bar and is as tall as its frame.</summary>
    [Fact]
    public void A_screen_whose_rows_fit_is_as_tall_as_its_frame()
    {
        for (var section = 0; section < SectionCount; section++)
        {
            using var editor = new HeadlessEditor(200, 60, section);
            var screen = editor.Section;
            editor.Layout();

            Assert.Equal(screen.Viewport.Size, screen.GetContentSize());
            Assert.False(screen.VerticalScrollBar.Visible, $"{screen.Title} shows a scroll bar at 200x60");

            editor.Wheel(screen, down: true);
            Assert.Equal(0, screen.Viewport.Y);
        }
    }

    /// <summary>A screen of rows that do not fit comes back to its frame's height once the terminal grows.</summary>
    [Fact]
    public void A_screen_that_scrolled_stops_when_the_terminal_grows()
    {
        using var editor = new HeadlessEditor(80, 24, Context);
        var screen = editor.Section;
        Assert.True(screen.GetContentSize().Height > screen.Viewport.Height);
        Assert.True(screen.VerticalScrollBar.Visible);

        editor.Resize(140, 45);
        editor.Layout();

        Assert.Equal(screen.Viewport.Size, screen.GetContentSize());
        Assert.False(screen.VerticalScrollBar.Visible);

        editor.Resize(80, 24);
        editor.Layout();
        Assert.Equal(screen.ContentRows, screen.GetContentSize().Height);
    }

    /// <summary>
    /// Tab walks the window: from the section list into the screen, through each of its fields (the part
    /// shown following the one in use, below the fold included), out to the window's buttons, and back
    /// to the section list.
    /// </summary>
    [Fact]
    public void Tab_walks_from_the_section_list_through_the_fields_to_the_buttons_and_back()
    {
        using var editor = new HeadlessEditor(80, 24, Context);
        var screen = editor.Section;
        var fields = screen.SubViews.OfType<TextField>().ToList();
        Assert.True(fields[^1].Frame.Bottom > screen.Viewport.Height, "the last field is not below the fold: the test proves nothing");
        Assert.IsType<ListView>(editor.Focused);
        Assert.NotSame(screen, editor.Focused!.SuperView);

        var visited = new List<View>();
        for (var press = 0; press < fields.Count + 12 && !(visited.Count > 0 && editor.Focused is ListView); press++)
        {
            editor.Press(Key.Tab);
            var focused = editor.Focused!;
            visited.Add(focused);
            if (ReferenceEquals(focused.SuperView, screen))
            {
                Assert.True(
                    focused.Frame.Y >= screen.Viewport.Y && focused.Frame.Bottom <= screen.Viewport.Y + screen.Viewport.Height,
                    $"the field at row {focused.Frame.Y} has the focus out of sight (rows {screen.Viewport.Y} to {screen.Viewport.Y + screen.Viewport.Height})");
            }
        }

        // Every field in its order, then the six buttons of the window, then the section list again.
        Assert.Equal(fields, visited.Take(fields.Count));
        Assert.True(screen.Viewport.Y > 0, "the part shown did not follow the focus below the fold");
        Assert.Equal(
            ["Open…", "Save…", "Validate", "Preset…", "Diagnostic", "Quit"],
            visited.Skip(fields.Count).OfType<Button>().Select(button => button.Text));
        Assert.IsType<ListView>(visited[^1]);
        Assert.Equal(fields.Count + 7, visited.Count);

        // A second lap starts at the first field again, and the part shown comes back up to it.
        editor.Press(Key.Tab);
        Assert.Same(fields[0], editor.Focused);
        Assert.True(fields[0].Frame.Y >= screen.Viewport.Y);

        // And backwards from the first field: the section list.
        editor.Press(Key.Tab.WithShift);
        Assert.IsType<ListView>(editor.Focused);
    }

    /// <summary>The wheel scrolls a screen whose rows do not fit, over a row of text as over a field, and no further than its ends.</summary>
    [Fact]
    public void The_wheel_scrolls_a_screen_whose_rows_do_not_fit()
    {
        using var editor = new HeadlessEditor(80, 24, Context);
        var screen = editor.Section;
        var last = screen.GetContentSize().Height - screen.Viewport.Height;
        Assert.True(last > 0);

        editor.Wheel(screen, down: true, column: 5, row: 1);
        Assert.Equal(1, screen.Viewport.Y);
        editor.Wheel(screen, down: true, column: 40, row: 6);
        Assert.Equal(2, screen.Viewport.Y);
        for (var notch = 0; notch < last + 5; notch++)
            editor.Wheel(screen, down: true);
        Assert.Equal(last, screen.Viewport.Y);

        for (var notch = 0; notch < last + 5; notch++)
            editor.Wheel(screen, down: false);
        Assert.Equal(0, screen.Viewport.Y);
    }

    /// <summary>The mounts screen does not scroll: the wheel over its notice moves nothing.</summary>
    [Fact]
    public void The_wheel_moves_nothing_on_a_screen_laid_out_against_its_frame()
    {
        using var editor = new HeadlessEditor(80, 24, Mounts);
        var screen = editor.Section;
        var first = Frames(screen);

        editor.Wheel(screen, down: true, column: 5, row: 1);

        Assert.Equal(0, screen.Viewport.Y);
        Assert.Equal(first, Frames(screen));
    }

    /// <summary>
    /// On every screen that is a form (the LLM, the rate limiting, the RAG, the decision models, the
    /// context, the bridge, the LLM logging), in a terminal of 80 columns as in a wider one: no widget
    /// lies over another (a note that wraps, or a caption wider than its column, would lie over a
    /// field), and no line of one row is cut.
    /// </summary>
    [Theory]
    [InlineData(Llm, 80, 24)]
    [InlineData(RateLimiting, 80, 24)]
    [InlineData(Rag, 80, 24)]
    [InlineData(LlmLogging, 80, 24)]
    [InlineData(Llm, 100, 30)]
    [InlineData(RateLimiting, 100, 30)]
    [InlineData(Rag, 100, 30)]
    [InlineData(LlmLogging, 100, 30)]
    [InlineData(Decision, 80, 24)]
    [InlineData(Context, 80, 24)]
    [InlineData(Bridge, 80, 24)]
    [InlineData(Decision, 100, 30)]
    [InlineData(Context, 100, 30)]
    [InlineData(Bridge, 100, 30)]
    public void No_text_runs_into_another_row_and_no_line_is_cut(int section, int columns, int rows)
    {
        using var editor = new HeadlessEditor(columns, rows, section);
        var screen = editor.Section;
        var widgets = screen.SubViews.Where(view => view.Visible).ToList();

        for (var i = 0; i < widgets.Count; i++)
        {
            for (var j = i + 1; j < widgets.Count; j++)
            {
                Assert.False(
                    widgets[i].Frame.IntersectsWith(widgets[j].Frame),
                    $"{screen.Title} at {columns}x{rows}: '{widgets[i].Text}' {widgets[i].Frame} lies over '{widgets[j].Text}' {widgets[j].Frame}");
            }
        }

        // The status, last, may take several rows (a screen without one ends on a caption, which fits
        // all the same); every other line of text has one, and fits in it.
        var lines = screen.SubViews.OfType<Label>().ToList();
        Assert.All(lines.Take(lines.Count - 1), line => Assert.True(
            line.Frame.Height == 1 && line.Text.Length <= line.Frame.Width,
            $"{screen.Title} at {columns}x{rows}: '{line.Text}' ({line.Text.Length} cells) in {line.Frame}"));
    }

    /// <summary>
    /// The bridge screen fits a terminal of 80 by 24 without scrolling, and a variable's name shows
    /// whole, its underscores included: a line of text has no hot key.
    /// </summary>
    [Fact]
    public void The_bridge_screen_fits_80_by_24_and_shows_a_variable_name_whole()
    {
        using var editor = new HeadlessEditor(80, 24, Bridge);

        Assert.False(editor.Section.VerticalScrollBar.Visible);
        Assert.Contains(Orkeon.Studio.Config.Presentation.BridgeForm.TokenNotice, editor.Screen(), StringComparison.Ordinal);
    }

    /// <summary>Scrolled to its end, the decision screen names each provider's key variable, whole.</summary>
    [Fact]
    public void The_decision_screen_scrolled_to_its_end_names_the_key_variables()
    {
        using var editor = new HeadlessEditor(80, 24, Decision);
        var screen = editor.Section;

        for (var notch = 0; notch < screen.GetContentSize().Height; notch++)
            editor.Wheel(screen, down: true, column: 5, row: 14);

        var shown = editor.Screen();
        Assert.Contains("TYPESAFE_API_KEY", shown, StringComparison.Ordinal);
        Assert.Contains("KEV_API_KEY (optional)", shown, StringComparison.Ordinal);
    }
}
