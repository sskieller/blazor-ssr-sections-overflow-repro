using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ReproApp.Tests;

/// <summary>
/// Hammers the static-SSR entry points through an in-memory host, the way the real app's
/// WebApplicationFactory integration suite does. The bug being chased
/// (https://github.com/dotnet/aspnetcore/issues/69035) is a race between a section
/// registration (HeadOutlet / PageTitle) and EndpointHtmlRenderer.WriteComponentHtml's
/// synchronous HTML walk, so what matters is many concurrent renders on few cores, not
/// anything about the page content.
///
/// It does not fail with an assertion when it hits: the stack overflow kills the process with
/// SIGABRT (exit 134). A green run here proves nothing except that the host is wired; the
/// signal is the exit code, which is why repro.sh loops rounds and looks at $?.
/// </summary>
[Collection("hammer")]
public sealed class StaticSsrHammerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] Routes = ["/", "/page-a", "/page-b", "/page-c", "/page-d", "/page-e"];

    private static readonly Stream StdOut = Console.OpenStandardOutput();

    private readonly WebApplicationFactory<Program> _factory;

    public StaticSsrHammerTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static int Iterations => ReadEnvInt("REPRO_ITERATIONS", 300);

    private static int Parallelism => ReadEnvInt("REPRO_PARALLELISM", 8);

    [Fact]
    public async Task Concurrent_static_ssr_renders_all_return_200()
    {
        var iterations = Iterations;
        var parallelism = Parallelism;
        var total = iterations * Routes.Length;

        Emit($"HAMMER config: REPRO_ITERATIONS={Describe("REPRO_ITERATIONS", iterations)} "
            + $"REPRO_PARALLELISM={Describe("REPRO_PARALLELISM", parallelism)} "
            + $"routes={Routes.Length} ({string.Join(' ', Routes)}) "
            + $"total_requests={total} processors={Environment.ProcessorCount} "
            + $"ForceMinWorkerThreads={EnvOrUnset("DOTNET_ThreadPool_ForceMinWorkerThreads")} "
            + $"ForceMaxWorkerThreads={EnvOrUnset("DOTNET_ThreadPool_ForceMaxWorkerThreads")} "
            + $"pool_min={PoolMin} pool_max={PoolMax}");

        using var client = _factory.CreateClient();
        using var gate = new SemaphoreSlim(parallelism, parallelism);

        var failures = new ConcurrentBag<string>();
        var completed = 0;
        var work = new List<Task>(total);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            foreach (var route in Routes)
            {
                work.Add(RequestAsync(client, gate, route, failures, () => Interlocked.Increment(ref completed)));
            }
        }

        await Task.WhenAll(work);
        stopwatch.Stop();
        ReproSignals.SignalHammerFinished();

        var done = Volatile.Read(ref completed);
        var perSecond = done / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        Emit($"HAMMER done: requests={done}/{total} failures={failures.Count} "
            + $"wall={stopwatch.Elapsed.TotalSeconds:F2}s rate={perSecond:F0} req/s");

        Assert.Equal(total, done);
        Assert.True(
            failures.IsEmpty,
            $"{failures.Count} of {total} requests did not return 200:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures.Take(20)));
    }

    private static async Task RequestAsync(
        HttpClient client,
        SemaphoreSlim gate,
        string route,
        ConcurrentBag<string> failures,
        Action onCompleted)
    {
        await gate.WaitAsync();
        try
        {
            using var response = await client.GetAsync(route);
            // Drain the body: the static-SSR HTML walk that overflows runs while the response
            // is being written, and /page-c streams, so a request whose body is never read may
            // never reach the second walk at all.
            var body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode != HttpStatusCode.OK)
            {
                failures.Add($"{route} -> {(int)response.StatusCode}");
            }
            else if (body.Length == 0)
            {
                failures.Add($"{route} -> 200 with an empty body");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{route} -> {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            onCompleted();
            gate.Release();
        }
    }

    private static string PoolMin
    {
        get
        {
            ThreadPool.GetMinThreads(out var worker, out var io);
            return $"{worker}w/{io}io";
        }
    }

    private static string PoolMax
    {
        get
        {
            ThreadPool.GetMaxThreads(out var worker, out var io);
            return $"{worker}w/{io}io";
        }
    }

    private static string EnvOrUnset(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(raw) ? "(unset)" : raw;
    }

    private static string Describe(string name, int effective)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(raw)
            ? $"{effective} (unset, default)"
            : $"{effective} (env \"{raw}\")";
    }

    /// <summary>
    /// Writes straight to the process's standard output handle. The runner replaces
    /// <see cref="Console.Out"/> to capture per-test output, and that capture has been known to
    /// be dropped or reordered by the test host — this line is the CI log's only proof that the
    /// configured load actually ran, so it must not depend on any of that.
    /// </summary>
    private static void Emit(string line)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            lock (StdOut)
            {
                StdOut.Write(bytes, 0, bytes.Length);
                StdOut.Flush();
            }
        }
        catch (Exception)
        {
            // A redirected/closed handle must never take the hammer down.
            Console.WriteLine(line);
        }
    }

    private static int ReadEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
