# Testing — Advanced Cloud Self Service

## Current state
No test project exists in the initial scaffold. Add one when meaningful pure-function logic is introduced in `Services/`.

## When to add tests
Create `CloudSelfService.Tests/` (xUnit) when any of the following exist:
- Pure-function helpers in `Services/` (parsing, calculations, data mapping)
- Model arithmetic or business rules
- Azure API response parsing logic

## Test conventions (follow the suite standard)
- xUnit in `CloudSelfService.Tests/` with `InternalsVisibleTo` grant in the `.csproj`
- Test naming: `MethodName_Condition_ExpectedResult`
- Skip: UI rendering, live Azure API calls, HTTP connections
- Target: pure functions, parsing, boundary conditions

## Dockerfile test gate
When a test project exists, add `dotnet test` to the Dockerfile build stage (before `dotnet publish`) so failing tests block container builds. See `EscapedDefects/Dockerfile` for the pattern.
