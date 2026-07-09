# /commit

Stage and commit tracked changes in CloudSelfService.

## Steps

1. Run `git status` to see what's changed.
2. Stage changed files explicitly (never `git add -A` blindly — exclude `appsettings.Development.json`, `*.user`, `bin/`, `obj/`).
3. Write the commit message (use a HEREDOC to preserve formatting):
   - Subject line: imperative mood, ~50-72 chars (120 hard cap), no trailing period, no Co-Authored-By trailer.
   - Body: for anything beyond a trivial one-liner (and always for large or multi-part changes), add a blank line after the subject, then prescriptive bullets describing each notable change (what + why); group related bullets. A trivial change may be subject-only.
4. Run `git commit`.
5. Confirm clean working tree with `git status`.

## Never commit
- `appsettings.Development.json` (contains secrets)
- `*.user` files
- `bin/` or `obj/` directories
- Any file containing credentials or connection strings
