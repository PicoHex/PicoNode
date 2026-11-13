# AGENTS.md

Orientation for agents (and humans) working in this repository. Keep it short and factual, and add to it
when you learn something the hard way. Note that `**/superpowers/` is gitignored (`.gitignore:416`), so the
SDD ledgers and design plans under `.superpowers/` and `docs/superpowers/` never leave this machine: anything
that must outlive the workspace belongs in a tracked doc (or in a commit message) instead.

## Build & test

- Whole solution (samples included): `dotnet build PicoNode.slnx -c Release --nologo`.
- One project: `dotnet test --project tests/<Project>/<Project>.csproj -c Release` — `--project` is the
  form `dotnet test --help` documents (SDK 10); a bare path is accepted but is not in its usage line.
- **Never pass `--nologo` to `dotnet test`** on SDK 10 — it makes the run report zero tests.
  `dotnet build --nologo` is fine.
- Runner arguments go **after** `--` (TUnit), e.g. `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -c Release -- --treenode-filter '/*/*/Name'`.
- TUnit exits **2** (not 1) on failure and colourises its output: strip ANSI before matching.
- NativeAOT check: `scripts/test-aot-publish.ps1 -RuntimeIdentifier linux-x64` (CI runs it once per matrix RID — win-x64,
  win-arm64, linux-x64, linux-arm64, osx-arm64).
- README samples: `dotnet test tests/PicoNode.Docs.Tests/PicoNode.Docs.Tests.csproj` compiles the README code
  blocks and measures them. Eleven of the twenty blocks are compiled samples (`#region readme:*` in
  `samples/readme/*.cs` and `ReadmeSamples.cs`) and must match their region byte for byte in all ten READMEs;
  a block that is deliberately not compilable carries `<!-- readme-fragment: reason -->` on the line above it,
  so an uncompiled snippet is a decision in review rather than an omission. `ReadmeAccuracyTests` measures the
  rest of the prose (line count, TFMs, dependency chains, package tables, `tests/README.md` counts).
- Zero external dependencies and AOT-first: no reflection, no `dynamic`, no runtime attribute consumption,
  no per-request LINQ or closures on hot paths. A new dependency or a trim-unsafe construct is a design
  change, not a detail.

## Public API

- `api/<PackageId>.public.txt` is the contract, produced **only** by the tool:
  `dotnet run --project scripts/api-surface -- dump <path/to/Assembly.dll> --out api/<PackageId>.public.txt`.
  Never hand-edit a baseline, and a dumper change must not alter unrelated baseline lines.
- **A baseline holds the surface of the LAST RELEASE** and is refreshed as part of the release commit.
  Do not refresh it mid-cycle for an in-flight feature: the release gate derives the version by diffing the
  freshly dumped surface against these files, so an early refresh hides a public-API change and the packages
  ship with a `y` bump where they need an `x` bump. That happened once — the rate-limit policy release went
  out as v2026.4.7 and had to be re-released as v2026.5.0; see the deviation note in
  `docs/specs/rate-limit-policy-design.md` §8.

## Releasing

- Dry run first: `./scripts/release.ps1 -DryRun` prints `last release`, `API changed : true|false` and the
  next version. Read it **before** touching the baselines.
- `./scripts/release.ps1 -Push` refreshes the baselines and commits `chore(release): vX.Y.Z` **only when
  the refresh changed them** (an unchanged API tags the current commit instead); then it creates an
  annotated tag and pushes branch and tag.
- **The tag is the release trigger**: `.github/workflows/release.yml` gates the publish on a **`verify` job**
  (restore + build, all ten test projects, the version-shape assertion) and `publish` reaches NuGet only
  through `needs: verify`. It then packs every package at the tag version and publishes to NuGet; a version
  that already exists on nuget.org fails the run instead of being skipped. Never tag by hand.
- A baseline holds the **last release's** surface and the script enforces it: when the committed baselines
  match the worktree but differ from the last tag (someone refreshed them mid-cycle), it explains the drift,
  warns on `-DryRun` and refuses to release - that is exactly how the API delta stayed invisible and
  v2026.4.7 shipped wrongly. Restore first: `git checkout <last-tag> -- api/`.
- Already published the wrong version? Unlisting (never deleting) is the remedy - NuGet versions cannot be
  reused - but **this repository has no unlist workflow**: it was removed, because nuget.org pins a trusted
  publishing policy to the workflow *file name*, so it could only ever run against a separate
  `unlist.yml`/`Unlist` policy that does not exist. Unlist by hand on nuget.org (and give the GitHub Release a
  "Superseded: use <version>" note); restoring the workflow is a three-step job documented in
  `docs/releasing.md` (section "Restoring the unlist workflow").
- **Releases are cut from `main`** — CI on `main` green first. The script refuses to tag any other branch
  (`-AllowBranch` is the deliberate override for re-tagging cases).
- Version rule: `<four-digit year>.<x>.<y>` — `x` bumps (and `y` resets to 0) when the public API changed;
  `y` bumps when it did not.
- **The version rule cannot see the wire.** `api-surface` diffs assemblies, so a change to the JSON a peer reads
  (identical C# surface, different bytes) is invisible to it and would ship as a `y` bump with nothing announcing it.
  `tests/PicoJsonRpc.Tests/Fixtures/wire-v1.ndjson` is a golden snapshot of 12 frames of the JSON-RPC encoder's output
  - the corpus `WireSnapshotTests` names, not a sweep of the encoder (`"result":null`, the quoted-string `params`
  fallback, surrogate substitution and multi-line normalisation are pinned by `WireConformanceTests` instead) -
  compared byte for byte; it is marked `-text` in `.gitattributes` so no checkout rewrites its LF terminators.
  If you change the emitted bytes: regenerate the snapshot in the **same** commit, put a `wire-change:` line in the
  commit message, and ship a consumer pinned to the old shape (`PicoAgent.Binding`) in lockstep.
- **What that snapshot enforces, exactly.** It forces a **file edit and a visible diff**: the bytes cannot move
  without the fixture moving in the same commit, so a wire change is always reviewable. It **does not force a
  version bump** - the version comes from the API diff only. `scripts/release.ps1 -DryRun` prints `wire hash` and
  `wire changed`; it measures the wire *before* it asks for the next version (fix round 1 moved it there), but it is
  warn-only and does not feed the version, the release workflow's `verify` job has no wire step at all (it just runs
  the tests), and a hand `git tag` skips the script entirely while still passing `verify`. So the `wire-change:` line
  and the lockstep release are **discipline, checked by a human** - nothing in this repository can fail a release
  that changed the bytes and bumped only `y`.

### Documentation is part of the release, not a follow-up

The API diff the script prints is also the list of documents that may now be wrong. Before tagging, walk it:

- **READMEs in this repository** - the root README in all ten languages, and the per-package `src/**/README.md`.
  A changed option, default or behaviour that a README describes has to move in the same release. `PicoNode.Docs.Tests`
  catches the measurable half (counts, block coverage, claims that can be derived from the tree); it cannot catch a
  sentence that is merely out of date.
- **The PicoHex skills** - `picohex-picorpc`, `picohex-picoweb`, `picohex-picodi`, `picohex-readme-standard`, ... live
  **outside this repository**, in the `picohex-skills` package, so nothing here fails when they go stale. They are what
  an agent or a partner reads *instead of* the source, which makes a stale skill worse than a stale README. Walk the
  diff member by member and ask of each one: does a skill describe it?
  - The **wire format** counts as a described API: v2026.6.0 had to rewrite `picohex-picorpc`'s "params/result are
    string-embedded JSON" section, because a partner following it would have called `JSON.parse(params)` on a value
    that is no longer quoted.
  - **Semantics** count too, not just members: `AcceptFaultBackoff = 0` changed meaning (1 ms), and the crash-backoff
    counters moved from the channel to the lifecycle.
- **The release notes** - `docs/release-notes/` gets an entry per release: the API delta, the wire delta, the breaking
  changes, and the limitations left open.
- **Analyzer release notes** - a new or changed `DiagnosticDescriptor` needs its `AnalyzerReleases.Unshipped.md` row.

**The walk is a check, not a principle** (`picohex-release`, step 4 as of v2026.11). The delta the
script prints is the checklist: every `+N`/`-M` line per package is one item, and the removals are the
breaking ones.

- **Forward `grep`:** each affected package's skill, for every changed member name. Not found ⇒ edit it;
  found but worded differently ⇒ edit it.
- **Reverse `grep`:** every API the skill *claims* exists. One this repository deleted or reshaped is the
  dangerous case, because the reader codes against it. (Checked at v2026.6.0: `picohex-perf` cites
  `ChunkTerminatorBytes` at `HttpResponseSerializer.cs:8`/used `:109` and `ContinueResponse` at
  `Http1ConnectionProcessor.cs:5`/used `:121` - all four still true, so no reverse fix was owed.)
- **Rule:** the API set a skill describes must equal what `api/<PackageId>.public.txt` exposes. An
  unwritten addition and an unremoved removal are both stale.

A release whose diff is documented nowhere is not finished; it is just uploaded.

### Performance numbers are judged, not printed

`picohex-perf` owns the rules (what to measure, when to stop, which optimisations actually paid) and
`picohex-benchmark` owns the API. Two of them bind this repository's own measurements:

- **Pre-register the prediction, and treat <15 % as no result.** A measurement that recovers less than
  the noise band is not evidence of a win; say "no measurable difference" instead of a small percentage.
- **`Speedup > 1` is not the verdict** - a ratio inside the 5 % dead band, or an interval containing
  parity, is inconclusive. This repository's perf work does not use PicoBench (`PerfHarness` is
  hand-rolled), so the equivalent discipline is: a floor is a *bound*, an allocation ceiling carries
  head-room, and a single run is not a number (T1.7/T1.8/T1.9 measured 5x100 with a warm-up, and T4.5's
  floors scale by a measured calibration rather than trusting the wall clock).

### Serialization belongs to PicoSerDe, and `PicoJetson` is its JSON format

`PicoJetson` is not a parallel library to PicoSerDe - it **is** PicoSerDe's JSON format, so the
`picohex-picoserde` rules and its shared `PicoSerDe.Core` types apply here. That is how
`PicoJetson.JsonIgnoreCondition` disappearing in 2026.13 reached this repository: the enum moved to
`PicoSerDe.Core.PicoIgnoreCondition`, and the bump did not compile until the reference followed it.

- **No `System.Text.Json` in `src/`.** The grep is the gate:
  `grep -rn "System.Text.Json\|Utf8JsonWriter\|Utf8JsonReader\|JsonDocument" src/ --include=*.cs`
  must be empty. `picohex-picorpc` is described as "System.Text.Json-free IPC" precisely because the
  wire codec is hand-written on `PicoJetson.JsonWriter`/`JsonReader`.
- **`JsonWriter` is a `ref struct`**: a helper that takes it **by value** writes into a copy whose
  commit never runs - the bytes are silently gone and nothing throws. Every helper takes
  `ref JsonWriter`; `using var` cannot be used for it (CS1657), so commit with `try/finally` +
  `Dispose()`, and remember that closing a container - not `Flush()` - is what closes the document.
- Prefer `Write*Property(name, value)`: one call writes name + colon + value, so the "name written,
  value pending" state cannot strand.

## Working rules

- TDD, RED first: a behavioural fix needs a test that fails before the change and passes after.
- New tests must be mutation-proven: pick a targeted mutant and show the new test fails on it. Re-derive the
  result yourself (never take a claim on faith), and measure over several independent runs.
- Concurrency claims must hold under a **1–2 CPU** affinity mask (a CI-runner proxy), not just on a roomy
  machine. Probes must be core-count independent — fixed racer counts plus an explicit yielding seam, never
  `clamp(2 × cores, …)`.
- Mixed line endings: the worktree can be CRLF while individual files are LF (`core.autocrlf=true`). Mutation
  probes must edit byte-safely (snapshot, restore, verify `sha256`) and be CRLF-aware.
- Keep the tree clean (`git status --porcelain` empty), delete one-shot probes, and keep scratch files out of
  the repository.
- Small, focused, conventional commits: `fix(web):`, `test(http):`, `docs(spec):`, `build(api):`, `chore(release):`.

## Environment quirks (this developer machine)

- Commit before you use a throwaway branch for a probe. `git checkout -B probe && git add -A && git commit`
  sweeps uncommitted work into the probe commit, and deleting the branch deletes that work (recoverable via
  `git reflog` + `git fsck` while the commit is unreferenced, but not worth the scare).
- github.com is reachable either through the proxy configured in `.gitconfig`
  (`http.https://github.com.proxy`) or directly. Sometimes only one works, and the
  symptom is `Failed to connect to github.com port 443`. Start with `git ls-remote origin main`; if that
  fails, retry with `-c http.https://github.com.proxy=` (and vice versa) before blaming credentials.
- `curl` does not read git's proxy setting, so `curl https://api.nuget.org/...` returns an empty response while
  `dotnet restore` works: add `-x socks5h://127.0.0.1:10808`. After a fresh publish, `dotnet restore` can still
  report NU1102 for the new version until `dotnet nuget locals http-cache --clear` - the version list is cached
  locally, and the CDN's index lags a push by minutes even after the nuspec is served.
- NuGet reads are served by a lagging mirror, so a version can be missing from the flat container minutes
  after a successful publish. Confirm publications through the search index instead:
  `https://azuresearch-usnc.nuget.org/query?q=packageid:<Id>&prerelease=true`.
- Tracked one-shot scripts are guarded by `CiWorkflowTests.Tracked_python_scripts_are_referenced`: every
  `*.py` file in the tree must be named by a CI workflow, a `scripts/*.ps1` or this file. Delete it or wire it.
  There is no `Fixtures/` carve-out any more: a Python fixture counts too, so wire it by name or delete it.
- Any workflow that runs `PicoJsonRpc.Tests` must install `node` first (pinned major, `actions/setup-node@v4`);
  guarded by `CiWorkflowTests.Every_workflow_that_runs_the_rpc_suite_installs_node`. Without it the 21
  child-process tests skip loudly in CI — a green run covering a third less than it claims.
