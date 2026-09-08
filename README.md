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
| `src/ReproApp/Components/Layout/MainLayout.razor` | Exactly **one** layout-level `<PageTitle>` and `@Body`. Dump 2 shows a single hoisted layout `PageTitle` still racing the walk, so this is the hoisted shape, not the many-swaps shape. The layout awaits before it renders, so that registration is a **continuation**, not part of the first synchronous pass. |
| `src/ReproApp/Components/Pages/Home.razor` | `/` — static SSR, no `PageTitle` of its own (the layout's wins). |
| `src/ReproApp/Components/Pages/PageA.razor` | `/page-a` — static SSR **with its own `PageTitle`**, also registered after an await: a second registration for the same `HeadOutlet` section, from a second pending task, replacing the layout's while the HTML walk runs. |
| `src/ReproApp/Components/Pages/PageB.razor` | `/page-b` — `@rendermode @(new InteractiveServerRenderMode(prerender: false))` and **no `PageTitle`**: an `SSRRenderModeBoundary` that emits no page markup on the static pass. Dump 2's tree. |
| `src/ReproApp/Components/Pages/PageC.razor` | `/page-c` — `@attribute [StreamRendering]`, an awaited randomised `Task.Delay(1..5 ms)`, and its `PageTitle` rendered **only after the await**, inside the loaded branch. The first walk emits no section registration at all; the registration lands on the streamed second walk. |
| `src/ReproApp/Components/Pages/PageD.razor` | `/page-d` — the **pre-hoist shape** the issue names: a `PageTitle` inside a data branch. The loading branch registers one, the continuation flips the `@if`, and that first `PageTitle` component is *disposed* while a second is *registered* for the same section inside a single static-SSR render. |
| `src/ReproApp/Components/Pages/PageE.razor` | `/page-e` — **non-streaming** static SSR that awaits and then swaps **both** of `HeadOutlet`'s outlets late: the title section via `PageTitle` and the head-content section via `HeadContent` (the public `SectionContent` for `HeadOutlet`'s second `SectionOutlet`, here a `<meta>`). Two outlets, two content renderers, both replaced from a continuation. |
| `src/ReproApp/Components/Pages/NotFound.razor` | `/not-found`, wired through `UseStatusCodePagesWithReExecute`. |
| `src/ReproApp/AnonymousAuthenticationStateProvider.cs` | The only concession to `AuthorizeRouteView`: `AddAuthorizationCore()` + `AddCascadingAuthenticationState()` + a provider that always returns an anonymous principal — but **genuinely asynchronously** (`Task.Yield` + a randomised `Task.Delay(0..3 ms)`), so `AuthorizeRouteView` awaits on every request the way cookie Identity does in the real app. `AuthorizeRouteView` is kept because `AuthorizeRouteViewCore` and its two `CascadingValue`s are inside the crashing component-id cycle. |

No auth, no database, no styling, no third-party packages. `tests/ReproApp.Tests/TreeShapeTests.cs`
asserts the tree really behaves as described, so a green hammer run cannot be green because the app
quietly stopped doing the interesting thing:

* `/page-a` renders `<title>Page A</title>` — the page replaced the layout's section content;
* `/page-b` renders `<title>Repro</title>`, a render-mode boundary comment and no page markup;
* `/page-c` renders the layout's title plus a `blazor-ssr` streamed update carrying the late
  registration;
* `/page-d` renders `<title>Page D loaded</title>` and never `<title>Loading...</title>` — the first
  `PageTitle` really was disposed and replaced within the one render;
* `/page-e` renders `<title>Page E</title>` *and* the `<meta name="repro-page">`, with no
  `blazor-ssr` patch — both head outlets were swapped inside the single non-streaming walk;
* `/` renders `<title>Repro</title>` — the renderer waited for the layout's pending task before
  writing the HTML.

Because every one of those titles is registered from a *continuation*, a correct title is also
proof that the async path really ran.

### Why the awaits matter

The crash stack enters the HTML walk from `WaitForResultReady` / `WaitForNonStreamingPendingTasks`
continuations: the write starts when the renderer's **pending tasks** complete, and a section
registration still lands during it. A tree with no pending tasks never takes that path. The first
two CI runs (2.8M requests, 0 hits) had exactly that problem — a `Task.FromResult` auth provider
and no async initialisation anywhere. Every component in the tree now awaits before it registers
its section, which drops in-memory throughput from ~6,500 to ~1,100 req/s locally: that cost *is*
the pending tasks being real.

## The driver

`tests/ReproApp.Tests/StaticSsrHammerTests.cs` runs a `WebApplicationFactory<Program>` in-memory
host and fires `REPRO_ITERATIONS` × {`/`, `/page-a`, `/page-b`, `/page-c`, `/page-d`, `/page-e`}
requests through one `HttpClient` with at most `REPRO_PARALLELISM` in flight, via `Task.WhenAll`,
asserting 200 and a non-empty body.

It prints its own effective configuration and result straight to the process's standard output
handle (bypassing the runner's `Console.Out` capture), so the CI log proves the load that actually
ran rather than the load the workflow intended:

```
HAMMER config: REPRO_ITERATIONS=200 (env "200") REPRO_PARALLELISM=16 (env "16") routes=6 (/ /page-a /page-b /page-c /page-d /page-e) total_requests=1200 processors=20
HAMMER done: requests=1200/1200 failures=0 wall=1.07s rate=1120 req/s
```

Rounds look quick because the host is in-memory, with no sockets and no TLS: tens of thousands of
renders cost seconds, not minutes. A short round is a full round, not a skipped one — check the
`HAMMER` lines rather than the clock.

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
| `REPRO_CPUS` | `0,1` | `taskset` cpu list to pin each round to. `0` = single core. |
| `REPRO_NO_TASKSET` | unset | Set to `1` to skip pinning altogether. |
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
`ubuntu-latest` shards**, each running `repro.sh` for **10 rounds of 5,000 iterations per route at
parallelism 16** (30,000 requests per round over six routes, 300,000 per shard). Shards 1–5 run
under `taskset -c 0,1` — the two-core shape the crash was observed on; shards 6–10 run under
`taskset -c 0`, a single core, which maximises preemption inside the synchronous walk.
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

| Run | Date | Runner | Shards | Rounds × iterations × routes | Requests | Hits | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| [34289519005](https://github.com/sskieller/blazor-ssr-sections-overflow-repro/actions/runs/34289519005) | 2026-09-09 | `ubuntu-latest` (2 vCPU), `taskset -c 0,1` | 10 | 5 × 2000 × 3 | 300,000 | **0** | All 10 shards green, every round exit 0. Rounds took ~4 s each, which is a *complete* round at ~1,500 req/s in-memory — not a skipped one. Routes were `/`, `/page-a`, `/page-b` only; no streaming page and no PageTitle-in-a-data-branch page yet. |
| [34290063356](https://github.com/sskieller/blazor-ssr-sections-overflow-repro/actions/runs/34290063356) | 2026-09-09 | `ubuntu-latest` (2 vCPU), shards 1–5 `taskset -c 0,1`, shards 6–10 `taskset -c 0` | 10 | 10 × 5000 × 5 | 2,500,000 | **0** | All 10 shards green. Load proven in the log: `25000/25000` requests per round at ~1,700 req/s. Added `/page-c` (streaming) and `/page-d` (flipping data branch). **Diagnosis: the tree still had no pending tasks** — a `Task.FromResult` auth provider and no async init anywhere — so the renderer never entered the walk from `WaitForNonStreamingPendingTasks`, which is where the crash stack enters it. |
| RUN3_URL_PLACEHOLDER | 2026-09-09 | `ubuntu-latest` (2 vCPU), shards 1–5 `taskset -c 0,1`, shards 6–10 `taskset -c 0` | 10 | 10 × 5000 × 6 | 3,000,000 | *pending* | Every section registration is now a **continuation**: the auth provider genuinely awaits, the layout awaits before its `PageTitle`, `/page-a` awaits before its own, and `/page-e` swaps both `HeadOutlet` outlets late. Local throughput fell ~6×, which is the pending tasks being real. |

Local Windows control run (author's box, 2026-09-09, .NET SDK 10.0.302 / runtime 10.0.10, 20
cores): `dotnet build` clean (0 warnings, 0 errors); `REPRO_ITERATIONS=200 REPRO_PARALLELISM=16
dotnet run --project tests/ReproApp.Tests` — 7 tests, 0 failures, 1,200/1,200 requests, 1.07 s,
exit 0. Expected: this has never reproduced on Windows or on a many-core box.

## About the dumps

The dumps this repository produces contain **only this repro's own memory**: a template Blazor Web
App with five near-empty components, no auth, no database, no user data, no secrets, no
configuration beyond the defaults in `src/ReproApp/appsettings.json`. They are safe to attach to
the public issue.
