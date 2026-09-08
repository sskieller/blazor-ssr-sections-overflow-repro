using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ReproApp.Tests;

/// <summary>
/// Hammers the three static-SSR entry points through an in-memory host, the way the real app's
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
public sealed class StaticSsrHammerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] Routes = ["/", "/page-a", "/page-b"];

    private readonly WebApplicationFactory<Program> _factory;

    public StaticSsrHammerTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static int Iterations => ReadEnvInt("REPRO_ITERATIONS", 300);

    private static int Parallelism => ReadEnvInt("REPRO_PARALLELISM", 8);

    [Fact]
    public async Task Concurrent_static_ssr_renders_all_return_200()
    {
        var iterations = Iterations;
        var parallelism = Parallelism;

        using var client = _factory.CreateClient();
        using var gate = new SemaphoreSlim(parallelism, parallelism);

        var failures = new ConcurrentBag<string>();
        var work = new List<Task>(iterations * Routes.Length);

        for (var i = 0; i < iterations; i++)
        {
            foreach (var route in Routes)
            {
                work.Add(RequestAsync(client, gate, route, failures));
            }
        }

        await Task.WhenAll(work);

        Assert.True(
            failures.IsEmpty,
            $"{failures.Count} of {iterations * Routes.Length} requests did not return 200:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures.Take(20)));
    }

    private static async Task RequestAsync(
        HttpClient client,
        SemaphoreSlim gate,
        string route,
        ConcurrentBag<string> failures)
    {
        await gate.WaitAsync();
        try
        {
            using var response = await client.GetAsync(route);
            // Drain the body: the static-SSR HTML walk that overflows runs while the response
            // is being written, so a request whose body is never read may never reach it.
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
            gate.Release();
        }
    }

    private static int ReadEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
