using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.Tests.Doubles;

/// <summary>
/// The <see cref="IUiDispatcher"/> of a <see cref="FakeUiThread"/>, in the shape of the WPF one:
/// a post made on the UI thread runs inline, one made anywhere else is queued for it.
/// </summary>
public sealed class FakeUiThreadDispatcher : IUiDispatcher
{
    private readonly FakeUiThread _thread;

    internal FakeUiThreadDispatcher(FakeUiThread thread) => _thread = thread;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_thread.CheckAccess())
            action();
        else
            _thread.Enqueue(static state => ((Action)state!)(), action);
    }
}
