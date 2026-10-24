# AGENTS.md

Orientation for agents (and humans) working in this repository. Keep it short and factual, and add to it
when you learn something the hard way. Note that `**/superpowers/` is gitignored (`.gitignore:416`), so the
SDD ledgers and design plans under `.superpowers/` and `docs/superpowers/` never leave this machine: anything
that must outlive the workspace belongs in a tracked doc (or in a commit message) instead.

## Build & test

- Whole solution (samples included): `dotnet build PicoNode.slnx -c Release --nologo`.
- One project: `dotnet test tests/<Project>/<Project>.csproj -c Release`.
- **Never pass `--nologo` to `dotnet test`** on SDK 10 — it makes the run report zero tests.
  `dotnet build --nologo` is fine.
- Runner arguments go **after** `--` (TUnit), e.g. `dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -c Release -- --treenode-filter '/*/*/Name'`.
- TUnit exits **2** (not 1) on failure and colourises its output: strip ANSI before matching.
- NativeAOT check: `scripts/test-aot-publish.ps1 -RuntimeIdentifier linux-x64` (CI runs it for the linux RIDs).
- README samples: `dotnet test tests/PicoNode.Docs.Tests/PicoNode.Docs.Tests.csproj` compiles the README's
  rate-limiting samples and fails when a README drifts from the marked region in `ReadmeSamples.cs` —
  change a sample in both places, in all ten READMEs.
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
- `./scripts/release.ps1 -Push` refreshes the baselines, commits `chore(release): vX.Y.Z`, creates an
  annotated tag and pushes branch + tag.
- **The tag is the release trigger**: `.github/workflows/release.yml` gates the publish on a **`verify` job**
  (restore + build, all ten test projects, the version-shape assertion) and `publish` reaches NuGet only
  through `needs: verify`. It then packs every package at the tag version and publishes to NuGet; a version
  that already exists on nuget.org fails the run instead of being skipped. Never tag by hand.
- A baseline holds the **last release's** surface and the script enforces it: when the committed baselines
  match the worktree but differ from the last tag (someone refreshed them mid-cycle), it explains the drift,
  warns on `-DryRun` and refuses to release - that is exactly how the API delta stayed invisible and
  v2026.4.7 shipped wrongly. Restore first: `git checkout <last-tag> -- api/`.
- Already published the wrong version? Unlisting (never deleting) is the remedy - NuGet versions cannot be
  reused: `gh workflow run unlist.yml -f version=X.Y.Z -f confirm=unlist`. That workflow needs the API key to
  carry the **Unlist** permission as well as push (the release key currently only pushes, so unlisting is a
  manual step on nuget.org), and the GitHub Release should get a "Superseded: use <version>" note.
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
