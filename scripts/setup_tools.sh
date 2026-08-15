#!/usr/bin/env bash
# Clone RecompOne and apply the local fixes this port depends on.
#
# tools/RecompOne is a gitignored checkout, so any change made inside it is lost
# on a fresh clone. Fixes live in patches/recompone/ and are re-applied here.
# Upstream rejects AI-authored pull requests, so these stay local and should be
# reported as issues rather than sent as PRs.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS="$ROOT/tools/RecompOne"
UPSTREAM="https://github.com/BlackLabelHQ/RecompOne.git"

if [ ! -d "$TOOLS/.git" ]; then
    echo "==> cloning RecompOne"
    git clone "$UPSTREAM" "$TOOLS"
else
    echo "==> RecompOne already present at $TOOLS"
fi

echo "==> applying local patches"
shopt -s nullglob
for patch in "$ROOT"/patches/recompone/*.patch; do
    name="$(basename "$patch")"
    if git -C "$TOOLS" apply --reverse --check "$patch" 2>/dev/null; then
        echo "    $name: already applied"
    elif git -C "$TOOLS" apply --check "$patch" 2>/dev/null; then
        git -C "$TOOLS" apply "$patch"
        echo "    $name: applied"
    else
        echo "    $name: FAILED TO APPLY (upstream likely changed)" >&2
        exit 1
    fi
done

echo "==> building recompiler"
dotnet build "$TOOLS/RecompOne.Recompiler" -c Release

echo "done."
