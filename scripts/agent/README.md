# Agent Workflow Scripts

These scripts implement the Claude execution contract for Phantom.

## Prerequisites

- `git`
- `gh` (authenticated with `repo` scope)
- `.NET 8 SDK` (`dotnet`) for `deadcode.sh`

## Contracts

### `ship.sh`

Input:
- task description
- commit message
- PR title/body

Output:
- JSON with branch name, PR URL, auto-merge status, and CI link

Example:

```bash
scripts/agent/ship.sh \
  --task "fix startup logging race" \
  --commit "fix: serialize startup emergency log writes" \
  --title "fix: serialize startup emergency log writes" \
  --body "Implements a lock around startup emergency log writes."
```

### `cleanup.sh`

Input:
- branch prefixes (default `claude,codex`)

Output:
- JSON with deleted local branches, deleted remote branches, stale worktrees removed, skipped reasons

Safety rule:
- If `git status --porcelain` is non-empty, cleanup exits without deletions.

Example:

```bash
scripts/agent/cleanup.sh --prefixes claude,codex --base main
```

### `deadcode.sh`

Input:
- cleanup scope (`broad`)

Output:
- JSON report of changed files, removed files, removed symbol candidates, analyzer findings, and nested `ship` output

Behavior:
- Runs analyzer scan and `dotnet format analyzers` for selected diagnostics.
- Removes tracked artifact files (`*.orig`, `*.rej`, `*.bak`, `*.tmp`, `*.old`).
- Opens a dedicated cleanup PR through `ship.sh`.

Example:

```bash
scripts/agent/deadcode.sh --scope broad
```

### `configure-github.sh`

One-time repository bootstrap.

- Enables auto-merge.
- Enables delete-branch-on-merge.
- Attempts to configure required status check for `main`.

Example:

```bash
scripts/agent/configure-github.sh --repo xobash/phantom --base main --required-check windows-ci
```
