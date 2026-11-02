# Releasing PicoNode

**`main` is the only release branch.** Every package published to NuGet is built from a commit on
`main`, and the only supported way to build release artifacts is `tools/release-build.sh`. This page is
the short version; the version rule, baselines and the wire snapshot live in `AGENTS.md` § Releasing and
the comment-based help at the top of `scripts/release.ps1`.

## Why the rule exists

Someone built a release from a side branch. The packages were **69 commits behind `main`**, and nothing
in them said so: a `.nupkg` records the *pack* time, not the age of the code inside it. So the artifact
looked newer than everything already published — a fresh timestamp, a new version — while the code inside
it was 69 commits out of date. The version number, the timestamp and the package id agreed with each
other, and all three pointed the wrong way.

A branch is not a slower `main`; it is a different line with its own distance from the trunk. Nothing in
the build output records that distance, so a side-branch release cannot be detected downstream — it can
only be prevented upstream. Hence a guard rather than a convention: `tools/release-build.sh` refuses to
build anywhere but `main`, and `scripts/release.ps1` refuses to tag anywhere but `main` (its
`-AllowBranch` switch is the deliberate, self-announcing override for re-tagging a historical commit).

## Three self-checks before you build

1. **You are on `main`, and the tree is clean.** `git rev-parse --abbrev-ref HEAD` prints `main`;
   `git status --porcelain` prints nothing. Uncommitted edits produce artifacts that exist in no commit
   and can never be rebuilt — the script enforces this (exit 2 / exit 3), but check it yourself first.
2. **You are on `origin/main`, not a local branch that drifted.** `git fetch origin` then
   `git rev-list --left-right --count origin/main...HEAD` must print `0	0`. A nonzero right column means
   you have unpushed commits (nobody else can rebuild what you ship); a nonzero left column means you are
   behind (you are about to publish a stale line — the failure this whole page exists to stop).
3. **CI is green on the exact commit you are about to build.** Take the full sha the script prints in its
   `HEAD` line (step 3 of its output) and confirm that sha — not "the last run", not `main` in general —
   passed `ci.yml`: `gh run list --branch main --limit 5` and match the sha. A release from a commit whose
   CI is red or still running is untested code with a version number on it.

## Build: `tools/release-build.sh`

```bash
bash tools/release-build.sh                      # artifacts -> $TMPDIR/rel-<short-sha>, outside the repo
OUT=/var/tmp/release bash tools/release-build.sh # somewhere else (must be outside the repository)
VERSION=2026.10.0 bash tools/release-build.sh    # pin the package version
```

Always start it with `bash` (not `./tools/release-build.sh`): the work tree is checked out with
`core.autocrlf=true`, so the file can carry CRLF endings, and `bash <script>` is the form that does
not depend on the shebang surviving them.
It packs the whole solution once, at one version, and prints the commit it built, the branch, the UTC
time, every `.nupkg` with its sha256, and the **primary artifact** (`PicoNode.<version>.nupkg`) with its
sha256 so a build can be compared with what NuGet received. Artifacts go outside the work tree by design,
and the script fails if that work tree moves while it is building.

| Exit | Meaning | What to do |
|-----:|---------|------------|
| `0` | Built from `main`, repository untouched | Continue to the release |
| `1` | The build failed, or packed a different number of packages than `src/` declares packable | Fix the build; never release from a broken commit |
| `2` | HEAD is not branch `main` (or is detached) | Merge into `main` and build there |
| `3` | The work tree is dirty (the first three paths are printed) | Commit or `git stash -u`, then rebuild |
| `4` | `OUT` resolves inside the repository | Point `OUT` somewhere outside the work tree |
| `5` | The work tree changed *while* the build ran (a concurrent edit, or a path the build leaked) | Re-run in a quiet workspace: those packages cannot be tied to a commit |

## Cutting the release

The local build proves the commit packs; it publishes nothing. The tag is the release trigger:

```bash
./scripts/release.ps1 -DryRun   # last release, API changed, wire hash, next version
./scripts/release.ps1 -Push     # refreshes api/, commits chore(release): vX.Y.Z, tags, pushes
```

`.github/workflows/release.yml` then gates publication on its `verify` job (restore + build, all ten test
projects, the version-shape assertion) — `publish` reaches NuGet only through `needs: verify`. Never tag
by hand, and never `dotnet nuget push` from a workstation.

## If the guard refuses

It is telling you the artifact would be unattributable, not that your code is wrong. The fix is always to
move the work onto `main` (merge/rebase, then rebuild) — not to bypass the script. If you believe you need
an exception, get it agreed explicitly and record why in the commit message; a released package with a
wrong provenance stays on nuget.org forever, because NuGet versions can be unlisted but never reused.

## If you published the wrong version

There is **no unlist workflow in this repository**, deliberately. Unlisting hides a version from search and
from resolution while consumers that pin it keep restoring; it never deletes, because NuGet versions cannot be
reused. The `unlist.yml` workflow that used to do this was removed on the owner's decision - withdrawal is not a
capability this repository wires up - and it could not have worked anyway. nuget.org matches an OIDC token
against a trusted publishing policy that pins the **workflow file name**, so `unlist.yml` needed its own policy
(workflow file `unlist.yml`, scope `Unlist`, environment empty); the policy registered for `release.yml` does
not admit it, and a push-scoped key is exactly what made the first attempt answer 403 for every package. A
switch that cannot move is worse than no switch: it looks like a remedy while it always fails.

For now, unlist by hand on nuget.org, and put a "Superseded: use <version>" note on the GitHub Release for the
bad version.

### Restoring the unlist workflow

If withdrawal ever becomes a wanted capability, all three steps - in this order:

1. **Bring the file back from git history:**

   ```bash
   git log --all --oneline -- .github/workflows/unlist.yml   # find the commit that deleted it
   git show <deleting-sha>^:.github/workflows/unlist.yml > .github/workflows/unlist.yml
   ```

2. **Register a trusted publishing policy for it on nuget.org** - publisher `GitHub Actions`, workflow file
   **`unlist.yml`**, scope **`Unlist`**, environment **empty**. The `release.yml` policy does not cover it: the
   policy is pinned to the file name, and an empty environment is not the `release` environment that
   `release.yml` declares.

3. **Run it:** `gh workflow run unlist.yml -f version=X.Y.Z -f confirm=unlist`.

Step 2 is the one that gets skipped, and skipping it is why the workflow was dead: without its own `Unlist`
policy the OIDC handshake mints a push-scoped key, and every package answers 403.
