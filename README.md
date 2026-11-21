# Advanced Cloud Self Service

A private storage access portal. Advanced customers and staff sign in with Microsoft and browse, upload and download files held in Advanced-hosted Azure storage. The browser never receives a storage key, SAS link or storage address: every byte streams through the app behind one access check.

**[Release notes](CHANGELOG.md)** · **[Developer guide (CLAUDE.md)](CLAUDE.md)** · **[Operations guide](docs/operations.md)**

## Quick start (no Entra, no Azure)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Auth__Provider = "Dev"
$env:Storage__UseFake = "true"
dotnet run --no-launch-profile
```

Open `http://localhost:5300/login` and pick a persona. Development sign-in and the fake storage are refused outside Development.

## Tests

```powershell
dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj

# Browser tests: app running as above, then
pip install -r webapp-tests/requirements.txt
python -m playwright install chromium
cd webapp-tests
python -m pytest test_cloudselfservice.py -v      # CSSP_BASE_URL overrides http://localhost:5300
```

## Container

```powershell
podman build -t cloudselfservice:1.0.0 --build-arg APP_VERSION=1.0.0 .
```

The build runs the unit tests and fails if any fail. See `CLAUDE.md` for the registry push steps and `docs/operations.md` for configuration.

## Azure Government customers

Send the customer's IT administrator `docs/gov-cloud-guest-setup.md`.
