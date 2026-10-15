#!/usr/bin/env bash
# Cut a release: tag HEAD. The tag is the version; nothing is committed, and
# nothing is pushed.
#
#   bash scripts/release.sh 0.5.0
#
# The script is Verdite Core's; see "Cutting one" in docs/PACKAGING.md.
exec bash "$(dirname "${BASH_SOURCE[0]}")/../tools/verdite-core/scripts/release.sh" "$@"
