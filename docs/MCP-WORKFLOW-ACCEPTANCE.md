> Historical 23 October acceptance record. See [MCP release — 28 October](MCP-RELEASE-20261028.md) for the current supported surface and verification limits.

# Employee MCP workflow acceptance — 23 October 2026

Application safeguards are deployed and live verified. Full employee CRUD/billing rollout remains in progress. The user explicitly authorizes production owned synthetic records followed by cleanup, preserving existing business records. No broad invoice generation, real communications, payments or native accounting exports have been exercised.

Actual endpoints: https://alpha-cp-mcp.technijian.com/mcp and https://api-clientportal.technijian.com. MCP uses the separate internal alpha-cp-apis IIS site via127.0.0.1:5001. All three run on TE-DC-TL-IIS-01/10.100.253.131 and use CP_Production_New on10.100.240.81. The alpha/RTM labels do not indicate isolation.

## Live application changes and evidence

Partial sp_Ticket_Upd calls now validate IDs/parameters and use authenticated native PATCH /api/tickets/{id}; complete legacy parameter sets retain their procedure path.11actual fixture checks passed: requested title persisted,15omitted fields remained byte-identical as native SQL JSON, another owned control ticket stayed unchanged, audit recorded and no notification flag remained. Concurrency/version-conflict behavior is still pending.

Both sp_TicketEntry_Del and stp_TktEntry_Delete are blocked through MCP even with allowWrite=true because native legacy deletion accepted an approved fixture. A safe permission-aware replacement remains pending. Both ticket-create aliases now inherit global authentication; live anonymous requests return401, authenticated invalid bodies return400 before database access.

The legacy stp_Technijian_User_List includes Password/private profile columns and its GetInActive=true OR predicate broadens the client filter. MCP now rejects that option and projects only UserID,Email,IsActive,IsDelete, dropping output parameters and rejecting unexpected schemas.7live privacy checks passed without logging/saving row values. The existing open-ticket-count composition supplies GetInActive=false and consumes exactly these four fields; its prior successful model probe was not repeated.

StoredProcedureWriteSafety preserves existing true write flags and supplements stale manifests with conservative name hints. Inventory and MCP list/describe/confirmation/result metadata agree. This is confirmation, not employee authorization or proof that a read-looking procedure has no side effects. A static source scan found101direct-write candidates among665read-labelled bodies; this remains an audit queue, not live execution proof.

## Release and test evidence

57Release tests passed. Final full CpApis.slnx Release build:0warnings/errors.18live HTTPS/Entra/MCP guard checks passed,7staff-projection checks passed, and11positive partial-edit checks passed.0model requests in this expansion.35reduced-schema LocalDB invoice checks remain passing, including7child types, exported protection, missing/null IDs, control preservation and rollback; disposable DB/instance removed.

Original server backup: D:/CP-API-deploy-backups/mcp-workflow-20261023-083628-9d9522a0. Live DLL/PDB source checksums matched the preserved local release for all existing source except the intended edited files; only two new helper files were initially added. Runtime dependency manifests were byte-identical. First deployment changed12DLL/PDB files across3sites and preserved27before-state files; second deployment changed only MCP Core DLL/PDB and preserved11other MCP files. Settings, database inventory and unchanged HTTP host were retained. The final binary hashes/stages are in CHECKPOINT.json and deployment receipts. No blind publication of the dirty tree occurred, and no commit was made.

Final CP graph:1044nodes/2478edges/83flows; tracked dirty scan19files/29symbols/5flows,MEDIUM global risk including prior WIP. CLI display still hides14symbols, so this is not a complete displayed release review. UNKNOWN method impacts were source-corroborated through both MCP registrations, API-client calls and the native InvoiceDA caller; UNKNOWN was not treated as low.

## Production fixtures and cleanup

All8test groups are cleaned (7committed fixture cohorts and1transaction-only native suite). The latest expanded independent28-check audit reports0remaining owned rows or temporary procedures, including native business audit dependencies covered by those fixtures. The initial invoice test cleanup missed one detail because native cascade constraints are disabled; it was found independently and removed with exact ID/tag guards. The first23Oct partial fixture stopped before the MCP update because the test query used ProjectScheduleId instead of native Project_Schedule_ID; its cleanup succeeded, and the corrected case passed. Normal identity advancement/security/server logs are retained.

The old January time fixture correctly failed the deployed backdate guard; a recent SQL-clock-derived window passed actual MCP create/edit. The22Oct17-check suite covers owned tickets, recent time, draft contracts and manual invoice lines/native CP totals.0.75hours×synthetic$100=$75 does not prove signed commercial pricing, complete raw billing populations or native accounting correctness.

A separate SQL capacity incident initially left D:0free. Fresh prewrite verification later showed35940MBfree and the fixture harness independently enforces at least1GB before writes. Do not duplicate or interfere with that separate recovery task.

## Native invoice fix deployed and verified

The original dbo.stp_Inv_Org_Loc_Inv_Delete_V2 is updated and live verified. Definition SHA256:85BB74D86D0555E84DC01CADF6B58A3A193ECCFC888D77559F7055BAC0644BEA. The correction explicitly removes all7child types in one transaction, locks accounting-state validation, protects exported invoices and returns0 for missing/null IDs. Object identity, signature and permissions were preserved. Deployment receipt:invoice-delete-deployment-20261023.json; migration:database/20261023-invoice-delete-safety.sql. Original rollback text is preserved.

Native-schema acceptance passed25checks in225ms, including all7child types, exported/missing/null guards, unchanged owned control records and injected rollback. All native test rows/audits were rolled back and the exact temporary procedure dropped.10post-deployment actual MCP manual-draft checks passed: RetVal1, header/detail both removed, other owned invoice byte-identical, native audit and cleanup verified. No constraints/indexes/triggers were changed or historical orphans repaired. Do not enable FKs or publish the older sandbox ticket-create body.

## Employee authority and remaining acceptance

Keep the Chat-AI connector policy unchanged until native role/client/record/action authority is enforced. Shared Entra application authentication and model-supplied UserID/CreatedBy/allowWrite do not establish employee authority. A verified server-side bridge from the signed-in Chat-AI employee is a valid internal design alongside delegated identity; native roles and client rights must be sourced, not invented. Expose curated business actions and deny raw procedure bypass; destructive/financial actions require trusted human confirmation and auditable actor attribution. Safe time deletion remains to be implemented under those checks.

The47-scenario matrix still includes role/client/tenant isolation, actor forgery, safe dependent deletion, token expiry/revocation, concurrency/idempotency, signed/effective contract pricing, normal/AH/on-site/holiday boundaries, raw/excluded work completeness, weekly/monthly deduplication and scoped native accounting acceptance. No full rollout or commercial invoice-correctness claim is supported yet.

Evidence root: D:/Chat-AI_Ravi/output/clientportal-mcp-workflow-coverage-20261022. Start with CHECKPOINT.json, DEPLOYMENT-20261023.md, workflow-coverage.csv, production-partial-patch-results-20261023.json, staff-projection-live-20261023.json and production-cleanup-independent.json.
