# /security-review

Invoke the security-auditor agent against the current working tree.

```
Agent(subagent_type: "security-auditor", prompt: "Review all recent changes in CloudSelfService for security issues. Fix anything you find.")
```

Pass `--review-only` to skip fixes and only report findings.
