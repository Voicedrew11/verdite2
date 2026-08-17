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
patches=("$ROOT"/patches/recompone/*.patch)

# The stack is peeled off newest-first before it is applied oldest-first.
#
# Asking each patch on its own "are you already applied?" -- reverse-check it and
# see -- only works while no patch touches lines an earlier one added. 0010 edits
# GteDepth.cs, which 0009 creates, so on an already-patched checkout 0009 reverses
# against text 0010 has since changed, fails, and gets reported as upstream having
# moved. Undoing the stack in the exact opposite order to the one it was applied in
# has no such problem, and leaves a tree every patch applies to cleanly.
#
# A patch that does not reverse stops the peeling rather than forcing it: on a
# fresh clone the first check fails at once and nothing is undone, and an
# uncaptured edit inside the checkout stops it at that patch instead of being
# rolled over.
for (( i=${#patches[@]}-1 ; i>=0 ; i-- )); do
    git -C "$TOOLS" apply --reverse --check "${patches[i]}" 2>/dev/null || break
    git -C "$TOOLS" apply --reverse "${patches[i]}"
done

for patch in "${patches[@]}"; do
    name="$(basename "$patch")"
    if git -C "$TOOLS" apply --check "$patch" 2>/dev/null; then
        git -C "$TOOLS" apply "$patch"
        echo "    $name: applied"
    elif git -C "$TOOLS" apply --reverse --check "$patch" 2>/dev/null; then
        echo "    $name: already applied"
    else
        echo "    $name: FAILED TO APPLY (upstream likely changed)" >&2
        exit 1
    fi
done

echo "==> building recompiler"
dotnet build "$TOOLS/RecompOne.Recompiler" -c Release

echo "done."
