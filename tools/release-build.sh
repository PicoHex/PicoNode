#!/usr/bin/env bash
#
# tools/release-build.sh -- the only supported way to build PicoNode release artifacts.
#
# Rule (docs/releasing.md): main is the only release branch. This script refuses to
# produce artifacts anywhere else, so "built from a side branch" cannot happen by
# accident. A side-branch package is stale by an unknown number of commits while its
# .nupkg timestamp is the pack time -- it looks newer than main and is older.
#
# Usage:
#   bash tools/release-build.sh                      # artifacts -> $TMPDIR/rel-<short-sha>
#   OUT=/var/tmp/release bash tools/release-build.sh # artifacts somewhere else
#   VERSION=2026.10.0 bash tools/release-build.sh    # pin the package version
#
# Exit codes:
#   0  artifacts built, repository untouched
#   1  the build failed (or packed nothing / packed fewer packages than src declares)
#   2  HEAD is not branch main (or is detached)
#   3  the work tree is dirty
#   4  OUT resolves inside the repository
#   5  the work tree changed while the build was running (concurrent edit, leaked path)
set -euo pipefail

RELEASE_BRANCH="main"
SOLUTION="PicoNode.slnx"

say() { printf '%s\n' "$*"; }
die() { local code="$1"; shift; printf '\nrelease-build: REFUSED - %s\n' "$*" >&2; exit "$code"; }

# --- R1: branch guard -------------------------------------------------------
# Releases are cut from main. A tag publishes whatever commit it points at, so the
# branch that built the bits is part of what the version number claims.
repo_root=$(git rev-parse --show-toplevel 2>/dev/null) || die 2 "not inside a git work tree"
cd "$repo_root"

head_sha=$(git rev-parse HEAD)
branch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || printf 'HEAD')

if [ "$branch" = "HEAD" ]; then
  die 2 "HEAD is detached (at $head_sha), not on '$RELEASE_BRANCH'. A detached build belongs to no branch, so nothing records how far it has drifted from the released line. Check out '$RELEASE_BRANCH' and rebuild."
fi

if [ "$branch" != "$RELEASE_BRANCH" ]; then
  die 2 "current branch is '$branch' (HEAD $head_sha), not '$RELEASE_BRANCH'. Releases are cut from '$RELEASE_BRANCH' only: a branch can be arbitrarily far behind the trunk while its packages carry a fresh timestamp, so consumers see a new version and get older code. Merge into '$RELEASE_BRANCH' and build there."
fi

# --- R2: clean tree ---------------------------------------------------------
# A release is built from a commit. Uncommitted edits produce artifacts that exist
# in no commit and can never be rebuilt.
dirty=$(git status --porcelain)
if [ -n "$dirty" ]; then
  total=$(printf '%s\n' "$dirty" | wc -l | tr -d ' ')
  say ""
  say "release-build: REFUSED - the work tree is dirty ($total path(s)); artifacts must come from a commit."
  shown=0
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    if [ "$shown" -lt 3 ]; then
      printf '  %s\n' "$line"
      shown=$((shown + 1))
    fi
  done <<< "$dirty"
  if [ "$total" -gt 3 ]; then
    printf '  ... and %s more\n' "$((total - 3))"
  fi
  printf '  commit or stash them (git stash -u) and run again\n' >&2
  exit 3
fi

# --- R3: what is being built ------------------------------------------------
# The sha is the identity of the release: it is what CI ran on and what the tag must
# point at. Printed before anything else so a failed run still names its commit.
utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
say "HEAD         : $head_sha"
say "branch       : $branch"
say "UTC time     : $utc"

# --- R4: artifacts land outside the repository ------------------------------
# Nothing this script produces may appear in `git status`. The default OUT is keyed
# by the commit, so the build of one commit is never confused with another's.
short_sha=$(git rev-parse --short HEAD)
OUT=${OUT:-${TMPDIR:-/tmp}/rel-$short_sha}
root_abs=$(cd "$repo_root" && pwd -P)

# Resolve OUT BEFORE creating it: a refused path must not leave a directory behind
# (an `OUT=./artifacts/probe` created and then rejected would be exactly the residue
# this section exists to prevent). `realpath -m` normalises a path that does not exist;
# when it is unavailable the check still runs on the path as given, and again on the
# directory once it exists.
if command -v realpath >/dev/null 2>&1 && realpath -m "$OUT" >/dev/null 2>&1; then
  out_abs=$(realpath -m "$OUT")
elif [ -d "$OUT" ]; then
  out_abs=$(cd "$OUT" && pwd -P)
else
  out_abs="$OUT"
fi

inside_repo() {
  case "$1" in
    "$root_abs"|"$root_abs"/*) return 0 ;;
    *) return 1 ;;
  esac
}

if inside_repo "$out_abs"; then
  die 4 "OUT ($out_abs) is inside the repository ($root_abs). Build artifacts leave the work tree; set OUT to a path outside it."
fi

mkdir -p "$OUT" || die 4 "cannot create OUT directory: $OUT"
out_abs=$(cd "$OUT" && pwd -P) || die 4 "cannot resolve OUT directory: $OUT"
inside_repo "$out_abs" && rmdir "$OUT" 2>/dev/null
if inside_repo "$out_abs"; then
  die 4 "OUT ($out_abs) is inside the repository ($root_abs). Build artifacts leave the work tree; set OUT to a path outside it."
fi

say "OUT          : $out_abs"

# --- R6: build --------------------------------------------------------------
# One solution-level pack: every project with IsPackable=true, once, at one version,
# in dependency order (project references resolve locally). That is the same package
# set the release workflow publishes; the count is cross-checked below.
pack_args=(pack "$SOLUTION" -c Release --nologo -o "$OUT")
if [ -n "${VERSION:-}" ]; then
  pack_args+=(-p:Version="$VERSION")
  say "version      : $VERSION"
else
  say "version      : (project default from Directory.Build.props)"
fi

say ""
say "== dotnet ${pack_args[*]}"
if ! dotnet "${pack_args[@]}"; then
  die 1 "dotnet pack failed - no release may be cut from a red build. Anything already written to $out_abs by this run is a PARTIAL set (pack writes packages in dependency order, so the failure came after some of them): delete $out_abs before retrying. Fix the build, do not release from a broken commit."
fi

# --- R5: report the artifacts ----------------------------------------------
shopt -s nullglob
nupkgs=("$OUT"/*.nupkg)
shopt -u nullglob

[ "${#nupkgs[@]}" -eq 0 ] && die 1 "dotnet pack reported success but wrote no .nupkg into $out_abs"

# The release workflow asserts the same invariant: a packable project that silently
# produced no package would otherwise ship as a version bump with nothing in it.
expected=0
while IFS= read -r proj; do
  if grep -q '<IsPackable>true</IsPackable>' "$proj"; then
    expected=$((expected + 1))
  fi
done < <(find src -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*')
if [ "$expected" -ne "${#nupkgs[@]}" ]; then
  die 1 "packed ${#nupkgs[@]} package(s) but src/ declares $expected packable project(s) - a package is missing or an extra one slipped in."
fi

primary=""
for f in "${nupkgs[@]}"; do
  if [[ "$(basename "$f")" =~ ^PicoNode\.[0-9] ]]; then
    primary="$f"
    break
  fi
done
[ -z "$primary" ] && primary="${nupkgs[0]}"

say ""
say "== artifacts ($out_abs)"
for f in "${nupkgs[@]}"; do
  digest=$(sha256sum "$f" | cut -d' ' -f1)
  mark=" "
  [ "$f" = "$primary" ] && mark="*"
  printf '  %s %s  %s\n' "$mark" "$digest" "$(basename "$f")"
done

say ""
say "primary artifact : $(basename "$primary")"
say "sha256           : $(sha256sum "$primary" | cut -d' ' -f1)"

# --- R4 (post-check): the tree the artifact came from did not move -----------
# R2 proved this tree was clean before the build, so anything dirty now changed
# WHILE the build ran. That is normally not the pack's own doing - it writes only to
# bin/obj (ignored) and to OUT - but either way the packages were compiled from a
# working copy that no commit describes, so they cannot be tied to $head_sha and
# cannot be released.
after_status=$(git status --porcelain)
if [ -n "$after_status" ]; then
  say ""
  say "release-build: REFUSED - the work tree changed while the build was running."
  say "  It was clean before the build (R2), so these paths appeared or changed during it"
  say "  - a concurrent editor, or a build step that leaked a tracked path:"
  printf '%s\n' "$after_status" | head -5
  say "  The packages in $out_abs were compiled from that moving tree and cannot be tied"
  say "  to $head_sha. Re-run when the workspace is quiet."
  exit 5
fi

say "work tree        : clean after the build (git status --porcelain is empty)"
say ""
say "Built $head_sha from $RELEASE_BRANCH at $utc."
say "Next: docs/releasing.md, then scripts/release.ps1 -DryRun."
