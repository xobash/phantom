#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./lib.sh
source "$SCRIPT_DIR/lib.sh"

usage() {
  cat <<'USAGE'
Usage:
  scripts/agent/ship.sh \
    --task "task description" \
    --commit "commit message" \
    --title "PR title" \
    [--body "PR body"] \
    [--body-file /path/to/body.md] \
    [--branch claude/my-branch] \
    [--base main] \
    [--merge-method squash|merge|rebase] \
    [--dry-run]

Contract:
  Input: task description, commit message, PR title/body
  Output: JSON with branch name, PR URL, auto-merge status, CI link
USAGE
}

TASK_DESC=""
COMMIT_MSG=""
PR_TITLE=""
PR_BODY=""
PR_BODY_FILE=""
BRANCH_NAME=""
BASE_BRANCH="main"
MERGE_METHOD="squash"
DRY_RUN="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --task)
      TASK_DESC="$2"
      shift 2
      ;;
    --commit)
      COMMIT_MSG="$2"
      shift 2
      ;;
    --title)
      PR_TITLE="$2"
      shift 2
      ;;
    --body)
      PR_BODY="$2"
      shift 2
      ;;
    --body-file)
      PR_BODY_FILE="$2"
      shift 2
      ;;
    --branch)
      BRANCH_NAME="$2"
      shift 2
      ;;
    --base)
      BASE_BRANCH="$2"
      shift 2
      ;;
    --merge-method)
      MERGE_METHOD="$2"
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

[[ -n "$TASK_DESC" ]] || die "--task is required"
[[ -n "$COMMIT_MSG" ]] || die "--commit is required"
[[ -n "$PR_TITLE" ]] || die "--title is required"

if [[ -n "$PR_BODY" && -n "$PR_BODY_FILE" ]]; then
  die "Use either --body or --body-file, not both"
fi

if [[ -z "$BRANCH_NAME" ]]; then
  BRANCH_NAME="claude/$(slugify "$TASK_DESC")"
fi

case "$MERGE_METHOD" in
  squash|merge|rebase)
    ;;
  *)
    die "--merge-method must be one of: squash, merge, rebase"
    ;;
esac

require_cmd git
require_cmd gh

in_git_repo || die "Not in a git repository"
ROOT="$(repo_root)"
cd "$ROOT"

created_tmp_body="false"
if [[ -n "$PR_BODY_FILE" ]]; then
  [[ -f "$PR_BODY_FILE" ]] || die "PR body file not found: $PR_BODY_FILE"
else
  PR_BODY_FILE="$(mktemp)"
  created_tmp_body="true"
  if [[ -z "$PR_BODY" ]]; then
    PR_BODY="Task: $TASK_DESC"
  fi
  printf '%s\n' "$PR_BODY" > "$PR_BODY_FILE"
fi

if [[ "$DRY_RUN" == "true" ]]; then
  repo_slug="$(origin_repo_slug)"
  ci_url="https://github.com/${repo_slug}/actions?query=branch%3A${BRANCH_NAME}"

  printf '{\n'
  printf '  "task": "%s",\n' "$(json_escape "$TASK_DESC")"
  printf '  "branch": "%s",\n' "$(json_escape "$BRANCH_NAME")"
  printf '  "base": "%s",\n' "$(json_escape "$BASE_BRANCH")"
  printf '  "pr_url": "",\n'
  printf '  "auto_merge_requested": false,\n'
  printf '  "ci_url": "%s",\n' "$(json_escape "$ci_url")"
  printf '  "dry_run": true\n'
  printf '}\n'

  [[ "$created_tmp_body" == "true" ]] && rm -f "$PR_BODY_FILE" >/dev/null 2>&1 || true
  exit 0
fi

ensure_gh_auth

stash_created="false"
stash_restored="false"
stash_ref=""

cleanup_on_error() {
  local exit_code=$?
  if [[ "$stash_created" == "true" && "$stash_restored" == "false" ]]; then
    agent_warn "Attempting to restore stashed work after failure."
    if [[ -n "$stash_ref" ]]; then
      git stash pop "$stash_ref" >/dev/null 2>&1 || true
    else
      git stash pop >/dev/null 2>&1 || true
    fi
    stash_restored="true"
  fi

  if [[ "$created_tmp_body" == "true" ]]; then
    rm -f "$PR_BODY_FILE" >/dev/null 2>&1 || true
  fi

  exit "$exit_code"
}
trap cleanup_on_error ERR INT TERM

if is_dirty_worktree; then
  stash_name="agent-ship-$(date +%s)"
  agent_info "Working tree is dirty; stashing changes before branch sync."
  git stash push -u -m "$stash_name" >/dev/null
  stash_created="true"
  stash_ref="$(git stash list --format='%gd %s' | awk -v needle="$stash_name" '$0 ~ needle { print $1; exit }')"
fi

agent_info "Fetching latest origin/$BASE_BRANCH"
git fetch origin "$BASE_BRANCH" --prune >/dev/null

if git show-ref --verify --quiet "refs/heads/$BRANCH_NAME"; then
  agent_info "Switching to existing branch $BRANCH_NAME"
  git switch "$BRANCH_NAME" >/dev/null
  agent_info "Rebasing $BRANCH_NAME onto origin/$BASE_BRANCH"
  git rebase "origin/$BASE_BRANCH" >/dev/null
else
  agent_info "Creating branch $BRANCH_NAME from origin/$BASE_BRANCH"
  git switch -c "$BRANCH_NAME" "origin/$BASE_BRANCH" >/dev/null
fi

if [[ "$stash_created" == "true" && "$stash_restored" == "false" ]]; then
  agent_info "Re-applying stashed changes"
  if [[ -n "$stash_ref" ]]; then
    git stash pop "$stash_ref" >/dev/null
  else
    git stash pop >/dev/null
  fi
  stash_restored="true"
fi

git add -A -- ':!.claude/worktrees' ':!.claude/worktrees/**' ':!.claude/reports' ':!.claude/reports/**'

if git diff --cached --quiet; then
  agent_info "No staged changes detected; skipping commit."
else
  git commit -m "$COMMIT_MSG" >/dev/null
fi

agent_info "Pushing branch $BRANCH_NAME"
git push -u origin "$BRANCH_NAME" >/dev/null

existing_pr_number="$(gh pr list --state open --head "$BRANCH_NAME" --json number --jq '.[0].number // empty' 2>/dev/null || true)"

if [[ -n "$existing_pr_number" ]]; then
  pr_number="$existing_pr_number"
  agent_info "Updating existing PR #$pr_number"
  gh pr edit "$pr_number" --title "$PR_TITLE" --body "$(cat "$PR_BODY_FILE")" >/dev/null
else
  agent_info "Creating new PR"
  created_pr_url="$(gh pr create --base "$BASE_BRANCH" --head "$BRANCH_NAME" --title "$PR_TITLE" --body-file "$PR_BODY_FILE")"
  pr_number="$(gh pr view "$created_pr_url" --json number --jq '.number')"
fi

pr_url="$(gh pr view "$pr_number" --json url --jq '.url')"

merge_args=(--auto --delete-branch)
case "$MERGE_METHOD" in
  squash)
    merge_args+=(--squash)
    ;;
  merge)
    merge_args+=(--merge)
    ;;
  rebase)
    merge_args+=(--rebase)
    ;;
esac

auto_merge_requested="true"
auto_merge_error=""
if ! gh pr merge "$pr_number" "${merge_args[@]}" >/dev/null 2>&1; then
  auto_merge_requested="false"
  auto_merge_error="Failed to enable auto-merge. Verify repository auto-merge settings and branch protection."
fi

repo_slug="$(origin_repo_slug)"
ci_url="https://github.com/${repo_slug}/actions?query=branch%3A${BRANCH_NAME}"
checks_json="$(gh pr view "$pr_number" --json statusCheckRollup --jq '.statusCheckRollup | map((.name // .context // "unknown") + ":" + (.state // .status // "unknown"))' 2>/dev/null || printf '[]')"

printf '{\n'
printf '  "task": "%s",\n' "$(json_escape "$TASK_DESC")"
printf '  "branch": "%s",\n' "$(json_escape "$BRANCH_NAME")"
printf '  "base": "%s",\n' "$(json_escape "$BASE_BRANCH")"
printf '  "pr_number": %s,\n' "$pr_number"
printf '  "pr_url": "%s",\n' "$(json_escape "$pr_url")"
printf '  "auto_merge_requested": %s,\n' "$auto_merge_requested"
printf '  "auto_merge_error": "%s",\n' "$(json_escape "$auto_merge_error")"
printf '  "ci_url": "%s",\n' "$(json_escape "$ci_url")"
printf '  "status_checks": %s,\n' "$checks_json"
printf '  "dry_run": false\n'
printf '}\n'

if [[ "$created_tmp_body" == "true" ]]; then
  rm -f "$PR_BODY_FILE" >/dev/null 2>&1 || true
fi

trap - ERR INT TERM
