#!/usr/bin/env bash
# Relative-link checker for the repo's Markdown. Resolves every relative link and
# image target against the filesystem the way GitHub does, and fails if any point
# at a path that does not exist. External URLs, mailto:, and bare #anchors are not
# checked (no network, no flake): this guards against path rot, the failure mode
# that silently breaks docs when files move.
set -euo pipefail

cd "$(dirname "$0")/.."
broken=0
checked=0

# All tracked Markdown, excluding vendored/build output.
while IFS= read -r file; do
  # Inline links and images: ](target): strip an optional "title" and #anchor.
  while IFS= read -r raw; do
    link=${raw#*](}
    link=${link%)}
    target=${link%% *}   # drop optional link title
    target=${target%%#*}  # drop anchor fragment
    case "$target" in
      ''|http://*|https://*|mailto:*|tel:*|//*) continue ;;
    esac
    if [ "${target#/}" != "$target" ]; then
      resolved=".${target}"          # repo-root-absolute
    else
      resolved="$(dirname "$file")/${target}"  # file-relative
    fi
    checked=$((checked + 1))
    if [ ! -e "$resolved" ]; then
      echo "BROKEN  $file -> $link"
      broken=$((broken + 1))
    fi
  done < <(grep -oE '\]\([^)]+\)' "$file" || true)
done < <(git ls-files '*.md')

echo "Checked $checked relative link(s); $broken broken."
[ "$broken" -eq 0 ]
