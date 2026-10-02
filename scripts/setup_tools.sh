#!/usr/bin/env bash
# Build RecompOne, and manage the standalone fork it is a subtree of.
#
# tools/RecompOne is a git SUBTREE of the fork Voicedrew11/verdite-recompone,
# taken with --squash, at the same prefix. Its sources are tracked in this
# repository, so a fresh clone already has a working recompiler and an edit made
# inside it is a change to this repo like any other. It used to be a gitignored
# clone of an upstream pin with patches/recompone/*.patch replayed over it on
# every run.
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
# patches/recompone/*.patch is KEPT and is no longer replayed: the diffs stay
# as the record of what the port changed and why.
#
# Harvesting upstream no longer happens here. It happens in a working clone of
# the fork, where the fork's harvest_upstream.sh starts the merge; this checkout
# then takes the result with --pull-fork.
#
# Upstream rejects AI-authored pull requests. Fixes go upstream as issues, never
# as PRs, and only when the user writes them. Nothing goes upstream from here.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS="$ROOT/tools/RecompOne"
# Defined once; override with VERDITE_FORK_URL / VERDITE_FORK_BRANCH (a local
# path is handy while testing).
FORK_URL="${VERDITE_FORK_URL:-https://github.com/Voicedrew11/verdite-recompone.git}"
FORK_BRANCH="${VERDITE_FORK_BRANCH:-main}"
SIGS="$TOOLS/RecompOne.Recompiler/AutoConfigure/signatures/psyq.json"

usage() {
    cat <<'USAGE'
usage: setup_tools.sh [--signatures] [--pull-fork [ref]] [--push-fork] [--no-build]

  (no flags)        build the recompiler
  --signatures      fetch AutoConfigure/signatures/psyq.json (15.7 MB, gitignored;
                    only the standalone --autoconfigure command reads it)
  --pull-fork [ref] pull the RecompOne fork into tools/RecompOne as one squash
                    subtree commit; ref defaults to main
  --push-fork       push tools/RecompOne's subtree commits to the RecompOne fork;
                    refuses if one of them also touches files outside it
  --sync-upstream   removed; prints where harvesting moved and exits 2
  --no-build        skip the build
USAGE
}

# `git subtree` ships as a separate package on some distros (Fedora:
# git-subtree), and it prints usage to stderr with exit 129 when present, so
# test the output rather than the status.
require_subtree() {
    help="$(git subtree -h 2>&1 || true)"
    case "$help" in
        *"usage: git subtree"*) ;;
        *) echo "git subtree is not installed. On Fedora install git-subtree." >&2
           exit 1 ;;
    esac
}

SYNC=0 SIGNATURES=0 BUILD=1 PULL=0 PUSH=0 PULL_REF="$FORK_BRANCH"
while [ $# -gt 0 ]; do
    case "$1" in
        --signatures)    SIGNATURES=1 ;;
        --pull-fork)
            PULL=1
            case "${2:-}" in
                ""|-*) ;;
                *) PULL_REF="$2"; shift ;;
            esac
            ;;
        --push-fork)     PUSH=1 ;;
        --sync-upstream) SYNC=1 ;;
        --no-build)      BUILD=0 ;;
        -h|--help)       usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

if [ ! -d "$TOOLS" ]; then
    echo "tools/RecompOne is missing. It is a subtree of $FORK_URL -- restore it" >&2
    echo "with 'git subtree pull --prefix=tools/RecompOne --squash $FORK_URL $FORK_BRANCH'." >&2
    exit 1
fi

if [ "$SYNC" = 1 ]; then
    cat >&2 <<EOF
--sync-upstream has moved. Upstream harvesting happens in a working clone of the
fork ($FORK_URL), not in this checkout:

  1. clone $FORK_URL and add upstream BlackLabelHQ/RecompOne in it
  2. run its harvest_upstream.sh, resolve the merge, push the fork
  3. back here, run: bash scripts/setup_tools.sh --pull-fork
EOF
    exit 2
fi

if [ "$SIGNATURES" = 1 ]; then
    upstream="$(cat "$TOOLS/UPSTREAM")"
    url="https://raw.githubusercontent.com/BlackLabelHQ/RecompOne/$upstream/RecompOne.Recompiler/AutoConfigure/signatures/psyq.json"
    echo "==> fetching PSY-Q signatures (upstream $upstream)"
    mkdir -p "$(dirname "$SIGS")"
    curl -fL "$url" -o "$SIGS.part" || { rm -f "$SIGS.part"; exit 1; }
    mv "$SIGS.part" "$SIGS"
    echo "    $(du -h "$SIGS" | cut -f1) -> ${SIGS#$ROOT/}"
fi

if [ "$PULL" = 1 ]; then
    require_subtree
    if [ -n "$(git -C "$ROOT" status --porcelain --untracked-files=no)" ]; then
        echo "working tree is dirty; commit or stash before --pull-fork." >&2
        exit 1
    fi
    echo "==> pulling $FORK_URL $PULL_REF into tools/RecompOne (squash)"
    git -C "$ROOT" subtree pull --prefix=tools/RecompOne --squash "$FORK_URL" "$PULL_REF" \
        -m "Pull tools/RecompOne from the fork at $PULL_REF"
fi

if [ "$PUSH" = 1 ]; then
    require_subtree
    # A commit that touches tools/RecompOne must touch nothing else, or the
    # split carries a half-commit to the fork. Check every one since the last join.
    # The last join is the newest add/pull merge on the first-parent line: its
    # second parent is a squash commit naming tools/RecompOne.
    join=""
    while read -r c _ p2; do
        if git -C "$ROOT" log -1 --format=%B "$p2" | grep -q '^git-subtree-dir: tools/RecompOne/*$'; then
            join="$c"; break
        fi
    done < <(git -C "$ROOT" log --first-parent --merges --format='%H %P' HEAD)
    if [ -z "$join" ]; then
        echo "no subtree join found on the first-parent line; refusing to push." >&2
        exit 1
    fi
    mixed=0
    for c in $(git -C "$ROOT" rev-list --no-merges "$join..HEAD" -- tools/RecompOne); do
        if git -C "$ROOT" diff-tree --no-commit-id --name-only -r "$c" | grep -qv '^tools/RecompOne/'; then
            echo "mixed commit: $(git -C "$ROOT" log -1 --format='%h %s' "$c")" >&2
            mixed=1
        fi
    done
    if [ "$mixed" = 1 ]; then
        echo "split those into a tools/RecompOne commit and a game commit, then push." >&2
        exit 1
    fi
    echo "==> pushing tools/RecompOne to $FORK_URL $FORK_BRANCH (this publishes to the fork)"
    git -C "$ROOT" subtree push --prefix=tools/RecompOne "$FORK_URL" "$FORK_BRANCH"
fi


if [ "$BUILD" = 1 ]; then
    echo "==> building recompiler"
    dotnet build "$TOOLS/RecompOne.Recompiler" -c Release
fi

echo "done."
