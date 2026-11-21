# Testing — Advanced Cloud Self Service

## Layout
- `CloudSelfService.Tests/` (xUnit, `InternalsVisibleTo` granted) holds unit, contract and endpoint tests. Run: `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj`. Name the project: a bare `dotnet test` runs nothing.
- PostgreSQL integration tests skip unless `CSSP_TEST_PG` is set to a connection string for a throwaway database.
- `webapp-tests/` is the app-owned Playwright pytest suite (`data-testid` selectors only). It starts nothing; point `CSSP_BASE_URL` at an app running in Development with `Auth__Provider=Dev` and `Storage__UseFake=true`.

## Conventions
- Test naming: `MethodName_Condition_ExpectedResult`
- Skip: live Azure calls unless explicitly enabled
- Target: access decisions, path handling, parsing, boundary conditions, endpoint contracts

## Dockerfile test gate
The Dockerfile build stage runs `dotnet test` before `dotnet publish`, so a failing test blocks the container build. Some tests read `ThirdPartyNotices/`, so keep that folder out of `.dockerignore`.
