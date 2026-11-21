# Task 2 report: Sumi skin, shell and static guards

Status: DONE_WITH_CONCERNS (minor). Commit 3e16bf6 "Adopt Developer Standards sumi skin and shell; drop MudBlazor and Bootstrap".

## Implemented
- Copied from RM: theme.css (verbatim), app.css, 5 font files (theme.css references ../fonts/*), Icon, AppMenu(+css), ChangelogDialog(+css), ReconnectModal(+css,+js), LoginLayout, MainLayout(+css), NavMenu(+css), ChangelogDocument.cs, session-guard.js, rm.js -> cloudselfservice.js.
- Renames: `window.cssp`; keys `cloudSelfServiceTheme` / `cloudSelfServiceSidebarCollapsed`; namespaces to CloudSelfService; resource `CloudSelfService.Docs.CHANGELOG.md`.
- Removed from cloudselfservice.js: `applyBarPadding` (ApexCharts-only, dead here).
- csproj: Markdig 1.3.2 + EmbeddedResource for CHANGELOG.md.
- App.razor: theme.css, app.css, CloudSelfService.styles.css, pre-paint script, `<ReconnectModal />`, cloudselfservice.js, session-guard.js, reconnect init only (no idle timeout). CRLF preserved. Render-mode logic kept.
- MainLayout: brand "CLOUD SELF SERVICE", version chip, AppMenu, ChangelogDialog; `_changelog` list removed. NavMenu: single Files -> `/` item (folder icon).
- AppMenu: theme (Light/Dark/System) + new "Release notes" item (raises `OnShowChangelog`); identity, avatar, sign-out and the AuthenticationStateProvider/EntraAvatarCache dependencies stripped (no auth yet). Trigger shows a `users` icon.
- Icon.razor: dropped RM-only glyphs (dashboard, pulse, chart, chart-bar, plug, people, list); added folder, file, upload, download, trash, pencil, plus, shield, users, database, history, help; kept document, menu, sun, moon, monitor, signout, close, chevrons.
- Deleted: wwwroot/css/bootstrap, open-iconic, site.css, wwwroot/js/theme-manager.js (13 tracked deletions). MudBlazor already gone (no `@using MudBlazor` remained).
- Tests copied/adapted: ComponentStyleContractTests, StylesheetIntegrityTests (chart-palette assertion and "three files" wording removed), IconNameContractTests, ChangelogDocumentTests.

## TDD evidence
RED (guards copied, old tree): `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` -> `Failed: 1, Passed: 25, Total: 26`; `TheGuard_CoversTheStylesheetsThisMigrationEdits`: Collection ["site.css"], Not found "app.css".
GREEN after shell: `Passed! Failed: 0, Passed: 27, Total: 27`.

Planted-defect red runs (each reverted; final run green 27/27):
1. `.app__nav { color: var(--nope); }` appended to NavMenu.razor.css -> FAIL `ComponentStyleContractTests.EveryTokenNamedInAComponent_IsDefinedInAStylesheet`: "NavMenu.razor.css names --nope". (Failed 1, Passed 26)
2. `<div class="zz__item--bad">` in Index.razor -> FAIL `ComponentStyleContractTests.EveryComponentModifierNamedInARazorFile_IsStyledSomewhere`. (Failed 1, Passed 26)
3. One `*/` removed from app.css -> FAIL `StylesheetIntegrityTests.EveryComment_IsTerminated(fileName: "app.css")`. (Failed 1, Passed 26)
4. `<Icon Name="nope" />` in Index.razor -> FAIL `IconNameContractTests.EveryIconNameUsed_IsACaseInTheIconSwitch`: "Index.razor asks for \"nope\"". (Failed 1, Passed 26)

Final: `dotnet build` 0 warnings / 0 errors; 27 tests pass.

## Visual checks actually performed (Python Playwright, headless Chromium, app on :5300)
- `/` in light and dark at 1440, 768, 375: header 56px, Zen Kaku font loads, body bg tokens differ per theme (light rgb(239,234,224), dark rgb(20,21,28)); screenshots taken and viewed for light-1440 and dark-375 only (others measured, not viewed).
- 1440: sidebar 220px -> 56px collapsed (data-sidebar=collapsed) -> 220px expanded.
- Changelog dialog opens from the version chip and from the menu's "Release notes"; shows CHANGELOG.md content; closes via button.
- Theme: menu -> Dark sets data-bs-theme=dark and localStorage cloudSelfServiceTheme=dark.
- 375: rail toggle opens the drawer (`.app__sidebar.is-open`).
- Console: only error is a 404 for `/_framework/dotnet-browser-tools/hot-reload-settings.json` (dotnet-run dev probe, not app code); one WebSocket->LongPolling warning (expected, forced transport). No page errors.
- Not checked: 768 and 375 pixel-level layout beyond measurement, drawer close behaviour, light/dark appearance of the changelog dialog screenshot, keyboard navigation of the menu.
Server stopped by PID (55048); port 5300 confirmed closed.

## Review findings / concerns
- session-guard.js: RM's copy was byte-identical to the app's existing one (no diff staged); fine.
- AppMenu's trigger is a `users` icon placeholder until Task 5 supplies identity/avatar; the `.appmenu__initials` class is reused for it. Task 5 will need to restore identity/sign-out blocks (copy from RM's AppMenu).
- LoginLayout.razor was copied but is unused until Task 5.
- `app.css`/`theme.css` still contain RM-vocabulary rules and chart tokens (`--chart-*` in theme.css, verbatim as instructed); app.css has unused component rules - harmless, guards pass.
- `.dockerignore` does not exist in this app, so embedding CHANGELOG.md will not break the container build; no container build was run.
- Line endings: App.razor kept CRLF; other copied files are LF as in RM. Existing MainLayout/NavMenu files were overwritten wholesale by RM's.
- CHANGELOG.md content left untouched (still says MudBlazor/old nav; Task 12 rewrites it). CLAUDE.md `_changelog` instructions are stale (Task 12).

## Fix round 1
Commit: see `git log` (message "Extend static guards to scoped CSS and C# class literals; tidy shell").

Changes:
- IMPORTANT 1: StylesheetIntegrityTests now enumerates wwwroot/css/*.css plus every Pages/**/ and Shared/**/ `*.razor.css` (relative paths in test names); class remarks and coverage test updated (`TheGuard_CoversTopLevelAndScopedStylesheets`).
- IMPORTANT 2: element-class check also scans string literals (no backslash/newline) in `.razor` and `.cs` under Pages/ and Shared/, acting only on BEM-shaped tokens (`IsBemElement`).
- Minors: foreign-app/Task history comments removed or rewritten (ComponentStyleContractTests, IconNameContractTests, StylesheetIntegrityTests, LoginLayout, MainLayout.razor.css, ChangelogDialog(.css), ReconnectModal, ChangelogDocument); brand text now "Cloud Self Service" with `.app__brand__text { text-transform: uppercase }`; empty changelog now shows "No release notes yet." (`changelog__empty`, styled) instead of rendering nothing; AppMenu.css got a one-line "for Task 5's markup" comment on the identity/sign-out rules (not deleted).
- csproj: the repo's committed file at 101c72c is LF (not CRLF); my first commit had flipped it to CRLF. Restored to LF; `git diff 101c72c --stat` now shows 8 insertions only.

Red proofs (each reverted):
- Dropped `*/` in Shared/NavMenu.razor.css -> `StylesheetIntegrityTests.EveryComment_IsTerminated(fileName: "Shared/NavMenu.razor.css")`: "1 '/*' against 0 '*/'". Failed 1, Passed 31, Total 32.
- `private string Probe() => "nope__thing";` in MainLayout.razor @code -> `EveryComponentElementClassNamedInARazorFile_IsStyledSomewhere`: "MainLayout.razor uses .nope__thing". Failed 1, Passed 31, Total 32.

Commands: `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` -> `Passed! Failed: 0, Passed: 32, Total: 32` (before and after the red proofs); `dotnet build` 0 warnings, 0 errors.
Not re-run: the browser visual checks (changes are comments, an uppercase CSS rule, and an empty-state branch).
