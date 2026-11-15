using System.Diagnostics.CodeAnalysis;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.Tests.Doubles;

/// <summary>
/// The one UI thread the application has, for a test that walks the ViewModels without a window.
/// <para>
/// The ViewModels are bound to the thread that owns their collections. Under WPF the dispatcher
/// keeps them there: every <c>await</c> comes back to it, every discarded task continues on it,
/// every post lands on it. A test with no <see cref="SynchronizationContext"/> has none of that:
/// each continuation runs on whichever pool thread finished the work, so a refresh nobody awaits
/// rebuilds a collection while the walk is reading it. <see cref="RunAsync"/> gives the walk a
/// thread of its own with a context that queues onto it, and pumps that queue until the walk ends.
/// </para>
/// <para>
/// A callback that throws fails the walk at once, with that exception: under WPF it would take the
/// application down, and here it must not vanish on a thread nobody looks at. Once the walk has
/// ended and what was already queued has run, the thread is gone and a late post is dropped — a
/// window that closed.
/// </para>
/// </summary>
public sealed class FakeUiThread
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly PumpContext _context;
    private int _threadId;
    private bool _closed;

    private FakeUiThread()
    {
        _context = new PumpContext(this);
        Dispatcher = new FakeUiThreadDispatcher(this);
    }

    /// <summary>The dispatcher the ViewModels are given: it posts to this thread.</summary>
    public IUiDispatcher Dispatcher { get; }

    /// <summary>Whether the caller is on this thread.</summary>
    public bool CheckAccess() => Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);

    /// <summary>
    /// Runs <paramref name="walk"/> on a UI thread of its own and completes when it has ended:
    /// with its result, with its exception, or with the exception of a callback that threw.
    /// </summary>
    public static Task RunAsync(Func<FakeUiThread, Task> walk)
    {
        ArgumentNullException.ThrowIfNull(walk);

        var ui = new FakeUiThread();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A thread of its own, not a pool thread: it blocks while its queue is empty.
        var thread = new Thread(() => ui.Pump(walk, ended)) { IsBackground = true, Name = nameof(FakeUiThread) };
        thread.Start();

        return ended.Task;
    }

    /// <summary>Queues <paramref name="callback"/> for this thread; dropped once the walk has ended.</summary>
    internal void Enqueue(SendOrPostCallback callback, object? state)
    {
        lock (_gate)
        {
            if (_closed)
                return;

            _queue.Enqueue((callback, state));
            Monitor.Pulse(_gate);
        }
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "Whatever a callback or the walk throws is handed to the test through the task; nothing may escape on this thread.")]
    private void Pump(Func<FakeUiThread, Task> walk, TaskCompletionSource ended)
    {
        Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
        SynchronizationContext.SetSynchronizationContext(_context);

        Exception? thrown = null;
        Task? walking = null;
        try
        {
            walking = walk(this);
            // The end of the walk wakes the pump, which may be waiting on an empty queue.
            _ = walking.ContinueWith(
                static (_, state) => ((FakeUiThread)state!).Enqueue(static _ => { }, null),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            while (Next(walking) is { } next)
                next.Callback(next.State);
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        lock (_gate)
        {
            _closed = true;
            _queue.Clear();
        }

        if (thrown is not null)
            ended.SetException(thrown);
        else if (walking!.IsFaulted)
            ended.SetException(walking.Exception.InnerExceptions);
        else if (walking.IsCanceled)
            ended.SetCanceled();
        else
            ended.SetResult();
    }

    /// <summary>The next callback, waiting for one while the walk runs; null once it has ended and the queue is empty.</summary>
    private (SendOrPostCallback Callback, object? State)? Next(Task walking)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_queue.TryDequeue(out var next))
                    return next;

                if (walking.IsCompleted)
                    return null;

                Monitor.Wait(_gate);
            }
        }
    }

    private sealed class PumpContext(FakeUiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            owner.Enqueue(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);

            // Blocking another thread on this one is how a UI deadlocks: no ViewModel does it.
            if (!owner.CheckAccess())
                throw new NotSupportedException("A synchronous send to the UI thread from another thread is not supported.");

            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
