using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace ReproApp;

/// <summary>
/// The smallest thing that satisfies <c>AuthorizeRouteView</c> without any identity stack —
/// but a GENUINELY ASYNCHRONOUS one.
/// <para>
/// The crash stack in https://github.com/dotnet/aspnetcore/issues/69035 enters the HTML walk
/// from <c>WaitForResultReady</c> / <c>WaitForNonStreamingPendingTasks</c> continuations: the
/// write starts when the renderer's PENDING TASKS complete, and a section registration still
/// lands during it. A provider returning <c>Task.FromResult</c> produces no pending task at
/// all, so the renderer never takes that path — which is why the first two CI runs (2.8M
/// requests) never hit. The real app has cookie Identity, whose
/// <c>GetAuthenticationStateAsync</c> genuinely awaits, and both dumps' trees run through
/// <c>AuthorizeRouteView</c>.
/// </para>
/// The principal stays anonymous; only the timing is real.
/// </summary>
public sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // Task.Yield guarantees the caller really suspends; the randomised delay moves where
        // the continuation lands relative to the walk on every request.
        await Task.Yield();
        await Task.Delay(Random.Shared.Next(0, 4));
        return Anonymous;
    }
}
