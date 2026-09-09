#!/usr/bin/env bash
# Build the vendored RecompOne, and harvest from upstream when asked.
#
# tools/RecompOne is VENDORED: its sources are tracked in this repository, so a
# fresh clone already has a working recompiler and an edit made inside it is a
# change to this repo like any other. It used to be a gitignored clone of an
# upstream pin with patches/recompone/*.patch replayed over it on every run.
#
# Why that changed. `git apply` matches text context; it does not know what
# upstream changed. So upstream's Rider reformat (410f0d4) broke 28 of the 39
# patches at once, and would have broken them again on every future pin move,
# because a diff is written against context that has to still be there. A
# vendored fork gets a real merge base instead, and a three-way merge reasons
# about changes rather than appearances: the reformat is absorbed once, as a
# commit. Measured at the time of vendoring, a single upstream commit
# (67fc37c, 23 files) cost 23 conflict hunks to take as a merge, against
# hand-authoring a ~700 line patch to be carried for the life of the project.
#
# patches/recompone/*.patch is KEPT and is no longer replayed. Each one is a
# commit in the fork's history (tools/RecompOne.git, gitignored) and the diffs
# stay as the record of what the port changed and why -- and as the way back,
# since `--sync-upstream` rebuilds the checkout from upstream if you ever want
# to start over.
#
# Upstream rejects AI-authored pull requests. Fixes go upstream as issues, never
# as PRs, and only when the user writes them.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS="$ROOT/tools/RecompOne"
FORK_GIT="$ROOT/tools/RecompOne.git"
UPSTREAM_URL="https://github.com/BlackLabelHQ/RecompOne.git"
SIGS="$TOOLS/RecompOne.Recompiler/AutoConfigure/signatures/psyq.json"

fork() { git --git-dir="$FORK_GIT" --work-tree="$TOOLS" "$@"; }

usage() {
    cat <<'USAGE'
usage: setup_tools.sh [--sync-upstream] [--signatures] [--no-build]

  (no flags)        build the vendored recompiler
  --signatures      fetch AutoConfigure/signatures/psyq.json (15.7 MB, gitignored;
                    only the standalone --autoconfigure command reads it)
  --sync-upstream   fetch upstream and start a three-way merge into the vendored
                    tree, on a branch, for you to resolve
  --no-build        skip the build
USAGE
}

SYNC=0 SIGNATURES=0 BUILD=1
for arg in "$@"; do
    case "$arg" in
        --sync-upstream) SYNC=1 ;;
        --signatures)    SIGNATURES=1 ;;
        --no-build)      BUILD=0 ;;
        -h|--help)       usage; exit 0 ;;
        *) echo "unknown argument: $arg" >&2; usage >&2; exit 2 ;;
    esac
done

if [ ! -d "$TOOLS" ]; then
    echo "tools/RecompOne is missing. It is tracked in this repository -- restore it" >&2
    echo "with 'git checkout -- tools/RecompOne' rather than cloning upstream." >&2
    exit 1
fi

# The fork repository is what makes upstream reachable: it holds the port's 39
# patches as commits, the merge, and the upstream remote. It is gitignored and
# rebuildable, so a fresh clone has none -- create it on demand.
ensure_fork() {
    if [ -d "$FORK_GIT" ]; then
        fork fetch --quiet origin || true
        return
    fi
    echo "==> creating the fork repository (tools/RecompOne.git)"
    git clone --quiet --bare "$UPSTREAM_URL" "$FORK_GIT"
    git --git-dir="$FORK_GIT" config core.bare false
    git --git-dir="$FORK_GIT" config core.worktree "$TOOLS"
    git --git-dir="$FORK_GIT" config remote.origin.fetch '+refs/heads/*:refs/remotes/origin/*'
    fork fetch --quiet origin
    # The vendored tree is the port's; record it as a commit whose parent is the
    # upstream commit it was merged from, so a merge has a real base.
    base="$(cat "$TOOLS/UPSTREAM")"
    fork checkout --quiet -B vendored "$base"
    fork add -A
    fork -c user.name="vendored" -c user.email="vendored@localhost" \
         commit --quiet -m "The vendored tree, as tracked in the game repository"
}

if [ "$SIGNATURES" = 1 ]; then
    echo "==> fetching PSY-Q signatures"
    ensure_fork
    mkdir -p "$(dirname "$SIGS")"
    fork show "origin/master:RecompOne.Recompiler/AutoConfigure/signatures/psyq.json" > "$SIGS"
    echo "    $(du -h "$SIGS" | cut -f1) -> ${SIGS#$ROOT/}"
fi

if [ "$SYNC" = 1 ]; then
    ensure_fork
    base="$(cat "$TOOLS/UPSTREAM")"
    head="$(fork rev-parse origin/master)"
    echo "==> vendored tree is at upstream $base"
    echo "==> upstream master is at $head"
    if [ "$base" = "$head" ]; then
        echo "    already current; nothing to harvest."
        exit 0
    fi
    echo "    $(fork rev-list --count "$base..$head") commit(s) to consider:"
    fork log --oneline "$base..$head" | sed 's/^/      /'
    echo
    echo "==> merging upstream into the vendored tree"
    fork checkout --quiet -B vendored
    fork add -A
    fork -c user.name="vendored" -c user.email="vendored@localhost" \
         commit --quiet -m "The vendored tree, as tracked in the game repository" || true
    set +e
    fork merge --no-commit origin/master
    rc=$?
    set -e
    cat <<EOF

The merge is in tools/RecompOne, with conflicts left in the files.

  resolve:  \$EDITOR the conflicted files, then
            git --git-dir=tools/RecompOne.git --work-tree=tools/RecompOne add <file>
  finish:   echo $head > tools/RecompOne/UPSTREAM
            bash scripts/setup_tools.sh          # build it
            # then run the game and check: 144 fps drawn at 20 ticks/s, the
            # agent beacon reaching an fdat overlay with a real position
  abandon:  git --git-dir=tools/RecompOne.git --work-tree=tools/RecompOne \\
                merge --abort && git checkout -- tools/RecompOne

Take one upstream commit rather than all of them with:
  git --git-dir=tools/RecompOne.git --work-tree=tools/RecompOne cherry-pick -n <sha>
EOF
    exit $rc
fi

if [ "$BUILD" = 1 ]; then
    echo "==> building recompiler"
    dotnet build "$TOOLS/RecompOne.Recompiler" -c Release
fi

echo "done."
