#!/usr/bin/env bash
#
# Enables the versioned git hooks in .githooks for this clone.
#
# git only reads hooks from .git/hooks, which is not shared between clones. Pointing
# core.hooksPath at the committed .githooks directory is what makes the pre-commit secret scan
# apply to everyone.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

git -C "$ROOT" config core.hooksPath .githooks
chmod +x "$ROOT/.githooks/pre-commit"

echo "core.hooksPath set to .githooks"

if command -v trufflehog > /dev/null 2>&1; then
    echo "trufflehog found: $(trufflehog --version)"
else
    echo "trufflehog is not installed, so the pre-commit hook will warn and allow the commit."
    echo "Install it from https://github.com/trufflesecurity/trufflehog"
fi
