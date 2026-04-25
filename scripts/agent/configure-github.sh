#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./lib.sh
source "$SCRIPT_DIR/lib.sh"

usage() {
  cat <<'USAGE'
Usage:
  scripts/agent/configure-github.sh [--repo owner/name] [--base main] [--required-check windows-ci] [--dry-run]

Purpose:
  One-time repository bootstrap for this agent workflow:
  - Enable auto-merge
  - Enable delete branch on merge
  - Configure required status check on base branch
USAGE
}

REPO_SLUG=""
BASE_BRANCH="main"
REQUIRED_CHECK="windows-ci"
DRY_RUN="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      REPO_SLUG="$2"
      shift 2
      ;;
    --base)
      BASE_BRANCH="$2"
      shift 2
      ;;
    --required-check)
      REQUIRED_CHECK="$2"
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

require_cmd gh

if [[ "$DRY_RUN" == "true" ]]; then
  if [[ -z "$REPO_SLUG" ]]; then
    REPO_SLUG="$(origin_repo_slug)"
  fi

  printf '{\n'
  printf '  "repo": "%s",\n' "$(json_escape "$REPO_SLUG")"
  printf '  "base": "%s",\n' "$(json_escape "$BASE_BRANCH")"
  printf '  "required_check": "%s",\n' "$(json_escape "$REQUIRED_CHECK")"
  printf '  "auto_merge_enabled": true,\n'
  printf '  "delete_branch_on_merge": true,\n'
  printf '  "required_status_check_configured": true,\n'
  printf '  "dry_run": true\n'
  printf '}\n'
  exit 0
fi

ensure_gh_auth

if [[ -z "$REPO_SLUG" ]]; then
  REPO_SLUG="$(gh repo view --json nameWithOwner --jq '.nameWithOwner')"
fi

agent_info "Enabling repository auto-merge and delete branch on merge"
gh api --method PATCH "repos/$REPO_SLUG" \
  -f allow_auto_merge=true \
  -f delete_branch_on_merge=true >/dev/null

status_checks_configured="true"
if ! gh api --method PATCH "repos/$REPO_SLUG/branches/$BASE_BRANCH/protection/required_status_checks" \
  -f strict=true \
  -f contexts[]="$REQUIRED_CHECK" >/dev/null 2>&1; then
  agent_warn "PATCH required_status_checks failed; attempting full branch protection upsert."

  payload_file="$(mktemp)"
  cat > "$payload_file" <<JSON
{
  "required_status_checks": {
    "strict": true,
    "contexts": ["$REQUIRED_CHECK"]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": null,
  "restrictions": null
}
JSON

  if ! gh api --method PUT "repos/$REPO_SLUG/branches/$BASE_BRANCH/protection" --input "$payload_file" >/dev/null 2>&1; then
    status_checks_configured="false"
    agent_warn "Could not configure required checks automatically. Configure branch protection manually in GitHub settings."
  fi

  rm -f "$payload_file" >/dev/null 2>&1 || true
fi

printf '{\n'
printf '  "repo": "%s",\n' "$(json_escape "$REPO_SLUG")"
printf '  "base": "%s",\n' "$(json_escape "$BASE_BRANCH")"
printf '  "required_check": "%s",\n' "$(json_escape "$REQUIRED_CHECK")"
printf '  "auto_merge_enabled": true,\n'
printf '  "delete_branch_on_merge": true,\n'
printf '  "required_status_check_configured": %s,\n' "$status_checks_configured"
printf '  "dry_run": false\n'
printf '}\n'
