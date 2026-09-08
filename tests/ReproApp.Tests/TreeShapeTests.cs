using Microsoft.AspNetCore.Mvc.Testing;

namespace ReproApp.Tests;

/// <summary>
/// Proves the repro really has the tree the dumps show, so a green hammer run cannot be green
/// because the app quietly stopped doing the interesting thing. Every title asserted here is
/// registered from a CONTINUATION (the layout, the auth state provider and every page await
/// before they render), so a correct title is also proof the async paths ran.
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

    [Fact]
    public async Task Page_c_registers_its_section_on_the_streamed_pass()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/page-c", TestContext.Current.CancellationToken);

        // The first walk emitted the loading branch under the layout's title...
        Assert.Contains("<title>Repro</title>", html);
        Assert.Contains("Loading...", html);
        // ...and the section registration arrived in a streamed update after the await.
        Assert.Contains("blazor-ssr", html);
        Assert.Contains("Streamed, PageTitle registered after the await.", html);
    }

    [Fact]
    public async Task Page_d_disposes_and_re_registers_its_section_within_one_render()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/page-d", TestContext.Current.CancellationToken);

        // The loading branch's PageTitle was disposed and replaced by the loaded branch's
        // before the response was written: the final document carries only the second one.
        Assert.Contains("<title>Page D loaded</title>", html);
        Assert.DoesNotContain("<title>Loading...</title>", html);
        Assert.DoesNotContain("<title>Repro</title>", html);
    }

    [Fact]
    public async Task Page_e_swaps_both_head_outlets_from_a_continuation()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/page-e", TestContext.Current.CancellationToken);

        // The title outlet took the page's late registration...
        Assert.Contains("<title>Page E</title>", html);
        Assert.DoesNotContain("<title>Repro</title>", html);
        // ...and so did HeadOutlet's second outlet, the head-content section.
        Assert.Contains("name=\"repro-page\"", html);
        // Non-streaming: the whole document arrived in one walk, no streamed patch needed.
        Assert.DoesNotContain("blazor-ssr", html);
    }

    [Fact]
    public async Task Home_shows_the_layouts_awaited_title()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        // The layout registers its PageTitle only after its own await; seeing it here proves
        // the renderer waited for that pending task before writing the HTML.
        Assert.Contains("<title>Repro</title>", html);
    }
}
