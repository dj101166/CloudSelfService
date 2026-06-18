# /webapp-testing

Interactive Playwright scripting for Advanced Cloud Self Service at `http://localhost:5300`.

## App details
- Base URL: `http://localhost:5300`
- No auth in development mode (no login step required)
- Theme: light/dark toggle in topbar

## Blazor timing rules
- Always wait for `.page-title-main` to be visible before asserting page content
- Navigation is instant (Blazor SPA), but components are async — wait for specific elements
- Use `page.wait_for_selector(".page-title-main")` after nav

## Nav labels (sidebar)
- OPERATIONS section: Dashboard, Service Health, App Services

## Pages
| Route | Title |
|---|---|
| `/` | Dashboard |
| `/service-health` | Service Health |
| `/app-services` | App Services |
