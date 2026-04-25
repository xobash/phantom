#!/usr/bin/env bash

set -euo pipefail

agent_info() {
  printf '[agent] %s\n' "$*" >&2
}

agent_warn() {
  printf '[agent][warn] %s\n' "$*" >&2
}

agent_error() {
  printf '[agent][error] %s\n' "$*" >&2
}

die() {
  agent_error "$*"
  exit 1
}

require_cmd() {
  local cmd="$1"
  command -v "$cmd" >/dev/null 2>&1 || die "Required command not found: $cmd"
}

repo_root() {
  git rev-parse --show-toplevel
}

in_git_repo() {
  git rev-parse --git-dir >/dev/null 2>&1
}

is_dirty_worktree() {
  [[ -n "$(git status --porcelain)" ]]
}

slugify() {
  local input="$1"
  local lowered
  lowered="$(printf '%s' "$input" | tr '[:upper:]' '[:lower:]')"
  lowered="$(printf '%s' "$lowered" | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//; s/-{2,}/-/g')"
  if [[ -z "$lowered" ]]; then
    lowered="task"
  fi
  printf '%s' "${lowered:0:48}"
}

json_escape() {
  local s="$1"
  s="${s//\\/\\\\}"
  s="${s//\"/\\\"}"
  s="${s//$'\n'/\\n}"
  s="${s//$'\r'/\\r}"
  s="${s//$'\t'/\\t}"
  printf '%s' "$s"
}

json_array_from_args() {
  local first=1
  local item
  printf '['
  for item in "$@"; do
    if [[ $first -eq 0 ]]; then
      printf ','
    fi
    first=0
    printf '"%s"' "$(json_escape "$item")"
  done
  printf ']'
}

origin_repo_slug() {
  local remote
  remote="$(git remote get-url origin)"

  if [[ "$remote" =~ ^https://github.com/([^/]+/[^/.]+)(\.git)?$ ]]; then
    printf '%s' "${BASH_REMATCH[1]}"
    return 0
  fi

  if [[ "$remote" =~ ^git@github.com:([^/]+/[^/.]+)(\.git)?$ ]]; then
    printf '%s' "${BASH_REMATCH[1]}"
    return 0
  fi

  die "Could not parse GitHub owner/repo from origin remote: $remote"
}

ensure_gh_auth() {
  gh auth status -h github.com >/dev/null 2>&1 || die "GitHub CLI is not authenticated. Run: gh auth login"
}
