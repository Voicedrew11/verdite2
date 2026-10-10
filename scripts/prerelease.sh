#!/usr/bin/env bash
# Publish the current branch's HEAD as the preview: the rolling `preview`
# prerelease on GitHub, replacing the last one, each package zipped with the
# PREVIEW_PASSWORD secret. Release.yml's preview job does the work; this starts it
# on the branch as GitHub has it, so HEAD must be pushed. VERSION is not touched
# and no tag is made: a preview is a commit, not a version. See "Previews" in
# docs/PACKAGING.md.
#
#   bash scripts/prerelease.sh            # start it and wait for it
#   bash scripts/prerelease.sh --no-wait
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

WAIT=1
case "${1:-}" in
    "") ;;
    --no-wait) WAIT=0 ;;
    *) echo "usage: bash scripts/prerelease.sh [--no-wait]"; exit 1 ;;
esac

branch="$(git rev-parse --abbrev-ref HEAD)"
[ "$branch" != HEAD ] || { echo "detached HEAD; check out the branch to publish"; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "working tree is dirty"; exit 1; }
git fetch -q origin "$branch"
[ "$(git rev-parse HEAD)" = "$(git rev-parse "origin/$branch")" ] \
    || { echo "HEAD is not origin/$branch; push it first (the workflow builds what GitHub has)"; exit 1; }
# Listing secrets needs admin access; without it, say so rather than claim the
# secret is unset (the job refuses to zip anything if it really is).
if secrets="$(gh secret list 2>/dev/null)"; then
    grep -q '^PREVIEW_PASSWORD\b' <<<"$secrets" \
        || { echo "the PREVIEW_PASSWORD secret is not set: gh secret set PREVIEW_PASSWORD"; exit 1; }
else
    echo "warning: cannot list the repository's secrets; the job fails if PREVIEW_PASSWORD is unset"
fi

sha="$(git rev-parse HEAD)"
# A little early, for clock skew. GNU date, else BSD's.
start="$(date -u -d "-30 sec" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v-30S +%Y-%m-%dT%H:%M:%SZ)"
gh workflow run release.yml --ref "$branch" -f preview=true
echo "==> preview ${sha::9} started"
# The run appears a moment after it is asked for; find it by its commit, and
# started no earlier than this, since a commit can have been a preview before.
for _ in $(seq 20); do
    run="$(gh run list --workflow release.yml --event workflow_dispatch --commit "$sha" --limit 5 \
        --json databaseId,createdAt --jq "[.[] | select(.createdAt >= \"$start\")][0].databaseId // empty")"
    [ -n "$run" ] && break
    sleep 3
done
[ -n "${run:-}" ] || { echo "could not find the run; see: gh run list --workflow release.yml"; exit 1; }
echo "    $(gh run view "$run" --json url --jq .url)"
[ "$WAIT" = 1 ] || exit 0
gh run watch "$run" --exit-status --interval 30 >/dev/null
echo "==> published: $(gh release view preview --json url --jq .url)"
