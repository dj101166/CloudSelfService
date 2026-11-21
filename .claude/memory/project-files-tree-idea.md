---
name: project-files-tree-idea
description: Agreed and parked for phase 2: Files home as a Storage-Explorer-style tree (subscription → storage account → share/container), with staff-entered subscription display names. Design with Dan first.
metadata:
  type: project
---

**Status: agreed, parked for phase 2 (Dan, 2026-11-21).** Write a short design for Dan's approval before building, the same way as phase 1.

**Problem:** Files home (`Pages/Index.razor`) shows one card per share or container. Staff get one level of grouping (by customer, with "Advanced internal" last); customers get a flat list. No card shows the subscription or storage account. Once every customer target is registered the page becomes too long to scan, and nobody can tell which client or account a card belongs to.

**Agreed shape, modelled on Azure Storage Explorer's tree:**
```
▾ Contoso Utilities — Production      (subscription)
   ▾ auscacprdstacontoso01            (storage account)
       exports                        file share
       logs                           blob container
▸ Advanced — RD Migration             (subscription)
```
- Three collapsible levels: subscription → storage account → share or container. Collapse state is per viewer, in localStorage (try/catch; the page must work without it).
- The existing filter box searches all three levels.
- Customers see the same tree, pruned to their own targets.
- Clicking a share or container still opens `/files/{targetId}/...`. Its breadcrumb gains the subscription and account levels.

**Subscription names (Dan's decision):** staff enter them on the admin side, alongside the subscription ID, as a customer and/or subscription display name (e.g. "Contoso Utilities — Production"). Customers see that name, never a raw GUID.
- The app must not look names up in Azure: the managed identity would need ARM Reader, which widens its rights.
- Likely data change: a small `subscriptions` table (id GUID, display name, optional customer link), or a subscription display-name field on the customer record. Decide in the design.
- Targets already store `SubscriptionId`. Show a target whose subscription has no name under its ID, with a staff-only "name this subscription" prompt.

**Constraints:**
- Build the tree only from `StorageAccessService.ListReachableTargetsAsync`, so there is no new path to storage and no change to authorisation.
- Customers must never see another customer's subscriptions or accounts, even as an empty node.
- Use `StoragePath.DisplayName` for every rendered name.
- Keep the existing `data-testid` hooks where possible (`target-card`, `data-target-name`). Add new ones for the tree levels and update `webapp-tests/`.

**Why:** all customers' shares and containers are about to be registered, and Dan needs to see at a glance which client, subscription and account he is opening.

**How to apply:** when the Files home, target grouping or "Storage Explorer-like" navigation comes up, start from this shape and the staff-named subscriptions.

Related: [[project-phase1-status]], [[project-download-progress-idea]].
