---
name: project-download-progress-idea
description: Agreed but parked: in-app download progress via server-reported bytes in the existing upload panel (option A). Not started; design and plan it with Dan first.
metadata:
  type: project
---

**Status: parked (Dan, 2026-11-21).** Dan wants in-app download progress like Azure Storage Explorer's activity list, but chose to hold off. When it's picked up, write a short design for approval first, then a plan, the same way as phase 1. Don't build it straight away.

**Agreed approach: option A, server-reported progress.**
- Downloads keep going through the browser's normal save (`<a download>` → `GET /api/files/{id}/download`).
- The download endpoint already counts bytes written in its streaming loop. Publish that progress per user and request to the page, through the Blazor circuit or a small status endpoint.
- The existing upload panel becomes a "Transfers" list showing downloads and uploads together.
- The link needs a per-click transfer id (for example a query parameter) so the page can tie the progress back to the click.
- Cancel from the panel aborts the request server-side, using the existing `ctx.Abort` path. The browser then marks the download as failed.

**Known limitations (accepted):**
- The browser's own download bar still appears as well.
- Progress shows bytes sent by the server. It can run slightly ahead of what has reached the user's disk.

**Rejected or deferred alternatives:**
- **B: the File System Access API** (`showSaveFilePicker` plus a page fetch with Range resume). It gives true progress, pause/resume and no browser bar, but works in Chrome/Edge only and asks for a save location every time. Revisit only if customers ask for pause and resume of large downloads.
- **Reading the download into page memory.** Impossible at the sizes involved: a 60 GB file was tested on 2026-11-21.
- **Service-worker streaming tricks.** Too fragile across browsers.

**Why:** Dan raised it after a successful 60 GB download, because Storage Explorer and the Azure portal both show transfer progress in their own interface. He wants it without making the design complicated.

**How to apply:** when download progress, transfer lists or Storage Explorer parity come up, start from option A. Watch these constraints:
- every progress message must respect the single storage gate and per-request authorisation;
- a user must never see another user's transfers;
- downloads already re-authorise every 64 MiB / 60 s, and progress must not add per-chunk database writes.

Related: [[project-phase1-status]].
