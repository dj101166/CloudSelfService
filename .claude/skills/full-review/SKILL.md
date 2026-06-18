# /full-review

Run a comprehensive review of recent CloudSelfService changes.

## Phases

1. **Security audit** — invoke security-auditor agent.
2. **Test coverage** — invoke unit-test-reviewer agent (if `Services/` changed).
3. **Build verification** — `dotnet build` (0 errors required).

## Usage

```
/full-review              # all phases
/full-review --security-only
/full-review --tests-only
```
