---
name: app-purpose
description: What Cloud Self Service does and its current development status
metadata:
  type: project
---

Advanced Cloud Self Service is a client-facing portal for Advanced-hosted environments.

Core features planned:
- SLA uptime display (Azure Monitor / Application Insights)
- Service health status per environment/service
- App Services management (restart, view logs)

**Why:** Clients want self-service visibility and basic controls without needing Azure portal access or support tickets.

**How to apply:** When building data-fetch services, target Azure Resource Management API and Application Insights. Managed identity in production, DefaultAzureCredential in dev.

Current status: Initial scaffold only — no data sources connected yet (as of June 2026). Pages show placeholder content.
