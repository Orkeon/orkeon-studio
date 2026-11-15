using Orkeon.Studio.Wpf.Tests.Doubles;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The headless walks stand on <see cref="FakeUiThread"/> for what the WPF dispatcher gives the
/// application: one thread, everything on it, in order. These hold the double to that.
/// </summary>
public sealed class FakeUiThreadTests
{
    [Fact]
    public async Task The_walk_and_the_continuations_of_its_awaits_run_on_the_one_ui_thread()
    {
        var threads = new List<int>();
        var onUiThread = new List<bool>();

        await FakeUiThread.RunAsync(async ui =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            onUiThread.Add(ui.CheckAccess());

            await Task.Yield();
            threads.Add(Environment.CurrentManagedThreadId);

            // Work finished by a pool thread: the continuation still comes back.
            var elsewhere = await Task.Run(() => Environment.CurrentManagedThreadId, TestContext.Current.CancellationToken);
            threads.Add(Environment.CurrentManagedThreadId);
            onUiThread.Add(ui.CheckAccess());

            Assert.NotEqual(threads[0], elsewhere);
        });

        Assert.Equal(3, threads.Count);
        Assert.Single(threads.Distinct());
        Assert.All(onUiThread, Assert.True);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threads[0]);
    }

    [Fact]
    public async Task Posts_made_from_another_thread_run_on_the_ui_thread_in_the_order_they_were_made()
    {
        var order = new List<int>();
        var threads = new HashSet<int>();
        var uiThread = 0;

        await FakeUiThread.RunAsync(async ui =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await Task.Run(
                () =>
                {
                    for (var index = 0; index < 200; index++)
                    {
                        var number = index;
                        ui.Dispatcher.Post(() =>
                        {
                            order.Add(number);
                            threads.Add(Environment.CurrentManagedThreadId);
                        });
                    }

                    ui.Dispatcher.Post(last.SetResult);
                },
                TestContext.Current.CancellationToken);

            await last.Task;
        });

        Assert.Equal(Enumerable.Range(0, 200), order);
        Assert.Equal([uiThread], threads);
    }

    [Fact]
    public async Task A_post_made_on_the_ui_thread_runs_inline_like_the_wpf_dispatcher()
    {
        var ran = false;
        var ranBeforePostReturned = false;

        await FakeUiThread.RunAsync(ui =>
        {
            ui.Dispatcher.Post(() => ran = true);
            ranBeforePostReturned = ran;
            return Task.CompletedTask;
        });

        Assert.True(ranBeforePostReturned);
    }

    [Fact]
    public async Task A_task_nobody_awaits_continues_on_the_ui_thread_and_never_beside_the_walk()
    {
        var inside = 0;
        var overlaps = 0;
        var threads = new HashSet<int>();
        var uiThread = 0;

        await FakeUiThread.RunAsync(async ui =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            var discarded = new List<Task>();

            // The shape of the window's discarded refreshes: started, not awaited, finished by a pool thread.
            for (var index = 0; index < 50; index++)
            {
                discarded.Add(RefreshAsync());
                Touch();
                await Task.Yield();
            }

            await Task.WhenAll(discarded);

            async Task RefreshAsync()
            {
                await Task.Run(static () => { }, TestContext.Current.CancellationToken).ConfigureAwait(true);
                Touch();
            }

            // What a ViewModel does to a collection it owns: no lock, because one thread.
            void Touch()
            {
                if (Interlocked.Increment(ref inside) != 1)
                    Interlocked.Increment(ref overlaps);
                threads.Add(Environment.CurrentManagedThreadId);
                Interlocked.Decrement(ref inside);
            }
        });

        Assert.Equal(0, overlaps);
        Assert.Equal([uiThread], threads);
    }

    [Fact]
    public async Task An_exception_thrown_by_a_posted_callback_fails_the_walk_even_when_the_walk_would_never_end()
    {
        var never = new TaskCompletionSource();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => FakeUiThread.RunAsync(async ui =>
        {
            _ = Task.Run(
                () => ui.Dispatcher.Post(() => throw new InvalidOperationException("posted")),
                TestContext.Current.CancellationToken);

            await never.Task;
        }));

        Assert.Equal("posted", thrown.Message);
    }

    [Fact]
    public async Task An_exception_thrown_by_a_task_nobody_observes_on_the_context_fails_the_walk()
    {
        var never = new TaskCompletionSource();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => FakeUiThread.RunAsync(async _ =>
        {
            // Posted straight to the context, as an « async void » handler's fault is.
            SynchronizationContext.Current!.Post(static _ => throw new InvalidOperationException("context"), null);

            await never.Task;
        }));

        Assert.Equal("context", thrown.Message);
    }

    [Fact]
    public async Task The_exception_of_the_walk_itself_reaches_the_test_as_it_was_thrown()
    {
        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => FakeUiThread.RunAsync(async _ =>
        {
            await Task.Yield();
            throw new ArgumentException("walk");
        }));

        Assert.Equal("walk", thrown.Message);

        var synchronous = await Assert.ThrowsAsync<ArgumentException>(
            () => FakeUiThread.RunAsync(static _ => throw new ArgumentException("at once")));

        Assert.Equal("at once", synchronous.Message);
    }

    [Fact]
    public async Task What_was_queued_before_the_walk_ended_still_runs_and_a_post_made_after_it_is_dropped()
    {
        FakeUiThread? ended = null;
        var queuedBefore = false;
        var postedAfter = false;

        await FakeUiThread.RunAsync(ui =>
        {
            ended = ui;
            SynchronizationContext.Current!.Post(_ => queuedBefore = true, null);
            return Task.CompletedTask;
        });

        Assert.NotNull(ended);
        Assert.True(queuedBefore);

        // The thread is gone: nothing runs the callback, and nothing runs it inline on this thread.
        ended.Dispatcher.Post(() => postedAfter = true);

        Assert.False(ended.CheckAccess());
        Assert.False(postedAfter);
    }
}
