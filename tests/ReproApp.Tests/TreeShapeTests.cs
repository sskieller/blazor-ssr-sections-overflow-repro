using Microsoft.AspNetCore.Mvc.Testing;

namespace ReproApp.Tests;

/// <summary>
/// Proves the repro really has the tree the dumps show, so a green hammer run cannot be green
/// because the app quietly stopped doing the interesting thing.
/// </summary>
public sealed class TreeShapeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TreeShapeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Page_a_replaces_the_layout_section_content()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/page-a", TestContext.Current.CancellationToken);

        // The page's own PageTitle won the HeadOutlet section over the layout's.
        Assert.Contains("<title>Page A</title>", html);
        Assert.DoesNotContain("<title>Repro</title>", html);
    }

    [Fact]
    public async Task Page_b_keeps_the_layout_section_content_and_emits_a_render_mode_boundary()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/page-b", TestContext.Current.CancellationToken);

        // Only the layout registered a section: dump 2's shape.
        Assert.Contains("<title>Repro</title>", html);
        // prerender:false -> a boundary marker and no page markup on the static pass.
        Assert.Contains("Blazor:", html); // the SSRRenderModeBoundary marker comment
        Assert.DoesNotContain("Interactive Server, prerender:false", html);
    }
}
