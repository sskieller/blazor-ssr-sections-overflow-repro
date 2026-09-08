using System.Diagnostics;

namespace ReproApp;

/// <summary>
/// The awaits in this repro used to be <c>Task.Delay</c> — a timer, which completes on the timer
/// queue and costs the thread pool nothing. The crashing app's pending tasks are EF/Npgsql queries
/// and cookie decryption: real work that occupies a pool thread while other requests are queued
/// behind it. This turns every await in the tree into that shape instead.
/// </summary>
public static class PoolWork
{
    /// <summary>
    /// Burns 0..<paramref name="maxMilliseconds"/> ms of CPU on a thread-pool thread.
    /// <c>ConfigureAwait(false)</c> is deliberate: the continuation must resume on a pool thread
    /// rather than being posted straight back through the renderer's dispatcher, so the section
    /// registration lands from wherever the pool happens to schedule it.
    /// </summary>
    public static Task SpinAsync(int maxMilliseconds) =>
        Task.Run(() => Spin(Random.Shared.Next(0, maxMilliseconds + 1)));

    private static void Spin(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            // Still hand the work to the pool: the point is the hop, not only the burn.
            Thread.SpinWait(50);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var budget = TimeSpan.FromMilliseconds(milliseconds);
        while (stopwatch.Elapsed < budget)
        {
            Thread.SpinWait(200);
        }
    }
}
