### Task 2: Sumi skin, shell and static guards

**Files:**
- Copy from RM: `wwwroot/css/theme.css` (verbatim), `wwwroot/css/app.css`, `Shared/{Icon.razor, AppMenu.razor(.css), ChangelogDialog.razor(.css), ReconnectModal.razor(.css,.js), LoginLayout.razor, MainLayout.razor(.css), NavMenu.razor(.css)}`, `Services/ChangelogDocument.cs`, `wwwroot/js/session-guard.js` (replaces the current copy)
- Create: `wwwroot/js/cloudselfservice.js` (RM's `rm.js` with the global renamed `window.cssp`, keys renamed)
- Tests, copied from RM and adapted: `ComponentStyleContractTests.cs`, `StylesheetIntegrityTests.cs`, `IconNameContractTests.cs`, `ChangelogDocumentTests.cs`
- Delete: `wwwroot/css/bootstrap/`, `wwwroot/css/open-iconic/`, `wwwroot/css/site.css`, `wwwroot/js/theme-manager.js`, `Shared/MainLayout.razor.css`'s old rules (replaced by RM's)
- Modify: `CloudSelfService.csproj` (add `Markdig`; `<EmbeddedResource Include="CHANGELOG.md" LogicalName="CloudSelfService.Docs.CHANGELOG.md" />`), `App.razor` (link `theme.css`, `app.css`, `CloudSelfService.styles.css` in that order; no chart palette)

**Interfaces:**
- Produces: `<Icon Name="…" />`. Later tasks may use `folder`, `file`, `upload`, `download`, `trash`, `pencil`, `plus`, `shield`, `users`, `database`, `history`, `help`, and must add any missing name to `Icon.razor`'s switch. NavMenu items are added by later tasks.

- [ ] **Step 1: Copy the four guard tests from RM** and repoint their paths and namespaces. Run them. Expected: FAIL, because `site.css`/Bootstrap classes are still referenced and theme.css is absent.
- [ ] **Step 2: Copy the shell files.** Rename the keys and the JS global, then strip RM-specific nav entries. NavMenu for now has one item, `Files` → `/`. The AppMenu shows the theme control and the changelog link. The header shows `advanced-logo-full.png`, "Cloud Self Service" and the `AppVersionService` chip.
- [ ] **Step 3: Delete MudBlazor, Bootstrap, Open Iconic, `site.css` and `theme-manager.js`.** Remove the `@using MudBlazor` lines.
- [ ] **Step 4: Prove each guard red.** Plant one defect per guard, watch the test fail, then revert. The four defects: an undefined `var(--nope)` in a `.razor.css`; a `block__element--modifier` class with no rule; a stylesheet missing one `*/`; `<Icon Name="nope" />`. Record the four red runs in the task report.
- [ ] **Step 5: Verify.** `dotnet test …` passes, all green. Run the app and check `/` in light and dark at 1440 px, 768 px and 375 px: the header, collapsible sidebar, changelog dialog and theme switch all work, and there are no console errors.
- [ ] **Step 6: Commit:** "Adopt Developer Standards sumi skin and shell; drop MudBlazor and Bootstrap".

---

