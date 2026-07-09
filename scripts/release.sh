#!/usr/bin/env bash
# Cut a release. A push is a release, so this is the only way to push.
#
#   scripts/release.sh 0.8.0 "Short subject line"
#
# Reads the long commit body from stdin, or opens $EDITOR when stdin is a terminal.
# Refuses to run when checks fail, when the tree is dirty in a way that was not staged,
# or when the tag already exists.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:-}"
SUBJECT="${2:-}"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "usage: $0 <major.minor.patch> [subject]" >&2
    exit 2
fi

TAG="v$VERSION"

if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
    echo "Tag $TAG already exists. A release is never rewritten." >&2
    exit 1
fi

CURRENT=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props)
if [ "$CURRENT" = "$VERSION" ]; then
    echo "Directory.Build.props already says $VERSION; every push must bump it." >&2
    exit 1
fi

echo "== $CURRENT -> $VERSION =="
sed -i "s|<Version>$CURRENT</Version>|<Version>$VERSION</Version>|" Directory.Build.props

# Never publish something that has not been exercised.
if ! scripts/check.sh >/tmp/gogdl-release-check.log 2>&1; then
    echo "checks failed, not releasing:" >&2
    tail -30 /tmp/gogdl-release-check.log >&2
    git checkout -- Directory.Build.props
    exit 1
fi
echo "checks passed"

[ -n "$SUBJECT" ] || SUBJECT="Release $TAG"

if [ -t 0 ]; then
    BODY=""
else
    BODY=$(cat)
fi

git add -A
printf '%s\n\n%s\n' "$SUBJECT" "$BODY" | git commit -q -F -
git tag -a "$TAG" -m "$TAG — $SUBJECT"

echo "== pushing =="
git push origin main "$TAG"

echo
echo "Released $TAG"
