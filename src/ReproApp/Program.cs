using Microsoft.AspNetCore.Components.Authorization;
using ReproApp;
using ReproApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// AuthorizeRouteView needs authorization services and a cascading AuthenticationState.
// No identity, no database: the provider below always returns an anonymous user, which is
// enough to keep the AuthorizeRouteViewCore + two CascadingValue components that appear in
// the crashing componentId cycle.
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();

var app = builder.Build();

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// So WebApplicationFactory<Program> in the test project can see the entry point.
public partial class Program;
