# Azure Government customers: signing in to Advanced Cloud Self Service

Advanced Cloud Self Service signs users in through Advanced's Microsoft Entra tenant, which runs in the Azure commercial cloud. People whose organisation lives in Azure Government therefore sign in as **cross-cloud B2B guests**. That needs a one-time setup in both tenants, and it only works when both sides have done their part.

This page is for the customer's IT administrator. Advanced does the mirror image on its side.

Microsoft documents the underlying feature on its **Cross-cloud settings** page: <https://learn.microsoft.com/en-us/entra/external-id/cross-cloud-settings>. Read it if a step below doesn't match what you see in the portal.

## Your side (customer IT)

1. **Enable the commercial cloud.** In the Microsoft Entra admin center go to **External Identities > Cross-tenant access settings > Microsoft cloud settings** and enable **Microsoft Azure Commercial**.
2. **Add Advanced as an organization.** Under **Cross-tenant access settings > Organizational settings**, choose **Add organization** and paste Advanced's tenant ID:

   `<Advanced tenant ID>`

   Advanced support gives you the value. Paste the ID rather than searching by domain: a lookup by domain does not work across clouds.
3. **Allow outbound B2B collaboration** for the users or groups who need the portal. Without it, your users cannot be invited into Advanced's tenant.
4. **Send Advanced the users' UPNs**, not their email addresses. The UPN is the sign-in name in your Entra directory (it often, but not always, matches the email address). Cross-cloud invitations are matched on the UPN.

## Advanced's side

Advanced support then:

1. Enables **Microsoft Azure Government** in Advanced's Microsoft cloud settings.
2. Adds your tenant ID as an organization and sets the inbound B2B collaboration settings.
3. Registers each user in the portal by UPN, and invites each one to Advanced's tenant by UPN.

## What your users do

Each user receives an invitation and redeems it. After that they open the portal and choose **Sign in with Microsoft**, using their normal work account. They can browse only the storage Advanced has granted to your organisation.

## Checking it worked

- The user can redeem the invitation without an "unable to invite" or "tenant not found" error.
- The user signs in and sees the Files page. If they see "Access Denied", tell Advanced support the UPN you sent them; the user must be registered under exactly that UPN.
