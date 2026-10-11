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
# The patches are KEPT and are no longer replayed: the diffs stay as the record
# of what the port changed and why, in the fork, at tools/RecompOne/patches/.
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
# Verdite Core, the game-agnostic tooling, is the second subtree; same rules.
CORE_URL="${VERDITE_CORE_URL:-https://github.com/Voicedrew11/verdite-core.git}"
CORE_BRANCH="${VERDITE_CORE_BRANCH:-main}"
SIGS="$TOOLS/RecompOne.Recompiler/AutoConfigure/signatures/psyq.json"

usage() {
    cat <<'USAGE'
usage: setup_tools.sh [--signatures] [--pull-fork [ref]] [--push-fork] [--rejoin-fork]
                      [--pull-core [ref]] [--push-core] [--rejoin-core] [--no-build]

  (no flags)        build the recompiler
  --signatures      fetch AutoConfigure/signatures/psyq.json (15.7 MB, gitignored;
                    only the standalone --autoconfigure command reads it)
  --pull-fork [ref] pull the RecompOne fork into tools/RecompOne as one squash
                    subtree commit; ref defaults to main
  --push-fork       push tools/RecompOne's subtree commits to the RecompOne fork;
                    refuses a mixed commit, a commit the fork already has under
                    another hash, and a push that is not a fast-forward
  --rejoin-fork     when tools/RecompOne equals the fork, mark HEAD as that commit
                    (a two-parent rejoin), so the next split starts there
  --pull-core [ref] the same for Verdite Core into tools/verdite-core
  --push-core       the same for Verdite Core
  --rejoin-core     the same for Verdite Core
                    (DRY_RUN=1 with either push: report what it would push, and stop)
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

SYNC=0 SIGNATURES=0 BUILD=1 PULL=0 PUSH=0 REJOIN=0 PULL_REF="$FORK_BRANCH"
CORE_PULL=0 CORE_PUSH=0 CORE_REJOIN=0 CORE_REF="$CORE_BRANCH"
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
        --rejoin-fork)   REJOIN=1 ;;
        --pull-core)
            CORE_PULL=1
            case "${2:-}" in
                ""|-*) ;;
                *) CORE_REF="$2"; shift ;;
            esac
            ;;
        --push-core)     CORE_PUSH=1 ;;
        --rejoin-core)   CORE_REJOIN=1 ;;
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

# pull_subtree PREFIX URL REF NAME
pull_subtree() {
    require_subtree
    if [ -n "$(git -C "$ROOT" status --porcelain --untracked-files=no)" ]; then
        echo "working tree is dirty; commit or stash before pulling $1." >&2
        exit 1
    fi
    echo "==> pulling $2 $3 into $1 (squash)"
    git -C "$ROOT" subtree pull --prefix="$1" --squash "$2" "$3" \
        -m "Pull $1 from $4 at $3"
}

# split_subtree PREFIX: `git subtree split` of HEAD, printed; dies loudly.
#
# The split reads every rejoin in the history and caches the shared commit each
# names; two naming one commit kill it ("cache for X already exists!"). The
# one-parent rejoins of 2026 (131853f, 447557c) name the commits a two-parent
# rejoin made later at the same point would name too, so for the split only, each
# rejoin older than another naming the same commit is replaced by a copy without
# the trailers, through replace refs in a namespace of its own (GIT_REPLACE_REF_BASE),
# removed after. Nothing in the history changes.
split_subtree() {
    local prefix="$1" ns="refs/subtree-split-$$/" c b s seen=" " out rc
    while read -r c; do
        b="$(git -C "$ROOT" log -1 --format=%B "$c")"
        grep -q "^git-subtree-dir: $prefix/*\$" <<<"$b" || continue
        s="$(sed -n 's/^git-subtree-split: *//p' <<<"$b" | head -1)"
        [ -n "$s" ] || continue
        if [[ "$seen" == *" $s "* ]]; then
            local copy
            copy="$(GIT_AUTHOR_NAME="$(git -C "$ROOT" log -1 --format=%an "$c")" \
                GIT_AUTHOR_EMAIL="$(git -C "$ROOT" log -1 --format=%ae "$c")" \
                GIT_AUTHOR_DATE="$(git -C "$ROOT" log -1 --format=%ad --date=raw "$c")" \
                GIT_COMMITTER_NAME="$(git -C "$ROOT" log -1 --format=%cn "$c")" \
                GIT_COMMITTER_EMAIL="$(git -C "$ROOT" log -1 --format=%ce "$c")" \
                GIT_COMMITTER_DATE="$(git -C "$ROOT" log -1 --format=%cd --date=raw "$c")" \
                git -C "$ROOT" commit-tree "$c^{tree}" $(git -C "$ROOT" log -1 --format=%P "$c" | sed 's/[^ ]*/-p &/g') \
                    -m "$(grep -v '^git-subtree-' <<<"$b")")"
            git -C "$ROOT" update-ref "${ns}$c" "$copy"
            echo "    (split: hiding the older rejoin $(git -C "$ROOT" log -1 --format='%h %s' "$c" | cut -c1-70))" >&2
        fi
        seen+="$s "
    done < <(git -C "$ROOT" log --format=%H --grep='^git-subtree-mainline:' HEAD)
    local err; err="$(mktemp)"
    out="$(GIT_REPLACE_REF_BASE="$ns" git -C "$ROOT" subtree split --prefix="$prefix" HEAD 2>"$err")"; rc=$?
    git -C "$ROOT" for-each-ref --format='delete %(refname)' "$ns" | git -C "$ROOT" update-ref --stdin
    local split; split="$(tail -1 <<<"$out")"
    if [ $rc -ne 0 ] || ! git -C "$ROOT" rev-parse -q --verify "$split^{commit}" >/dev/null; then
        echo "git subtree split of $prefix failed:" >&2
        tr '\r' '\n' < "$err" | grep -v '^[0-9]*/[0-9]* ' | tail -5 >&2
        rm -f "$err"; exit 1
    fi
    rm -f "$err"
    echo "$split"
}

# push_subtree PREFIX URL BRANCH
#
# The push is `git subtree split` and a plain push of the result, checked first;
# it refuses rather than publishing something wrong. Every check is made on what
# the split would actually publish (the shared branch..the split), each commit
# found again here by its author, author time and subject:
#
# - A commit the shared branch already has under another hash is refused. The
#   split rebuilds history the joins do not reach, and from a branch that
#   merged others it rebuilt 56 commits of tools/RecompOne from before the
#   subtree existed, with the trees already equal (SHARING.md, 2026-10-11).
# - A commit that touches the prefix must touch nothing else here, or the split
#   carries a half-commit to the shared repo. This is the original of each
#   commit to be pushed, wherever it sits: a pull merged after it does not hide
#   it, as "since the last join" did.
# - The shared branch must be an ancestor of the split, or the push is not a
#   fast-forward: the shared repo has commits this copy lacks, so pull first.
# - A one-parent commit carrying a rejoin's trailers since the last joins is
#   refused: git subtree takes it for a rejoin and the next pull fails with
#   "Needed a single revision" (RECOMPONE_FORK.md, "The pull that found a
#   one-parent rejoin"). The joins are every pull merge in HEAD's history, on any
#   parent line, not only the first: a pull made on a branch later merged into
#   another sits on that merge's second parent.
#
# Nothing is pushed when the prefix's tree already equals the shared branch's.
# With DRY_RUN=1 it reports what it would push and stops.
push_subtree() {
    require_subtree
    local prefix="$1" url="$2" branch="$3"
    local joins=() c p1 rest p bad=0
    while read -r c p1 rest; do
        for p in $rest; do
            if git -C "$ROOT" log -1 --format=%B "$p" | grep -q "^git-subtree-dir: $prefix/*\$"; then
                joins+=("$c"); break
            fi
        done
    done < <(git -C "$ROOT" log --merges --format='%H %P' HEAD)
    if [ ${#joins[@]} -eq 0 ]; then
        echo "no $prefix subtree join found in HEAD's history; refusing to push." >&2
        exit 1
    fi
    while read -r c p1 rest; do
        [ -n "$c" ] && [ -z "$rest" ] || continue
        local body; body="$(git -C "$ROOT" log -1 --format=%B "$c")"
        if grep -q "^git-subtree-mainline:" <<<"$body" && grep -q "^git-subtree-dir: $prefix/*\$" <<<"$body"; then
            echo "one-parent rejoin: $(git -C "$ROOT" log -1 --format='%h %s' "$c")" >&2
            bad=1
        fi
    done < <(git -C "$ROOT" log --format='%H %P' HEAD --not "${joins[@]}")

    local remote split
    git -C "$ROOT" fetch -q "$url" "$branch"
    remote="$(git -C "$ROOT" rev-parse FETCH_HEAD)"
    if [ "$(git -C "$ROOT" rev-parse "HEAD:$prefix")" = "$(git -C "$ROOT" rev-parse "$remote^{tree}")" ]; then
        echo "==> $prefix already equals $url $branch (${remote:0:7}); nothing to push."
        return 0
    fi
    split="$(split_subtree "$prefix")" || exit 1
    if ! git -C "$ROOT" merge-base --is-ancestor "$remote" "$split"; then
        echo "$url $branch (${remote:0:7}) has commits $prefix lacks; pull them first." >&2
        exit 1
    fi

    # What the split would publish, each found again here.
    local tmp; tmp="$(mktemp -d)"
    git -C "$ROOT" log --no-merges --format='%at %ae %s' "$remote" | sort -u > "$tmp/published"
    git -C "$ROOT" log --no-merges --format='%at %ae %s%x09%H' HEAD -- "$prefix" > "$tmp/local"
    local key orig n=0
    while IFS= read -r line; do
        c="${line%%$'\t'*}"; key="${line#*$'\t'}"
        n=$((n + 1))
        if grep -qxF -- "$key" "$tmp/published"; then
            echo "already in $branch under another hash: $(git -C "$ROOT" log -1 --format='%h %s' "$c" | cut -c1-90)" >&2
            bad=1; continue
        fi
        orig="$(awk -F'\t' -v k="$key" '$1 == k { print $2; exit }' "$tmp/local")"
        if [ -z "$orig" ]; then
            echo "no commit here matches split commit $(git -C "$ROOT" log -1 --format='%h %s' "$c" | cut -c1-80)" >&2
            bad=1
        elif git -C "$ROOT" diff-tree --no-commit-id --name-only -r "$orig" | grep -qv "^$prefix/"; then
            echo "mixed commit: $(git -C "$ROOT" log -1 --format='%h %s' "$orig" | cut -c1-90)" >&2
            bad=1
        fi
    done < <(git -C "$ROOT" log --no-merges --format='%H%x09%at %ae %s' "$remote..$split")
    rm -rf "$tmp"
    if [ "$bad" = 1 ]; then
        echo "refusing to push $prefix. Split a mixed commit into a $prefix commit and a" >&2
        echo "game commit; make a rejoin a real merge; see docs/RECOMPONE_FORK.md. A commit" >&2
        echo "already there under another hash is history the split rebuilt: when the" >&2
        echo "prefix equals the shared branch, --rejoin-fork / --rejoin-core marks it." >&2
        exit 1
    fi

    echo "==> $n commit(s) to push to $url $branch, ${remote:0:7}..${split:0:7}:"
    git -C "$ROOT" log --format='    %h %s' "$remote..$split" | cut -c1-100
    if [ "${DRY_RUN:-0}" = 1 ]; then
        echo "==> DRY_RUN=1: not pushed."
        return 0
    fi
    echo "==> pushing (this publishes to the shared repo)"
    git -C "$ROOT" push "$url" "$split:refs/heads/$branch"
}

# rejoin_subtree PREFIX URL BRANCH NAME
#
# Marks HEAD as equal to the shared branch, so a later split starts there. A split
# walks every commit that touches the prefix and no rejoin covers; from a branch
# that merged others it rebuilt pre-subtree history, which the push then refuses
# ("already in BRANCH under another hash"). A rejoin is a TWO-parent commit: HEAD,
# and the newest squash commit, whose tree is the shared branch's. git subtree
# pull takes a rejoin's second parent for the squash and fails on a one-parent one
# ("Needed a single revision"; RECOMPONE_FORK.md). Its trailers say HEAD is the
# shared branch's commit, so a split stops there. Made only when the prefix
# already equals the shared branch; the tree is HEAD's, so nothing else changes.
rejoin_subtree() {
    require_subtree
    local prefix="$1" url="$2" branch="$3" name="$4" remote head sq
    if [ -n "$(git -C "$ROOT" status --porcelain --untracked-files=no)" ]; then
        echo "working tree is dirty; commit or stash before a rejoin." >&2
        exit 1
    fi
    git -C "$ROOT" fetch -q "$url" "$branch"
    remote="$(git -C "$ROOT" rev-parse FETCH_HEAD)"
    head="$(git -C "$ROOT" rev-parse HEAD)"
    if [ "$(git -C "$ROOT" rev-parse "HEAD:$prefix")" != "$(git -C "$ROOT" rev-parse "$remote^{tree}")" ]; then
        echo "$prefix differs from $url $branch (${remote:0:7}); pull or push first, then rejoin." >&2
        exit 1
    fi
    local split; split="$(split_subtree "$prefix")" || exit 1
    if [ "$split" = "$remote" ]; then
        echo "==> a split of $prefix already stops at ${remote:0:7}; no rejoin needed."
        return 0
    fi
    # The newest squash commit with that tree (a squash names the split and no mainline).
    sq=""
    while read -r c; do
        if ! git -C "$ROOT" log -1 --format=%B "$c" | grep -q "^git-subtree-mainline:" \
            && [ "$(git -C "$ROOT" rev-parse "$c^{tree}")" = "$(git -C "$ROOT" rev-parse "$remote^{tree}")" ]; then
            sq="$c"; break
        fi
    done < <(git -C "$ROOT" log --format=%H --grep="^git-subtree-dir: $prefix/*\$" HEAD)
    if [ -z "$sq" ]; then
        echo "no squash commit of $prefix at ${remote:0:7} in HEAD's history; pull it first." >&2
        exit 1
    fi
    local c msg
    msg="Rejoin $prefix at $name's ${remote:0:7}

$prefix here equals $name's ${remote:0:7}, so a subtree split starts at
${head:0:10} and stops rebuilding history from before it. A real merge: the
second parent is the squash commit ${sq:0:10}, as git subtree pull needs.

git-subtree-dir: $prefix
git-subtree-mainline: $head
git-subtree-split: $remote"
    c="$(git -C "$ROOT" commit-tree "HEAD^{tree}" -p HEAD -p "$sq" -m "$msg")"
    git -C "$ROOT" merge -q --ff-only "$c"
    echo "==> rejoined $prefix at ${remote:0:7}: $(git -C "$ROOT" log -1 --format='%h %s' "$c")"
}

[ "$PULL" = 1 ] && pull_subtree tools/RecompOne "$FORK_URL" "$PULL_REF" "the fork"
[ "$PUSH" = 1 ] && push_subtree tools/RecompOne "$FORK_URL" "$FORK_BRANCH"
[ "$REJOIN" = 1 ] && rejoin_subtree tools/RecompOne "$FORK_URL" "$FORK_BRANCH" "the fork"
[ "$CORE_PULL" = 1 ] && pull_subtree tools/verdite-core "$CORE_URL" "$CORE_REF" "Verdite Core"
[ "$CORE_PUSH" = 1 ] && push_subtree tools/verdite-core "$CORE_URL" "$CORE_BRANCH"
[ "$CORE_REJOIN" = 1 ] && rejoin_subtree tools/verdite-core "$CORE_URL" "$CORE_BRANCH" "Verdite Core"

if [ "$BUILD" = 1 ]; then
    echo "==> building recompiler"
    dotnet build "$TOOLS/RecompOne.Recompiler" -c Release
fi

echo "done."
