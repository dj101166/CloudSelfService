# /webapp-test

Run Playwright web tests for Cloud Self Service.

## Prerequisites
- App must be running: `dotnet run` → `http://localhost:5300`
- `ASPNETCORE_ENVIRONMENT=Development`
- Playwright test suite in `D:\SC\IAS-AI\.claude\webapp-tests\test_cloudselfservice.py` (create if not yet written)

## Run
```powershell
cd "D:\SC\IAS-AI\.claude\webapp-tests"
python -m pytest test_cloudselfservice.py -v
```
