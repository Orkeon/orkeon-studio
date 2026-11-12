using Terminal.Gui.ViewBase;

namespace Orkeon.Studio.Run.Views;

/// <summary>
/// Accept handlers that keep an Accept to the view it was raised on. A copy of the helper
/// Orkeon.Cli.TerminalGui keeps internal since 1.0.0; declared in this namespace so it wins
/// over the Orkeon one wherever both are in scope.
/// </summary>
internal static class AcceptHandlers
{
    /// <summary>
    /// Runs <paramref name="action"/> when <paramref name="view"/> is accepted, and marks the Accept
    /// handled: an Accept left unhandled travels on to the <c>IsDefault</c> button of the same window,
    /// whose action would then run too (a Cancel that also confirms, a Validate that also runs).
    /// </summary>
    public static void OnAccept(this View view, Action action)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(action);
        view.Accepting += (_, e) =>
        {
            action();
            e.Handled = true;
        };
    }
}
