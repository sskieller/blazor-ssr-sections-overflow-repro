using System.Diagnostics;
using System.Security.Cryptography;

namespace ReproApp.Tests;

/// <summary>
/// Not a test of anything: a stand-in for the rest of a real integration suite. In the app where
/// the crash was observed, the static-SSR renders were never alone on the runner — dozens of
/// other tests were competing for the same two cores, which is the contention the walk was
/// preempted by. xUnit runs collections in parallel and this class is in a different collection
/// from the hammer, so the two overlap.
/// <para>
/// The burners run on DEDICATED FOREGROUND THREADS, not <c>Task.Run</c>. Shards 6-10 start the
/// process with <c>DOTNET_ThreadPool_ForceMaxWorkerThreads=2</c>; pool-based burners would take
/// both worker threads and livelock the hammer instead of merely competing with it (measured:
/// zero requests in 10 minutes). Dedicated threads compete for CPU, which is the point, without
/// consuming the deliberately starved pool.
/// </para>
/// </summary>
[Collection("noise")]
public sealed class NoiseTests
{
    private static int Threads => ReadEnvInt("REPRO_NOISE_THREADS", 2);

    private static int CapSeconds => ReadEnvInt("REPRO_NOISE_CAP_SECONDS", 900);

    [Fact]
    public async Task Burn_cpu_for_as_long_as_the_hammer_runs()
    {
        var threads = Threads;
        var clock = Stopwatch.StartNew();
        var cap = TimeSpan.FromSeconds(CapSeconds);
        var rounds = 0L;

        var burners = Enumerable.Range(0, threads)
            .Select(_ =>
            {
                var thread = new Thread(() =>
                {
                    var buffer = new byte[4096];
                    Random.Shared.NextBytes(buffer);

                    while (!ReproSignals.HammerFinished.IsCompleted && clock.Elapsed < cap)
                    {
                        var digest = SHA256.HashData(buffer);
                        digest.CopyTo(buffer, 0);
                        if (Interlocked.Increment(ref rounds) % 4096 == 0)
                        {
                            // Never monopolise a core outright: the goal is contention, not
                            // starvation of the thing being measured.
                            Thread.Sleep(0);
                        }
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                };
                thread.Start();
                return thread;
            })
            .ToArray();

        foreach (var thread in burners)
        {
            while (thread.IsAlive)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(Interlocked.Read(ref rounds) >= 0);
    }

    private static int ReadEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
