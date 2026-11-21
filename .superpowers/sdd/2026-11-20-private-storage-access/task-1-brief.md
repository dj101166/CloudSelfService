### Task 1: Render model, solution and test project

**Files:**
- Create: `CloudSelfService.slnx`, `App.razor`, `Routes.razor`, `CloudSelfService.Tests/CloudSelfService.Tests.csproj`, `CloudSelfService.Tests/SmokeTests.cs`
- Modify: `CloudSelfService.csproj`, `Program.cs`, `_Imports.razor`, `Pages/Index.razor`, `.gitignore`
- Delete: `CloudSelfService.sln`, `Pages/_Host.cshtml`, `Pages/Error.cshtml`, `Pages/Error.cshtml.cs`, `Pages/AppServices.razor`, `Pages/ServiceHealth.razor`

**Interfaces:**
- Produces: `public partial class Program;` at the end of `Program.cs`, which Task 8's `WebApplicationFactory<Program>` relies on. `InternalsVisibleTo("CloudSelfService.Tests")`.

- [ ] **Step 1: Create the branch.** Run `git checkout -b feat/private-storage-access`. Add `*.msg` to `.gitignore`. The two Microsoft emails stay on disk, untracked.
- [ ] **Step 2: Create the test project** by copying RM's `ReleaseMetrics.Tests.csproj` (same package versions) with the root namespace and assembly name `CloudSelfService.Tests`. Add `SmokeTests.AppVersionService_NoEnvVar_ReturnsFallback`, asserting that `new AppVersionService(...)` yields a non-empty version string.
- [ ] **Step 3: Edit the csproj.** Remove the `MudBlazor` reference. Add the `Compile/Content/EmbeddedResource/None Remove="CloudSelfService.Tests\**"` block (as in RM's csproj lines 17–20) and `<InternalsVisibleTo Include="CloudSelfService.Tests" />`. Replace the `.sln` with `CloudSelfService.slnx` listing both projects.
- [ ] **Step 4: Move to the render model.** `App.razor` is RM's `App.razor` with `releaseMetrics*` keys renamed. Until Task 2 it links only the existing stylesheets. `Routes.razor` is a plain `<Router>` with `<RouteView>` for now; Task 5 adds authorization. Program changes: `AddRazorComponents().AddInteractiveServerComponents()`, `UseAntiforgery()`, `MapStaticAssets()`, `MapRazorComponents<App>().AddInteractiveServerRenderMode()`. Remove `AddRazorPages`/`MapBlazorHub`/`MapFallbackToPage`. Keep the security-header middleware unchanged. Set culture to `en-US`, as RM does.
- [ ] **Step 5: Trim the pages.** Delete the placeholder pages listed above. `Pages/Index.razor` becomes `@page "/"` with heading `Files`.
- [ ] **Step 6: Verify.** Run `dotnet build CloudSelfService.slnx` and expect 0 errors. Run `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` and expect 1 passed. Then `dotnet run`, and `curl -s -o NUL -w "%{http_code}" http://localhost:5300/` should return `200`.
- [ ] **Step 7: Commit:** "Move to Blazor Web App render model; add test project and slnx".

---

