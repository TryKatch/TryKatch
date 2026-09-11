#!/usr/bin/env bash
set -euo pipefail

if ! command -v git >/dev/null 2>&1; then
  printf 'Git was not found. Install Git, then run git init in this directory.\n' >&2
  exit 0
fi

if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  printf 'Git repository already available; initialization skipped.\n'
  exit 0
fi

git init --initial-branch=main
printf 'Initialized Git repository with main as the initial branch.\n'
