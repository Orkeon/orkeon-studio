using System.Drawing;
using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Config.Tests.Doubles;
using Orkeon.Studio.Config.Views;
using Orkeon.Studio.Core.Process;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
// The bare name binds to the enclosing Orkeon.Application namespace here.
using TerminalApp = Terminal.Gui.App.Application;

namespace Orkeon.Studio.Config.Tests.Views;

// The static application is the one the editor runs on (see Orkeon.Studio.Config.csproj): the screens
// read it, and Terminal.Gui refuses its instance model beside it in one process.
#pragma warning disable CS0618

/// <summary>
/// The editor window running under the application, on Terminal.Gui's ANSI driver with no terminal
/// behind it: the screen has a size, the views are laid out as a user sees them, and keys and the mouse
/// wheel are injected as the driver would raise them. The application is the process's own, as the
/// editor starts it (<c>Application.Init</c>): Terminal.Gui allows one model per process, and the
/// screens read that one. So a test that opens an editor, and every test that builds a view, belongs to
/// <see cref="TerminalGuiTests"/>, whose tests run one after the other.
/// </summary>
internal sealed class HeadlessEditor : IDisposable
{
    private readonly SessionToken? _session;
    private bool _disposed;

    /// <summary>Opens the editor on a screen of <paramref name="columns"/> by <paramref name="rows"/>, showing <paramref name="section"/>.</summary>
    public HeadlessEditor(int columns, int rows, int section = 0, ConfigEditorModel? model = null)
    {
        TerminalApp.Init("ansi");
        TerminalApp.Driver!.SetScreenSize(columns, rows);
        Window = new StudioConfigWindow(model ?? NewModel());
        Window.SelectSection(section);
        _session = TerminalApp.Begin(Window);
        Layout();
    }

    /// <summary>An editor session on an empty document, with no CLI and no network behind it.</summary>
    public static ConfigEditorModel NewModel() => new(
        new FakeDirectoryProbe("/data/in"),
        new OrkeonProcessRunner(new FakeProcessLauncher(), new OrkeonBinaryLocator(new FakeExecutableProbe(), ["orkeon"])),
        new FakeLlmEndpointProbe());

    /// <summary>The window under test.</summary>
    public StudioConfigWindow Window { get; }

    /// <summary>The screen shown.</summary>
    public SectionView Section => Window.Sections[Window.CurrentSection];

    /// <summary>The view that holds the focus, at the deepest.</summary>
    public View? Focused => Window.MostFocused;

    /// <summary>One pass of layout and drawing, as the main loop runs after an event.</summary>
    public void Layout()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TerminalApp.LayoutAndDraw(false);
    }

    /// <summary>The terminal takes another size.</summary>
    public void Resize(int columns, int rows)
    {
        TerminalApp.Driver!.SetScreenSize(columns, rows);
        Layout();
    }

    /// <summary>A key pressed, then the pass of layout that follows it.</summary>
    public void Press(Key key)
    {
        TerminalApp.RaiseKeyDownEvent(key);
        Layout();
    }

    /// <summary>The wheel turned one notch over <paramref name="view"/>, <paramref name="column"/> and <paramref name="row"/> counted in its frame.</summary>
    public void Wheel(View view, bool down, int column = 5, int row = 3)
    {
        var frame = view.FrameToScreen();
        TerminalApp.Mouse.RaiseMouseEvent(new Mouse
        {
            Flags = down ? MouseFlags.WheeledDown : MouseFlags.WheeledUp,
            ScreenPosition = new Point(frame.X + column, frame.Y + row),
        });
        Layout();
    }

    /// <summary>What the terminal shows, row by row.</summary>
    public string Screen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return TerminalApp.Driver!.ToString() ?? "";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_session is not null)
            TerminalApp.End(_session);
        Window.Dispose();
        TerminalApp.Shutdown();
    }
}

/// <summary>
/// The tests that build Terminal.Gui views or run the application: one collection, so they run one
/// after the other. The application is one per process, and a screen built while a test runs it would
/// see it initialised.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TerminalGuiTests
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Terminal.Gui";
}
