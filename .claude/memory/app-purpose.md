---
name: app-purpose
description: What Cloud Self Service does and its current development status
metadata:
  type: project
---

Advanced Cloud Self Service is a private storage access portal. Customers (Entra B2B guests, including Azure Government guests) and Advanced staff sign in with Microsoft and browse, upload (chunked, resumable, up to 50 GB) and download files in Advanced-hosted Azure Files shares and Blob containers, through one access gate. No storage key, SAS or URL ever reaches the browser.

Staff admin covers Customers, Storage targets, Staff roles (Owner only) and an Audit log with CSV export.

**Why:** Customers need file exchange with Advanced-hosted environments without Azure portal access, storage keys or support tickets.

**How to apply:** Any storage access goes through `StorageAccessService`. The old SLA / service health / app-service-restart idea is not built; restart rules in `.claude/rules/security.md` are kept for a future feature.

Status: Phase 1 (file portal) is complete and release-ready as v1.0.0 (branch `feat/private-storage-access`). Phase 2 (an automation API for programmatic transfers) is not started. Operators: `docs/operations.md`.
