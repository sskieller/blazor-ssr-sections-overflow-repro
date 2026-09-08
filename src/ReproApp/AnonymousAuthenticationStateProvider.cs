using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace ReproApp;

/// <summary>
/// The smallest thing that satisfies <c>AuthorizeRouteView</c> without any identity stack:
/// every request is an anonymous, unauthenticated principal.
/// </summary>
public sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> Anonymous =
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Anonymous;
}
