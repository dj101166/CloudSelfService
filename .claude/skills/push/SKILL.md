# /push

Commit all pending changes in this app and push to GitHub.

For workspace-level operations (push all apps, sync to the team mirror), use the orchestrator `/push` from the workspace root.

## Steps

1. **Commit** — follow the same steps as `/commit` for this app.
2. **Push** — `git push` this app's repo to its GitHub remote (`master`).

## Safety rules (never without explicit approval)

- `git push --force` / `--force-with-lease`
- `git reset --hard`, `git rebase`, `git commit --amend`
- Stage `appsettings.Development.json` or any local-only directory
