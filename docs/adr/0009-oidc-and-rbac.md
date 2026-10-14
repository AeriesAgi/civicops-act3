# 0009 — Validated staff OIDC, explicit RBAC and scoped service identities

- Status: accepted; live municipal issuer and HTTPS deployment remain unverified.
- Date recorded: 2026-10-08 (SAST).
- Scope: authentication, authorization, session and credential lifecycle.

## Context and Problem Statement

Human approval needs a trustworthy staff identity; submitted actor names and demo role pickers cannot supply it. Municipalities also need noninteractive integrations whose tokens cannot inherit staff dispatch or administrator authority. Every endpoint must have a reviewable permission, including public resident routes.

## Decision Drivers

- Reuse a maintained OIDC handler with standard protocol validation.
- Map only explicitly trusted issuer roles/groups and municipality claims.
- Deny unmapped endpoints and separate service scopes from staff decisions.
- Keep credentials outside browser storage and invalidate changed/revoked local users.

## Considered Options

- Trust frontend role/actor labels.
- Implement a custom identity protocol and JWT parser.
- Use ASP.NET Core OIDC/cookie handlers plus explicit CivicOps policy/resource checks.

## Decision Outcome

Choose [EnterpriseIdentity.cs](../../products/civicops/Services/EnterpriseIdentity.cs), [IdentityOptions.cs](../../products/civicops/Services/IdentityOptions.cs) and [IdentityEndpoints.cs](../../products/civicops/Services/IdentityEndpoints.cs). Entra, Google Workspace and generic presets configure one runtime issuer. The handler uses authorization code with S256 PKCE, discovery/JWKS, nonce/correlation and issuer/signature validation. Explicit role/group maps and a trusted existing tenant are required; unmapped identity cannot sign in. Crew identity additionally binds a configured crew. Entra's preset requires a specific directory; Google hosted-domain restriction checks the validated claim rather than inventing group membership.

The browser receives a Secure, HttpOnly, SameSite=Lax `__Host-` opaque cookie reference. Claims/tickets remain in the memory-only server store, with no sliding refresh; access/refresh tokens are not persisted. An ID token remains server-side only for logout hints. Ephemeral data protection deliberately expires sessions and in-flight identity/CSRF handshakes on restart. Cookie mutations and bootstrap/demo login require CSRF.

[PermissionMatrix.cs](../../products/civicops/Services/PermissionMatrix.cs) defines one named policy per mapped method/route; unmapped routes fail startup and fallback denies access. [EnterpriseAccessMiddleware.cs](../../products/civicops/Services/EnterpriseAccessMiddleware.cs) adds tenant/crew and high-hazard checks. [ServiceAccountStore.cs](../../products/civicops/Services/ServiceAccountStore.cs) issues once-shown random tokens, stores hashes, binds explicit scopes/tenant/expiry and supports rotation overlap/revocation. No service scope grants staff decisions or admin. [BootstrapUserStore.cs](../../products/civicops/Services/BootstrapUserStore.cs) supports controlled local onboarding with audited role edits and session invalidation. Legacy environment clients remain limited default-tenant bootstrap identities.

## Consequences

Authentication and route/resource permissions have one auditable source. Local sessions are lost on restart and cannot support multi-node deployment. OIDC group membership is a sign-in snapshot until expiry/re-sign-in; there is no Graph overage resolution, directory polling or continuous-access evaluation. The owner must supply issuer MFA/access policy, complete claims, safe redirect registrations, HTTPS/trusted ingress and account lifecycle procedures.

Explicit demo identity is labelled synthetic/unverified and disabled outside demo; anonymous demo supervision never grants admin. Resident reference lookup is not verified resident identity. Independent municipal issuers and provider OAuth refresh need further deployment design/engineering.

## Confirmation

Implemented and tested locally: [OidcIntegrationTests.cs](../../tests/CivicOps.Tests/OidcIntegrationTests.cs) runs the actual handler against a local discovery/JWKS/token issuer, including PKCE and rejection cases. [IdentityTests.cs](../../tests/CivicOps.Tests/IdentityTests.cs), [RbacTests.cs](../../tests/CivicOps.Tests/RbacTests.cs), [CsrfTests.cs](../../tests/CivicOps.Tests/CsrfTests.cs) and [ServiceAccountTests.cs](../../tests/CivicOps.Tests/ServiceAccountTests.cs) cover mappings, policy enumeration, sessions, scopes and rotation. Real Entra/Google sign-in/logout and production ingress remain owner-pending. [RBAC setup](../RBAC.md) is the detailed contract.
