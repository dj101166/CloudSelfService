---
name: auth-model
description: How sign-in, admission, roles and dev personas work in Cloud Self Service
metadata:
  type: project
---

Auth is Microsoft Entra OIDC only (single tenant). GitHub OAuth and the service-signin endpoint are gone.

- **Customers** are B2B guests, including cross-cloud Azure Government guests (see `docs/gov-cloud-guest-setup.md`). They are admitted only if an admin registered their UPN under a customer; otherwise Access Denied. A first sign-in must come from the customer's recorded home tenant.
- **Staff** are recognised by `Entra:StaffEmailDomain`, auto-created on first sign-in with no role, and see "Access pending" until an admin assigns Staff or StaffAdmin.
- **Owner** comes from `Authorization:Owners` (config) and is never stored.
- **Session revalidation:** live circuits re-check the user every `Auth:RevalidateSeconds`, so deactivation or demotion bites mid-session. Idle timeout 60 min, absolute 12 h.
- **Dev personas** (Development + `Auth:Provider=Dev` only, via `/dev/signin?as=`): staff-owner, staff-admin, staff, staff-none, customer-read, customer-contribute, customer-full, stranger. They run the real admission code. `Storage:UseFake=true` seeds an in-memory backend. The app refuses to start with either outside Development.

**How to apply:** do not add shared-secret or API-key sign-in. Authorisation changes need a security-auditor pass.
