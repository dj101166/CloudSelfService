---
name: project-cost-analysis
description: 2026-11-25 cost projection for routing client transfers through the portal - about 672 CAD/month net, almost all landing on the AI resource group; private endpoints are the biggest line
metadata:
  type: project
---

**Result (2026-11-25, list prices, CAD):** routing every client transfer seen today (SAS traffic, 25 Oct-24 Nov 2026, ~5.9 TB/month, 4.5 TB of it uploads) through the portal costs about **672 CAD/month net**.
- **AI resource group (AUS-DEV-SUB) +804:** private endpoints 358 (35 at 10.22 each), Private Link + App Gateway + upload hop 276, internet egress taken over from clients 171.
- **Client stamps -132 in total:** their download egress moves to the App Gateway; the only new client-side line is the West US -> East US download hop (+39).
- Biggest per-client chargebacks: HB ~196, COB ~142, FW ~88; most others 10-60.
- Connecting all 168 account/service pairs instead of the 35 with traffic would add ~1,360/month: connect only what is used.
- Baseline: 56 TB across 84 client accounts (50% West US, 49% East US; FW alone 42%); billed storage up 32% Dec 2025 -> Oct 2026.

**Data:** `D:\tmp\css-cost-analysis\` (CSV + scripts, not in git). The report is `storage-portal-cost.html` there. Publishing it as an Artifact failed because this machine's session used apiKeyHelper; it needs a claude.ai login.

**Why:** Dan needed the projection to decide whether the tool's cost is acceptable, and whether to pass it through to clients.

**How to apply:**
- When onboarding a client, budget ~10.22 CAD/month per service endpoint, plus ~0.05 CAD/GB moved.
- Re-run the traffic split before any chargeback decision: Azure Monitor kept only 30 days of daily data.
- Billing attribution (endpoint owner pays) is from Azure's rules; confirm it on the first invoice after a pilot.

Related: [[project-phase1-status]].
