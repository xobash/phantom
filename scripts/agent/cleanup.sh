#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./lib.sh
source "$SCRIPT_DIR/lib.sh"

usage() {
  cat <<'USAGE'
Usage:
  scripts/agent/cleanup.sh [--prefixes claude,codex] [--base main] [--dry-run]

Contract:
  Input: branch prefixes (default claude,codex)
  Output: JSON with deleted local branches, deleted remote branches, skipped reasons

Safety:
  If the working tree is dirty, cleanup exits without deleting anything.
USAGE
}

PREFIXES_CSV="claude,codex"
BASE_BRANCH="main"
DRY_RUN="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --prefixes)
      PREFIXES_CSV="$2"
      shift 2
      ;;
    --base)
      BASE_BRANCH="$2"
      shift 2
      ;;
    --dry-run)
      DRY_RUN="true"
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      die "Unknown argument: $1"
      ;;
  esac
done

require_cmd git
in_git_repo || die "Not in a git repository"
ROOT="$(repo_root)"
cd "$ROOT"

IFS=',' read -r -a PREFIXES <<< "$PREFIXES_CSV"

contains_item() {
  local needle="$1"
  shift
  local item
  for item in "$@"; do
    if [[ "$item" == "$needle" ]]; then
      return 0
    fi
  done
  return 1
}

LOCAL_DELETED=()
REMOTE_DELETED=()
WORKTREES_DELETED=()
NOTES=()

if is_dirty_worktree; then
  NOTES+=("Working tree is dirty; cleanup skipped.")
  printf '{\n'
  printf '  "skipped": true,\n'
  printf '  "base": "%s",\n' "$(json_escape "$BASE_BRANCH")"
  printf '  "prefixes": %s,\n' "$(json_array_from_args "${PREFIXES[@]}")"
  printf '  "local_deleted": %s,\n' "$(json_array_from_args)"
  printf '  "remote_deleted": %s,\n' "$(json_array_from_args)"
  printf '  "stale_worktrees_deleted": %s,\n' "$(json_array_from_args)"
  printf '  "notes": %s\n' "$(json_array_from_args "${NOTES[@]}")"
  printf '}\n'
  exit 0
fi

if [[ "$DRY_RUN" == "false" ]]; then
  git fetch origin --prune >/dev/null
  git worktree prune >/dev/null
else
  NOTES+=("Dry-run mode enabled. No deletions were executed.")
fi

CURRENT_BRANCH="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || printf '')"
ACTIVE_WORKTREE_PATHS=()
ACTIVE_WORKTREE_BRANCHES=()

while IFS= read -r line; do
  case "$line" in
    worktree\ *)
      ACTIVE_WORKTREE_PATHS+=("${line#worktree }")
      ;;
    branch\ refs/heads/*)
      ACTIVE_WORKTREE_BRANCHES+=("${line#branch refs/heads/}")
      ;;
  esac
done < <(git worktree list --porcelain)

CLAUDE_WORKTREES_DIR="$ROOT/.claude/worktrees"
if [[ -d "$CLAUDE_WORKTREES_DIR" ]]; then
  for dir in "$CLAUDE_WORKTREES_DIR"/*; do
    [[ -d "$dir" ]] || continue

    if ! contains_item "$dir" "${ACTIVE_WORKTREE_PATHS[@]}"; then
      if [[ "$DRY_RUN" == "true" ]]; then
        WORKTREES_DELETED+=("$dir (dry-run)")
      else
        rm -rf "$dir"
        WORKTREES_DELETED+=("$dir")
      fi
    fi
  done
fi

for prefix in "${PREFIXES[@]}"; do
  while IFS= read -r branch; do
    [[ -n "$branch" ]] || continue

    if [[ "$branch" == "$CURRENT_BRANCH" ]]; then
      NOTES+=("Skipped local $branch (currently checked out).")
      continue
    fi

    if contains_item "$branch" "${ACTIVE_WORKTREE_BRANCHES[@]}"; then
      NOTES+=("Skipped local $branch (active in another worktree).")
      continue
    fi

    if git merge-base --is-ancestor "$branch" "origin/$BASE_BRANCH" >/dev/null 2>&1; then
      if [[ "$DRY_RUN" == "true" ]]; then
        LOCAL_DELETED+=("$branch (dry-run)")
      else
        if git branch -d "$branch" >/dev/null 2>&1; then
          LOCAL_DELETED+=("$branch")
        else
          NOTES+=("Failed to delete local $branch.")
        fi
      fi
    else
      NOTES+=("Skipped local $branch (not merged into origin/$BASE_BRANCH).")
    fi
  done < <(git for-each-ref --format='%(refname:short)' "refs/heads/${prefix}/*")

done

PROTECTED_BRANCHES=("$BASE_BRANCH" "main" "master" "develop" "development")

for prefix in "${PREFIXES[@]}"; do
  while IFS= read -r remote_ref; do
    [[ -n "$remote_ref" ]] || continue
    branch="${remote_ref#origin/}"

    if contains_item "$branch" "${PROTECTED_BRANCHES[@]}"; then
      NOTES+=("Skipped remote $branch (protected).")
      continue
    fi

    if git merge-base --is-ancestor "origin/$branch" "origin/$BASE_BRANCH" >/dev/null 2>&1; then
      if [[ "$DRY_RUN" == "true" ]]; then
        REMOTE_DELETED+=("$branch (dry-run)")
      else
        if git push origin --delete "$branch" >/dev/null 2>&1; then
          REMOTE_DELETED+=("$branch")
        else
          NOTES+=("Failed to delete remote $branch.")
        fi
      fi
    else
      NOTES+=("Skipped remote $branch (not merged into origin/$BASE_BRANCH).")
    fi
  done < <(git for-each-ref --format='%(refname:short)' "refs/remotes/origin/${prefix}/*")
done

printf '{\n'
printf '  "skipped": false,\n'
printf '  "base": "%s",\n' "$(json_escape "$BASE_BRANCH")"
printf '  "prefixes": %s,\n' "$(json_array_from_args "${PREFIXES[@]}")"
printf '  "local_deleted": %s,\n' "$(json_array_from_args "${LOCAL_DELETED[@]}")"
printf '  "remote_deleted": %s,\n' "$(json_array_from_args "${REMOTE_DELETED[@]}")"
printf '  "stale_worktrees_deleted": %s,\n' "$(json_array_from_args "${WORKTREES_DELETED[@]}")"
printf '  "notes": %s\n' "$(json_array_from_args "${NOTES[@]}")"
printf '}\n'
