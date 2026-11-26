# Memory — Advanced Cloud Self Service

- [App purpose and scope](app-purpose.md) — Private storage access portal (Files/Blob browse, chunked upload, download) for customers and staff; phase 2 automation API not started
- [Files tree idea](project-files-tree-idea.md) — Parked phase 2: Files home as a subscription → account → share/container tree; staff name subscriptions; design with Dan first
- [Download progress idea](project-download-progress-idea.md) — Parked: in-app download progress via server-reported bytes in a "Transfers" panel (option A); design with Dan first
- [Phase 1 status](project-phase1-status.md) — v1.0.0 shipped 2026-11-21, private-only access proven; ⭐ App Service access restriction blocks customer cut-over (60 GB download verified)
- [Cost analysis](project-cost-analysis.md) — 2026-11-25: ~672 CAD/mo net to route today's client transfers via the portal; ~804 on the AI RG, clients save ~132; endpoints are the biggest line
- [Auth model](auth-model.md) — Entra guests incl. Gov cloud, staff auto-created with no role, Owner from config, session revalidation, dev personas
