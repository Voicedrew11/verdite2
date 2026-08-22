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

# The patch stack in patches/recompone/ targets one specific upstream tree.
# main drifts -- files get renamed (0x1FFFFCu became Runtime.RamWordMask), which
# makes `git apply` reject the diffs against context that no longer exists -- so
# a plain clone of HEAD breaks the build. Pin to the newest commit the whole
# stack still applies to cleanly, in order. Re-run this after moving the pin to
# re-derive the patched checkout; bump it only alongside rebasing the patches.
PIN="870c5baa735111687c62d637a159eb47a08e94ae"

if [ ! -d "$TOOLS/.git" ]; then
    echo "==> cloning RecompOne"
    git clone "$UPSTREAM" "$TOOLS"
else
    echo "==> RecompOne already present at $TOOLS"
    git -C "$TOOLS" fetch --quiet origin || true
fi

echo "==> pinning RecompOne to $PIN"
# --force resets tracked files but leaves untracked ones; several patches CREATE
# files (0004 -> LibApi.cs, 0009 -> GteDepth.cs), so a re-run would hit "already
# exists" without also removing them. clean -fd gives a pristine pinned tree,
# which the apply loop below then patches from a known state every time.
git -C "$TOOLS" checkout --quiet --force "$PIN"
git -C "$TOOLS" clean -qfd

echo "==> applying local patches"
shopt -s nullglob
patches=("$ROOT"/patches/recompone/*.patch)

# The stack is peeled off newest-first before it is applied oldest-first.
#
# Asking each patch on its own "are you already applied?" -- reverse-check it and
# see -- only works while no patch touches lines an earlier one added. 0010 edits
# GteDepth.cs, which 0009 creates, and 0011, 0012 and 0014 edit it again, so on an
# already-patched checkout 0009 reverses against text a later patch has since
# changed, fails, and gets reported as upstream having moved. Undoing the stack in
# the exact opposite order to the one it was applied in has no such problem, and
# leaves a tree every patch applies to cleanly.
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
