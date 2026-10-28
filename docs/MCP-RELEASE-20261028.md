# Client Portal employee MCP source release — 28 October 2026

This commit records the deployed employee MCP implementation and its required API, catalog-safety and existing CardPointe dependencies. Earlier work was held in local source files; the runtime, tests and final database migrations are now versioned together. It contains no local configuration, credentials, database inventory, business fixtures, build output or diagnostic logs.

## Supported employee surface

The Technijian connection exposes 29 curated tools using the signed-in employee's delegated Microsoft identity. Native employee mapping, roles, menus and record scope control access. Raw stored-procedure and CardPointe tools remain unavailable to this delegated employee surface; existing legacy registrations remain preserved for their separate access path.

The supported surface covers ticket lists, activity and history; employee lookup and EOD; time entries and billing-hour reports; client and option lookup; invoice and contract reads; action status; and guarded create/edit/delete proposals for tickets, time entries, draft invoices and draft contracts. Pagination and text continuation preserve completeness signals. Missing, denied or failed results are not evidence of zero or successful application.

Writes use expiring, employee-owned proposals and authenticated human review. Native snapshots and action versions protect against stale reviews; request IDs make application idempotent. A chat acknowledgement does not approve or apply a proposal. The native operation must succeed and its readback must match before success is returned.

Draft invoice and contract metadata support does not implement commercial pricing verification, contract activation/signing, payments or accounting export. Use the native Portal for those operations.

## Contract lookup and diagnostics

`database/20261028-mcp-contract-ticket-reference-index.sql` adds a nonunique `ContractID` index including `TicketID` on the native ticket table. The reviewed native lookup fell from 81,554 logical reads in retained Query Store history to 235 in the measured post-index query, with three ticket-table reads. Existing index definitions and native procedures were preserved. A retained client-aborted execution correlates to the original failed test; the exact original exception number and cancellation trigger remain unverified.

The HTTP host injects a private action logger under the machine's common application-data directory at `Technijian/ClientPortalMcp/Diagnostics`. The directory is outside the website and requires an administrator-provisioned ACL for the MCP worker, administrators and SYSTEM. Event 6201 records UTC time, operation, request/action IDs, category, status and SQL error number only. Payloads, exception text, SQL, employee claims and review tokens are excluded. Daily files rotate at 5 MB with one previous file; owned dated files older than the retention window are removed. A failed diagnostic sink does not change the business action outcome.

## Verification recorded for this source

- 527 Client Portal automated tests passed, including 16 diagnostic/SDK/redaction/rotation/retention checks.
- 155 public-tool-to-native pipeline checks passed against owned synthetic fixtures in a rolled-back transaction; an independent connection verified zero remaining fixture rows.
- Eight isolated index/query/concurrency checks passed; disposable LocalDB resources were removed.
- Real employee Microsoft SSO verified draft-contract creation and approval, a controlled SQL lock failure without automatic retry or false success, explicit same-request recovery, review approval and native rename readback.
- Four final MCP DLL/PDB files were deployed; 130 other files were unchanged and three affected site health checks returned HTTP 200.
- 42 deployed PDB source documents matched; the retained source snapshot contains 36 non-generated runtime documents plus supporting tests, project files and SQL.

The live test-contract deletion review and its final owned-fixture cleanup still await the user's separate browser action-time confirmation. This source commit is not a cleanup receipt. The broader 29-tool scenario matrix is in `client-portal-mcp-coverage-20261027.json`; it distinguishes deterministic/native checks from representative real-SSO conversations. It is finite coverage, not proof of every phrase, data combination or employee sign-in.

## Database and deployment handling

The final 23 October migrations record employee identity, action audit/review storage, ticket mutations and native invoice-delete safety. Review target guards, native dependencies and current definitions before applying any migration. Source publication is not an instruction to rerun already applied production changes. The earlier candidate/sandbox migrations and test harnesses are deliberately excluded.

Keep local settings and secrets in protected host configuration. This commit does not change production configuration, permissions, deployment state or business records. The existing public GitHub remote has not been used to publish this internal source release.

Earlier dated acceptance documents are historical evidence; this document describes the current source boundary.
