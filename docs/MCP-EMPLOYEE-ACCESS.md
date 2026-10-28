> Historical 23 October acceptance record. See [MCP release — 28 October](MCP-RELEASE-20261028.md) for the current supported surface and verification limits.

# Client Portal employee MCP access

Status23October2026: the employee read foundation is deployed on the Portal hosts and RTM. Native RTM Test verifies `ssingh@technijian.com`, and Sync discovers all six implemented read tools. The existing Technijian connector now uses employee OBO and contains only a read policy, with its shared credential removed. This proves one live employee connection; full employee write and business-state coverage remains unfinished.

## Identity and authority

Chat-AI exchanges its authenticated Microsoft employee access token using Microsoft's on-behalf-of flow. The selected application organization must be Technijian. A dedicated single-tenant resource exposes `ClientPortal.Access`; it must request the `acct` optional access-token claim. CP validates the issuer, tenant, audience, scope, member claim and trusted caller. There is no application-token fallback.

Authorization uses an administrator-controlled immutable Microsoft tenant+object-ID binding to `dbo.tbl_Directory.DirID`. Native username, active state, role and location filter are loaded afresh. Email/UPN claims and model actor parameters cannot select an account. Missing/disabled/nonemployee profiles, duplicate active native usernames and conflicting claims fail closed. Enrollment rejects automatic rebinding, silently re-enabling disabled bindings and stale native-role reviews.

The new mapping migration is `database/20261023-mcp-employee-identity.sql`. It does not alter native usernames or roles and has no foreign key that would change native employee deletion behavior. Initial enrollment must use the verified Entra roster and reviewed manifest; runtime never auto-enrolls. Native account deactivation removes access on the next lookup. Standard Microsoft token validity and expiry still apply; these tests do not prove immediate Entra token revocation.

## Current curated tools

| Tool | Native authority and behavior |
| --- | --- |
| `client_portal_whoami` | Resolves the employee's own bound native identity and current role. |
| `client_portal_list_tickets` | Native My Open Tickets procedure with the authenticated directory ID; complete total and bounded projected titles. This is not unrestricted ticket search. |
| `client_portal_list_time_entries` | Own active username-owned entries, bounded dates and rows; recorded duration, drive, approval and invoice-link flags. |
| `client_portal_list_invoices` | Full invoice production menu plus global `Invoices.Get`; exact billing directory ID, active records, bounded page and stored amounts/reference flags. |
| `client_portal_list_contracts` | Contract production menu; exact client directory ID and bounded status/date/reference metadata. |
| `client_portal_billing_hours` | Invoice-report production menu plus global `TechBillable.List`; exact ticket client ID and at most31stored-local dates. Separates active/deleted/approved/invoiced/processed/Autotask groups, duration, drive and overlapping native buckets. |

Full invoices/contracts currently require native T1 menu membership; the invoice-report menu permits T1/T2. Explicit native scope Deny overrides Allow where an operation uses a native permission scope. T2/T3 are not granted full finance access merely because `Invoices.Get` returns All.

Recorded hour groups and stored invoice totals do not verify signed prices, rounding/minimums, internal-work exclusions, recurring charges, external accounting or commercial completeness. Contract signature/reference fields do not prove an executed agreement. Missing start timestamps fall outside the date report, and its response states that limit.

Employee MCP discovery/invocation excludes arbitrary stored procedures, card/payment tools and action approval. Employee sessions are bound to their tenant, object ID and client. Machine identities cannot reuse employee sessions or call employee tools. The reserved Chat-AI caller cannot use the arbitrary-procedure API as an alternate path, including when employee mode is disabled.

## Remaining activation and workflow work

The user explicitly approved the dedicated Technijian resource `1a1c6254-cc0a-4628-b712-75601ff166d4`, exposing only `ClientPortal.Access` to existing Chat-AI caller `95271648-21aa-436b-a088-7de56a6f0955`. Its resource-owned `acct` claim, v2 tokens and scope preauthorization are applied. No new Graph permission or client secret was added; the broad legacy resource and existing grants are preserved. No new explicit AllPrincipals grant row is claimed: real RTM OBO succeeds with the verified resource preauthorization. Both API hosts reserve this caller from raw procedure access; MCP has protected native SQL configuration and RTM has the protected resource setting. Never expose SQL credentials in connector configuration.

Curated ticket/time/financial writes, trusted human review for destructive/financial actions, version/idempotency protection and their full business-state acceptance are not implemented in this employee release. Their future names in the client policy are not proof that the server supports them. Keep them unavailable until implemented and verified. Existing legacy time-delete guards remain in place.

## Evidence

- CP:112tests pass; solution build0warnings/0errors.
- Chat-AI:107focused and2986full backend tests pass. PR24 merged; source revision `89d6655039cf1367aa13391040afd85898150662` deployed to RTM with unchanged frontend and dependency/runtime manifests. Existing transitive package advisories remain recorded separately.
- Actual Core/SQL and enrollment helper:53checks pass in owned LocalDB, including18identity/20financial/3own-time/12enrollment checks. Database, instance and temporary fixture files removed.
- Four exact financial/own-time read statements bind against the native SQL schema using schema-only execution; zero business rows requested and zero writes.
- Live enrollment:24reviewed unique enabled member mappings inserted among36active native employee profiles;12excluded. Independent28checks pass. These are real feature configuration mappings, not disposable test business records.
- CP rollout:8binary replacements/3configs, all3site health checks200 and123protected files unchanged. RTM native Test verifies the active employee; Sync and independent SQL readback show exactly six curated cached tools and no connector credential.
- Evidence root: `D:/Chat-AI_Ravi/output/clientportal-mcp-workflow-coverage-20261022`. Employee connection success does not constitute every user's interactive acceptance, write-workflow readiness or commercial billing acceptance.
