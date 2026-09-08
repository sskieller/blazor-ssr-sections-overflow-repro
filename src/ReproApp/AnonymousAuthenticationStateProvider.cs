using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace ReproApp;

/// <summary>
/// The smallest thing that satisfies <c>AuthorizeRouteView</c> without any identity stack —
/// but a genuinely asynchronous one that occupies a POOL THREAD, not a timer.
/// <para>
/// The crash stack in https://github.com/dotnet/aspnetcore/issues/69035 enters the HTML walk
/// from <c>WaitForResultReady</c> / <c>WaitForNonStreamingPendingTasks</c> continuations: the
/// write starts when the renderer's pending tasks complete, and a section registration still
/// lands during it. A provider returning <c>Task.FromResult</c> produces no pending task at all;
/// one returning <c>Task.Delay</c> produces a pending task that costs the pool nothing. The real
/// app has cookie Identity, whose <c>GetAuthenticationStateAsync</c> genuinely awaits real work.
/// </para>
/// The principal stays anonymous; only the timing and the scheduling are real.
/// </summary>
public sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await PoolWork.SpinAsync(2).ConfigureAwait(false);
        return Anonymous;
    }
}
