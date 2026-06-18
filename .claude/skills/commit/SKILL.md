# /commit

Stage and commit tracked changes in CloudSelfService.

## Steps

1. Run `git status` to see what's changed.
2. Stage changed files explicitly (never `git add -A` blindly — exclude `appsettings.Development.json`, `*.user`, `bin/`, `obj/`).
3. Write a concise commit message:
   - Subject line: imperative mood, ≤72 chars, no period.
   - Body (optional): why the change was made if not obvious.
4. Run `git commit`.
5. Confirm clean working tree with `git status`.

## Never commit
- `appsettings.Development.json` (contains secrets)
- `*.user` files
- `bin/` or `obj/` directories
- Any file containing credentials or connection strings
