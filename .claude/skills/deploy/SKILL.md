# /deploy

Build and push the CloudSelfService container image, then deploy to Azure.

## Steps

1. Confirm `dotnet build` is clean (0 errors).
2. Determine the version from `CHANGELOG.md` (top entry).
3. Build the container image:
   ```powershell
   $version = "0.1.0"
   podman build -t cloudselfservice:$version --build-arg APP_VERSION=$version .
   ```
4. Tag and push to ACR:
   ```powershell
   podman tag cloudselfservice:$version auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:latest
   podman push auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:latest
   ```
5. In the Azure portal (or via `az webapp restart`), restart the App Service to pull the new image.
6. Verify the topbar version chip shows the new version.

## Azure target
See `.claude/../../.claude/azure-deployments.json` at the workspace root for subscription / resource group / app name.
