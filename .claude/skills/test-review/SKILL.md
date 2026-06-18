# /test-review

Invoke the unit-test-reviewer agent to check coverage of recently changed service logic.

```
Agent(subagent_type: "unit-test-reviewer", prompt: "Review test coverage for the methods recently added or changed in Services/.")
```

Pass `--review-only` to report gaps without writing new tests.

**Note:** Stop any running `dotnet run` instance before running `dotnet test` — the running process holds a file lock on the `.dll` that causes MSB3027 errors.
