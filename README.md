# blazor-ssr-sections-overflow-repro

Minimal repro attempt for **[dotnet/aspnetcore#69035](https://github.com/dotnet/aspnetcore/issues/69035)** —
*Static SSR: stack overflow — `StaticHtmlRenderer`'s HTML walk cycles through a disposed
`SectionOutletContentRenderer` when a section registration races the walk.*

## What this reproduces

On 2-core Linux CI runners a Blazor Web App's static-SSR render intermittently dies with a
process-killing stack overflow (`SIGABRT`, exit **134**) ~15,000 frames deep in
`StaticHtmlRenderer.RenderElement` → `RenderCore` → `EndpointHtmlRenderer.RenderChildComponent`.

Two independent core dumps from the original app show the same signature:

* one `SectionOutlet+SectionOutletContentRenderer` with `_componentWasDisposed == true`, still
  referenced from a live ancestor's `CurrentRenderTree`;
* the frame-walk arguments cycle over a handful of component ids (two `CascadingValue`s →
  `AuthorizeRouteViewCore` → root → back), not 15k real children;
* dump 2 has **no page markup at all** — root, `HeadOutlet` (both internal `SectionOutlet`s and
  their content renderers, one disposed), router/layout chrome, and an `SSRRenderModeBoundary`
  for a page that is `InteractiveServer` with `prerender: false`, plus a single layout-level
  `PageTitle`.

It has **never** reproduced on a Windows dev machine, and it does not reproduce on this
repository's author's box either. This project exists to give the ASP.NET Core team something
public to run on 2-core Linux hardware.

## The tree that is mirrored

The structure — and only the structure — of the app the crash was observed in:

| File | What it carries |
| --- | --- |
| `src/ReproApp/Components/App.razor` | The document root: `<HeadOutlet />` in `<head>`, `<Routes />` in `<body>`, `blazor.web.js` (enhanced navigation on, the default). |
| `src/ReproApp/Components/Routes.razor` | `Router` → `AuthorizeRouteView` (`DefaultLayout = MainLayout`) with `NotAuthorized` / `Authorizing` branches that each use `LayoutView` (the `NotAuthorized` branch carries its own `PageTitle`), plus `FocusOnNavigate`. |
| `src/ReproApp/Components/Layout/MainLayout.razor` | Exactly **one** layout-level `<PageTitle>` and `@Body`. Dump 2 shows a single hoisted layout `PageTitle` still racing the walk, so this is the hoisted shape, not the many-swaps shape. |
| `src/ReproApp/Components/Pages/Home.razor` | `/` — static SSR, no `PageTitle` of its own (the layout's wins). |
| `src/ReproApp/Components/Pages/PageA.razor` | `/page-a` — static SSR **with its own `PageTitle`**: a second registration for the same `HeadOutlet` section, replacing the layout's while the HTML walk runs. |
| `src/ReproApp/Components/Pages/PageB.razor` | `/page-b` — `@rendermode @(new InteractiveServerRenderMode(prerender: false))` and **no `PageTitle`**: an `SSRRenderModeBoundary` that emits no page markup on the static pass. Dump 2's tree. |
| `src/ReproApp/Components/Pages/NotFound.razor` | `/not-found`, wired through `UseStatusCodePagesWithReExecute`. |
| `src/ReproApp/AnonymousAuthenticationStateProvider.cs` | The only concession to `AuthorizeRouteView`: `AddAuthorizationCore()` + `AddCascadingAuthenticationState()` + a provider that always returns an anonymous principal. `AuthorizeRouteView` is kept because `AuthorizeRouteViewCore` and its two `CascadingValue`s are inside the crashing component-id cycle. |

No auth, no database, no styling, no third-party packages. `tests/ReproApp.Tests/TreeShapeTests.cs`
asserts the tree really behaves as described (`/page-a` renders `<title>Page A</title>` — the page
replaced the layout's section content; `/page-b` renders `<title>Repro</title>` and a render-mode
boundary comment with no page markup).

## The driver

`tests/ReproApp.Tests/StaticSsrHammerTests.cs` runs a `WebApplicationFactory<Program>` in-memory
host and fires `REPRO_ITERATIONS` × {`/`, `/page-a`, `/page-b`} requests through one `HttpClient`
with at most `REPRO_PARALLELISM` in flight, via `Task.WhenAll`, asserting 200 and a non-empty body.

The suite is **xUnit v3**, built as its own executable on purpose: a stack overflow can never be
observed as a failed assertion — the runtime aborts the process. Running the test assembly
directly means the SIGABRT surfaces as `dotnet run`'s own exit code 134. Under the VSTest runner
the crash happens in a `testhost` child and `dotnet test` reports a plain exit 1, which hides the
signal.

## Running it locally

```bash
./repro.sh
```

Environment knobs (all optional):

| Variable | Default | Meaning |
| --- | --- | --- |
| `REPRO_ROUNDS` | `5` | How many times to run the suite. |
| `REPRO_ITERATIONS` | `300` | Requests per route per round. |
| `REPRO_PARALLELISM` | `8` | Concurrent in-flight requests. |
| `REPRO_NO_TASKSET` | unset | Set to `1` to skip pinning to cores 0,1. |
| `REPRO_CONFIGURATION` | `Release` | Build configuration. |

`repro.sh` runs every round under the crash trap

```
DOTNET_DbgEnableMiniDump=1
DOTNET_DbgMiniDumpType=2          # heap included — the render tree lives there
DOTNET_DbgMiniDumpName=/tmp/repro-%p.dmp
```

and, when `taskset` is available, pins the round to two cores, because the bug has only ever been
seen on 2-core runners. It prints each round's exit code, stops and prints `REPRODUCED (exit 134)`
on the first round that aborts, and lists any `/tmp/repro-*.dmp` at the end.

A plain run without the driver:

```bash
dotnet build
REPRO_ITERATIONS=50 dotnet test -c Release
# or, to get the raw process exit code (134 on a hit):
REPRO_ITERATIONS=50 dotnet run --project tests/ReproApp.Tests -c Release --no-build
```

`global.json` opts `dotnet test` into the Microsoft.Testing.Platform runner, which xUnit v3
needs on the .NET 10 SDK. It deliberately does **not** pin an SDK version.

## Running it in CI

`.github/workflows/repro.yml` runs on every push and on `workflow_dispatch`. It fans out **10
`ubuntu-latest` shards**, each running `repro.sh` for 5 rounds of 2,000 iterations per route.
Each shard installs `dotnet-dump`, and when a dump exists runs

```bash
dotnet-dump analyze <dmp> -c clrstack -c "dumpheap -type SectionOutletContentRenderer" -c exit
```

printing the output into the log, uploads every dump as an artifact (`if: always()`), writes
*reproduced yes/no* into its step summary, and **fails the shard when it reproduced** so a hit is
visible straight from the run list. A green run list therefore means "not reproduced in this
attempt".

## Results

Fill in as runs land.

| Date | Runner | Shards | Rounds × iterations | Hits | Hit rate | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| | `ubuntu-latest` (2 vCPU) | 10 | 5 × 2000 | | | |

Local Windows control run (author's box, 2026-09-09, .NET SDK 10.0.302 / runtime 10.0.10, many
cores): `dotnet build` clean (0 warnings, 0 errors), `REPRO_ITERATIONS=50 dotnet test -c Release`
— 3 tests, 0 failures, exit 0; `REPRO_ROUNDS=1 REPRO_ITERATIONS=50 ./repro.sh` — round exit 0, no
dumps, `NOT REPRODUCED`. Expected: this has never reproduced on Windows or on a many-core box.

## About the dumps

The dumps this repository produces contain **only this repro's own memory**: a template Blazor Web
App with five near-empty components, no auth, no database, no user data, no secrets, no
configuration beyond the defaults in `src/ReproApp/appsettings.json`. They are safe to attach to
the public issue.
