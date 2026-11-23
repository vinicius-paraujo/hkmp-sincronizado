using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Hkmp.Game.Server.Core;
using Xunit;

namespace Hkmp.NetworkTests;

/// <summary>
/// The server core: one thread for all server state, its mailbox, its deadlines, the timers built on them and the
/// guard that reports calls from other threads.
/// </summary>
public class ServerCoreTests {
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Deadlines_run_in_time_order_and_in_scheduling_order_when_due_together() {
        long now = 0;
        var scheduler = new Scheduler(() => now);
        var ran = new List<string>();
        scheduler.After(20, () => ran.Add("late"));
        scheduler.After(10, () => ran.Add("first"));
        scheduler.After(10, () => ran.Add("second"));

        now = 15;
        scheduler.RunDue(action => action());
        Assert.Equal(new[] { "first", "second" }, ran);

        now = 20;
        scheduler.RunDue(action => action());
        Assert.Equal(new[] { "first", "second", "late" }, ran);
    }

    [Fact]
    public void A_cancelled_deadline_never_runs() {
        long now = 0;
        var scheduler = new Scheduler(() => now);
        var ran = false;
        var task = scheduler.After(10, () => ran = true);

        task.Cancel();
        now = 10;
        scheduler.RunDue(action => action());

        Assert.False(ran);
        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public void The_scheduler_reports_how_long_until_the_next_deadline() {
        long now = 0;
        var scheduler = new Scheduler(() => now);
        Assert.Null(scheduler.MillisUntilNext());

        scheduler.After(30, () => { });
        now = 10;
        Assert.Equal(20, scheduler.MillisUntilNext());
    }

    [Fact]
    public void Posted_messages_run_on_the_core_thread_in_order() {
        using var core = new ServerCore();
        var order = new ConcurrentQueue<int>();
        var onCore = true;
        using var done = new ManualResetEventSlim();

        for (var i = 0; i < 100; i++) {
            var number = i;
            core.Post(() => {
                onCore &= core.IsCoreThread;
                order.Enqueue(number);
            });
        }
        core.Post(done.Set);

        Assert.True(done.Wait(Wait));
        Assert.True(onCore);
        Assert.Equal(System.Linq.Enumerable.Range(0, 100), order);
    }

    [Fact]
    public void Invoke_waits_for_the_work_and_passes_its_exception_back() {
        using var core = new ServerCore();
        var ranOnCore = false;

        core.Invoke(() => ranOnCore = core.IsCoreThread);
        Assert.True(ranOnCore);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            core.Invoke(() => throw new InvalidOperationException("from the core")));
        Assert.Equal("from the core", failure.Message);
    }

    [Fact]
    public void A_failing_message_does_not_stop_the_core() {
        using var core = new ServerCore();
        using var done = new ManualResetEventSlim();

        core.Post(() => throw new InvalidOperationException("broken handler"));
        core.Post(done.Set);

        Assert.True(done.Wait(Wait));
    }

    [Fact]
    public void A_scheduled_action_runs_on_the_core_after_its_delay() {
        using var core = new ServerCore();
        using var done = new ManualResetEventSlim();
        var onCore = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        core.Schedule(TimeSpan.FromMilliseconds(100), () => {
            onCore = core.IsCoreThread;
            done.Set();
        });

        Assert.True(done.Wait(Wait));
        Assert.True(onCore);
        Assert.True(clock.ElapsedMilliseconds >= 90, $"Ran after {clock.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void A_deadline_cancelled_from_another_thread_never_runs() {
        using var core = new ServerCore();
        var ran = false;

        var task = core.Schedule(TimeSpan.FromMilliseconds(100), () => ran = true);
        task.Cancel();
        Thread.Sleep(300);
        core.Invoke(() => { });

        Assert.False(ran);
    }

    [Fact]
    public void Background_work_returns_to_the_core_with_its_failure() {
        using var core = new ServerCore();
        using var done = new ManualResetEventSlim();
        var workOnCore = true;
        var finishedOnCore = false;
        Exception reported = null;

        core.RunInBackground(() => {
            workOnCore = core.IsCoreThread;
            throw new InvalidOperationException("disk full");
        }, failure => {
            finishedOnCore = core.IsCoreThread;
            reported = failure;
            done.Set();
        });

        Assert.True(done.Wait(Wait));
        Assert.False(workOnCore);
        Assert.True(finishedOnCore);
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void A_core_timer_elapses_on_the_core_and_repeats() {
        using var core = new ServerCore();
        var elapses = 0;
        var onCore = true;
        using var thirdElapse = new ManualResetEventSlim();
        var timer = new CoreTimer(core, 30, autoReset: true);
        timer.Elapsed += () => {
            onCore &= core.IsCoreThread;
            if (Interlocked.Increment(ref elapses) == 3) thirdElapse.Set();
        };

        timer.Start();
        Assert.True(thirdElapse.Wait(Wait));
        timer.Stop();

        Assert.True(onCore);
    }

    [Fact]
    public void Restarting_a_core_timer_pushes_its_elapse_back() {
        using var core = new ServerCore();
        var elapsed = 0;
        var timer = new CoreTimer(core, 200, autoReset: false);
        timer.Elapsed += () => Interlocked.Increment(ref elapsed);

        timer.Start();
        for (var i = 0; i < 5; i++) {
            Thread.Sleep(100);
            timer.Start();
        }

        Assert.Equal(0, Volatile.Read(ref elapsed));
        Thread.Sleep(400);
        Assert.Equal(1, Volatile.Read(ref elapsed));
    }

    [Fact]
    public void A_stopped_core_timer_does_not_elapse() {
        using var core = new ServerCore();
        var elapsed = false;
        var timer = new CoreTimer(core, 50, autoReset: false);
        timer.Elapsed += () => elapsed = true;

        timer.Start();
        timer.Stop();
        Thread.Sleep(200);

        Assert.False(elapsed);
    }

    [Fact]
    public void The_guard_counts_calls_from_other_threads_only() {
        using var core = new ServerCore();
        var guard = new CoreThreadGuard(core);

        core.Invoke(() => guard.Check("on the core"));
        Assert.Equal(0, guard.Violations);

        guard.Check("from the test thread");
        Assert.Equal(1, guard.Violations);
    }
}
