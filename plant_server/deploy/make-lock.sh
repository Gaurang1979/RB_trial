#!/usr/bin/env bash
# Makes requirements.lock again from requirements.txt: exact versions with hashes, for Python 3.14 on every OS.
# Run it after changing requirements.txt, then run the tests on an install from the new lock before committing both.
set -euo pipefail
cd "$(dirname "$0")/.."
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
uv pip compile requirements.txt --universal --python-version 3.14 --generate-hashes --no-header -q -o "$tmp"
{
  echo "# Exact versions (with hashes) of requirements.txt, tested together on Python 3.14. Every site install uses this file:"
  echo "#   pip install --require-hashes -r requirements.lock"
  echo "# Made by deploy/make-lock.sh (uv, one file for Linux, Windows and macOS); never edit it by hand."
  cat "$tmp"
} > requirements.lock
