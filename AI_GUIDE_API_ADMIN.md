# YemenDrive API and Admin — AI Development Guide

**Purpose:** working reference for AI-assisted changes in this repository. Audited against the checked-out source on 2026-09-23. This describes observed conventions, not a claim that every feature is complete.

## Source of truth and scope

- Repository: API, static administration UI, database, reusable services, and shared contracts.
- Start with `README.md`, then inspect the relevant implementation, migrations, tests, `CODEX_PROGRESS.md`, and `CODEX_WORK_LOG.md`. Existing docs can lag the implementation; source and tests determine current behavior.
- The working tree may contain user changes. Inspect `git status` before editing and preserve unrelated changes.
- This project’s admin is the static SPA under `src/YemenDrive.Api/wwwroot`; it is not an ASP.NET MVC/Razor admin project.

## ARCHITECTURE

Solution projects and intended responsibilities:

| Project | Responsibility |
|---|---|
| `YemenDrive.Api` | ASP.NET Core host, HTTP routes, authentication/session context, static admin assets and server integrations. |
| `YemenDrive.Application` | Feature-oriented request/response models and feature services (typically one directory per domain such as `Rides`, `Wallets`, `Safety`, `Accounting`). |
| `YemenDrive.Services` | Generic model-service contracts, shared operation bases and model/operation dispatcher. |
| `YemenDrive.Database` | EF Core entities/context, SQL Server provider, connection configuration and migrations. |
| `YemenDrive.Shared` | Shared API result/request, exceptions, security and small cross-project contracts. |

Typical model-operation path:

`HTTP endpoint -> ModelServiceDispatcher -> feature service -> YemenDriveDbContext -> ApiResult`

Feature services are discovered from the `YemenDrive.Application` assembly and registered as `IModelService`. `ModelService<TModel>.ModelName` is the request model’s CLR type name (for example `UserModel`); the matching operation service commonly has the domain name (`User : OperationsService<UserModel>`). A new API model/service pair usually does not need a new controller, but verify whether the feature is actually reachable through the dispatcher and authorized endpoint before assuming it is.

**Important observed variation:** this is not strict Clean Architecture in every code path. `Program.cs` also maps focused minimal-API routes (notably media and safety-recording workflows), and some specialized behavior lives in the API host. Keep new code near the existing owning feature and avoid duplicating logic across the dispatcher and direct routes.

## CODING STYLE

- C# / .NET 9, nullable reference types and implicit usings enabled; file-scoped namespaces, modern records, collection expressions, and primary constructors are present.
- Use descriptive domain-specific names, async I/O, and pass `CancellationToken` through HTTP, services, EF Core, and file/network operations.
- Use `ServiceException(code, safe Arabic message)` for expected business failures handled by the shared service result path. Do not return internal exception/SQL/secret details to clients; log safe diagnostics server-side.
- Keep user-facing API messages concise and Arabic when that matches the existing feature. Preserve stable machine-readable error codes.
- Minimize scope: inspect the analogous feature, endpoint contract, dependency registration/discovery and tests before changing an operation.
- Never treat a successful build as proof that a financial/state transition is correct. Add or update a focused test for important behavior.

## MODEL CONVENTIONS

- Request models normally live beside the feature in `YemenDrive.Application/<Feature>/` and end in `Model` (e.g. `UserModel`, `RideModel`). Responses/reports have explicit names such as `UserResponse`, `RideReport`, or `SafetyAdminReport`.
- Prefer typed records/models; keep API input separate from EF entities when an existing feature already does so. Do not expose secrets, password hashes, storage paths, or internal persistence entities in responses.
- `Entity`/database entities use integer IDs in this repository. Verify the actual model and Postman/API contract; do not copy UUID assumptions from other projects.
- Derive the authenticated actor from `ICurrentUserContext` / the bearer identity for self-scoped operations. Do not trust a client-supplied `userId` for authorization.
- Add validation and ownership checks before writes. Existing `ModelService<T>` supports `ValidateAsync`; feature services may also validate within their operation implementation.

## API CONVENTIONS

- Main client operation endpoint: `POST /api/execute` with JSON `{ "model": "...Model", "operation": "...", "data": { ... } }`.
- Main admin operation endpoint: `POST /api/admin/execute` after admin authentication. Admin and customer authorization paths are not interchangeable.
- `ModelServiceDispatcher` resolves model names and supported operation names case-insensitively. Implement/advertise each operation explicitly; do not assume generic CRUD operations are automatically safe or supported for a feature.
- Result envelope is `ApiResult` (`success`, `code`, `message`, optional `data` / `errors`). Use the established result helpers and stable codes. Some dedicated endpoints can have their own response/status shape—match their existing endpoint rather than wrapping everything by guesswork.
- JSON uses the web serializer defaults (camelCase). Keep enum representation and nullable/optional semantics compatible with Flutter and Postman collections.
- Preserve existing dedicated routes where present (for example auth, map search/route, safety recording chunks, health/media). SignalR and HTTP contracts must be checked together when changing real-time ride state.
- Authorization is part of the contract: enforce user ownership and role at the service/route boundary. Admin endpoints must remain admin-only.
- Google/provider credentials are server-side configuration only. Never return them to the admin page or mobile client, commit them, log them, or put them in a response DTO. Uploaded media and sensitive safety recordings have different access rules; recordings must not become public static files.

## UI / DESIGN SYSTEM (ADMIN)

- Admin is a static single-page interface: `src/YemenDrive.Api/wwwroot/admin.html` defines page structure, `admin.js` owns navigation/rendering/forms/API interactions, and `admin.css` owns layout, colors, spacing, responsive behavior, and components.
- Keep the existing Arabic RTL layout, sidebar navigation/view IDs, shared table/form/modal patterns, and responsive states. Add a view consistently in markup, navigation, rendering, and event wiring; do not build an unrelated second UI stack.
- Reuse the shared request/auth/error helpers in `admin.js`. Keep permission/error/loading/empty states clear, and disable duplicate submissions during writes.
- Escape/encode server-provided text before inserting into HTML. Avoid string-building markup with untrusted values; preserve CSRF/auth behavior for administrative mutations.
- Keep credentials out of HTML/JavaScript/CSS. UI visibility is not authorization—enforce access in the API too.

## DATABASE CONVENTIONS

- SQL Server through EF Core `YemenDriveDbContext`; entities and DbSets are in `YemenDrive.Database`. Schema changes belong in a new, ordered migration under `src/YemenDrive.Database/Migrations/`, with the model snapshot updated by EF tooling.
- Inspect the current entity, fluent configuration, migration history and SQL scripts before changing schema. Do not edit an already-applied migration to represent a new change.
- Preserve established integer identity keys, explicit foreign keys/indexes/uniqueness and delete behavior. Add a database constraint/index when it is part of a correctness guarantee (not only an application-side check).
- Persist timestamps in UTC (`...Utc`). Use `decimal` for currency; retain the project’s defined precision/scale and currency code. Do not use floating point for money.
- Keep each multi-row financial/state change transactional and idempotent where requests may be retried. Posted general-ledger entries and their lines are immutable: correct them with reversal/compensating entries, never edits/deletes.
- Account balances and statements must agree with posted ledger entries and wallet transaction records. Do not introduce direct client-side balance writes or bypass the accounting posting services.
- Never run migrations or destructive SQL against a configured/user database without explicit task scope and a verified target. Do not place database passwords, JWT keys, map keys or admin bootstrap secrets in tracked settings files.

## TESTING AND CHANGE CHECKLIST

- Existing tests include `tests/YemenDrive.FinancialIntegrationTests`; check its project and fixtures before modifying accounting behavior. Postman collection is under `postman/`.
- The solution contains the application projects but not `tests/YemenDrive.FinancialIntegrationTests`. That test is a standalone console executable using an isolated temporary SQL Server LocalDB database; run it with `dotnet run --project tests/YemenDrive.FinancialIntegrationTests` where LocalDB is available. `dotnet build YemenDrive.sln` builds the solution; `dotnet test YemenDrive.sln` alone does not run this excluded executable test project.
- For an API feature, cover success, validation/failure, authorization/ownership, idempotency/concurrency when relevant, and persistence invariants. For admin, verify the page is reachable and mutation/error states still work.
- Before handoff: report changed files, migration requirements (if any), exact checks run and any untested integration. Do not claim a feature is production-ready solely from mocked/unit coverage.

## NON-NEGOTIABLE PROJECT RULES

These are constraints enforced by the current dispatcher, database model, or integration tests—not a separate repository policy/linter:

- A model operation is callable only when a matching `IModelService.ModelName` is discovered and the service lists that operation. The dispatcher does not infer arbitrary CRUD support from a model name.
- `ICurrentUserContext` supplies the actor ID used by account-scoped features; admin execute additionally checks the active `Admin` role. Feature services perform the actual ownership/status validation.
- Published ledger entries are not edited or deleted. The database context blocks changes to posted entries and journal lines; the accounting service creates reversal entries for correction.
- Financial posting validates balanced positive debits/credits, active posting accounts and currency compatibility. Relevant multi-record payment flows use a database transaction and idempotency keys/indexes as implemented by each flow.
- The API’s database setup path has a destructive `RecreateSchema` branch that calls `EnsureDeletedAsync`; the test runner also deletes its uniquely named temporary test database in `finally`. Those are the only destructive paths identified here; do not confuse either with ordinary migrations.

No repository-wide analyzer or policy file was found that enforces every existing paragraph in this guide. Treat only the constraints above as directly evidenced runtime/database/test invariants; other practices are local patterns or explicit user instructions.

## SOURCE PRIORITY

- For what is implemented in the checked-out version, use `src/` and its current tests as primary evidence. Use `YemenDriveDbContext`, the current migration chain/snapshot and the actual route/service registration to establish persistence and API behavior.
- Use Postman request bodies and scripts as concrete examples of the exercised client contract, not proof that every operation is covered. Use README and progress/work-log files for setup/history, but verify them against source when they disagree.
- This repository has no separate API-contract document in its root. The Postman collection and feature model/service implementations are the practical contract references.
- The current README’s table-count/feature summary predates later migrations and current `DbSet` additions. Do not use its old count as the current schema inventory.
- This ordering follows the user’s explicit instruction for this guide; it is not currently encoded in an automated repository check.

## PATTERN CONSISTENCY

Several implementation styles coexist; the checked-in code does not use a single endpoint or persistence pattern everywhere:

- Most feature operations use `ModelService<TModel>` / `OperationsService<TModel>` and are dispatched by `{ model, operation, data }`. `AuthenticationEndpoints` and focused routes in `Program.cs` instead use explicit minimal API endpoints.
- Most model-operation responses use `ApiResult` inside HTTP 200 responses, including logical failures. Some direct endpoints return other HTTP status codes or plain route results. Match the specific existing endpoint rather than assuming one error/status convention applies to every route.
- Database evolution uses EF Core migrations and the model snapshot; root `stage3_*.sql` files are also present as manual migration/integrity scripts. Runtime setup and startup call EF `MigrateAsync`; the SQL scripts are not automatically run by that code path.
- Admin JavaScript mixes helper-based event listeners with inline `onclick` handlers in generated rows. It uses both `innerHTML` templates and safer `textContent`/DOM creation in individual features.
- There is one custom financial integration executable, not a uniform test framework across all projects. The admin JavaScript has no corresponding automated test project in this repository.

## ADMIN JAVASCRIPT CONVENTIONS

- The UI is plain browser JavaScript in one `admin.js` file, loaded by `admin.html`; no module bundler or frontend state framework is configured.
- A top-level mutable `state` object stores the current view and fetched feature collections. Feature loaders call `execute(...)`, update their collection, then render table rows. `loadView(...)`/navigation controls the displayed view and reloads data; this is an in-memory UI cache, not persistent business state.
- Shared helpers include `$`/`$$` DOM selectors, `escapeHtml`, `number`, `date`, `badge`, `toggleEmpty`, `execute`, and `toast`. Existing renderers primarily build template strings and assign `innerHTML`; many use `escapeHtml` for server text. Some actions embed numeric IDs in inline handlers after `Number(...)`, while static controls use `addEventListener`.
- Forms are described in `formConfigs` and rendered/submitted by shared modal/form logic. Feature-specific operations that need special payloads or workflow use dedicated functions rather than forcing them into generic CRUD forms.
- Admin login stores `accessToken` in `localStorage` under `yemendrive_admin_token`; `execute(...)` sends it as a Bearer header. `showAdminLogin(...)` removes the stored value on the API’s admin-authentication error. Renderers also use `textContent` for status/toast labels where HTML is unnecessary.
- Maintain the actual mixed pattern when extending a nearby feature. Do not assume every dynamic `innerHTML` interpolation is escaped just because `escapeHtml` exists; inspect the exact render path and how that value is constrained.

## STATE MANAGEMENT

- Persisted business state is in `YemenDriveDbContext`/SQL Server. Model services and direct endpoints read/write that database; admin arrays and form state are presentation caches and are reloaded from API responses.
- Most request handling is stateless between HTTP calls. Exceptions visible in the source are `OtpChallengeStore` (singleton in-memory concurrent dictionaries for challenges/verification) and `DatabaseConfigurationStore` (singleton in-memory current settings persisted as a JSON file). OTPs and verification challenges therefore live in the current API process; the source shown does not persist them to SQL.
- `admin.js` state, safety action busy-set, active audio object URLs and map timer are page-memory state. Only the admin access token is persisted by the browser, in `localStorage`.
- API startup also attempts `SeedAdministratorAsync` when database settings exist, applies migrations, and handles seed failure by logging a warning. This is a startup side effect, separate from request-scoped feature state.

## BACKWARD COMPATIBILITY

- `ModelServiceDispatcher` matches model and operation names case-insensitively. `OperationsService<TModel>` treats `add` and `create` as the same add handler; feature services may add custom operations by overriding their advertised operation collection and implementation.
- `ModelService<TModel>` accepts the legacy account-scope marker `{"id":"current"}` and strips that string field before typed deserialization; current identity is expected to come from `ICurrentUserContext`.
- The generic execute API coexists with explicit auth, setup, media, map/safety and audio routes. `README.md` and Postman include older and newer operation examples; adding a dedicated route does not remove existing model operations unless the code explicitly does so.
- Admin view code and Postman examples contain numeric enum values, integer resource IDs and compatibility fallbacks for alternate display fields. Preserve the actual type/meaning used by the affected endpoint; do not globally convert enums or IDs based on a single example.
- No centralized API version negotiation or deprecation registry was found. Compatibility is maintained locally in the model/service, endpoint, admin renderer and request collection.

## SECURITY

This section records security behavior and exceptions found in source; it does not claim these mechanisms are production-grade:

- Current login responses create `demo-access-{userId}` tokens. `HttpCurrentUserContext` extracts the numeric suffix from that string; `Program.cs` does not configure JWT signature validation or standard authentication middleware. Admin execute checks the resulting user is active and has the Admin role, but the current token format is not a signed bearer-token implementation.
- `/api/auth/login` trusts the request’s `isTrustedDevice` boolean to decide whether to issue an OTP challenge. `OtpChallengeStore` stores challenges in memory and currently writes the full OTP and destination to both `ILogger` and `Console`; this is an explicit sensitive-data logging exception in the current code.
- `DatabaseSettings` includes `Password`, and `DatabaseConfigurationStore` serializes the complete settings object to `src/YemenDrive.Api/data/database.settings.json`; no encryption is performed by that store’s serialization path. Do not inspect or reproduce the actual local settings contents in documentation or output.
- The admin access token is stored in browser `localStorage`, not an HttpOnly cookie. The static admin UI also performs an `IsAdminAsync` role check for admin execute and related admin actions; hiding a screen in JavaScript is not the server-side check.
- Uploaded images are placed under web root `uploads`; safety audio chunks are stored under API `data/safety-recordings` and accessed through purpose-specific routes. Their public/private treatment is intentionally different in `Program.cs`.
- Google Maps server credentials are bound from server configuration and used by `GoogleMapsGateway`; they are not meant to be returned to Flutter/admin. Conversely, the source also seeds a fixed experimental administrator credential when the configured database has no matching account. These are current implementation facts, not a security guarantee.

## FINANCIAL INVARIANTS

- `AccountingPostingService` is the dedicated general-ledger posting path used by the accounting/ride posting services. It requires at least two lines, one positive side (debit or credit) per line, equal positive debit/credit totals, active posting accounts and a matching currency.
- Posting uses a serializable transaction when it owns the transaction; when called inside a payment transaction it reuses that transaction. Source type/source ID/type uniqueness and request-specific idempotency keys protect the retry cases that each feature implements.
- A reversal references an already-posted entry, swaps each original line’s debit/credit and is limited to one reversal by the unique index. A posted journal entry and its lines are immutable; the change tracker and SQL check constraints enforce this independently of the UI.
- EF configuration also enforces relevant invariants such as non-negative wallet balance, positive wallet/payment amounts, unique successful payment per ride, at most one active approval/request per ride, and unique account/source references. The exact constraints are in `YemenDriveDbContext` and migrations.
- Financial integration scenarios exercise account provisioning for existing/new users and service kinds, balanced/immutable entries, wallet payment atomicity/idempotency, cash shortage/excess, customer confirmation, cancellation/refund choices and administrative settlement. These tests evidence only those scenarios; they do not prove every financial endpoint is routed through the ledger.
- Some older wallet/business operations coexist with the new ledger implementation. Verify the concrete service and its test before assuming every wallet transaction or legacy operation already posts a journal entry.

## LOGGING AND OBSERVABILITY

- `Program.cs` clears the default logging providers and adds Console logging. Startup administrator seeding logs a warning if it fails. `GoogleMapsGateway` uses structured `ILogger` warnings/information with HTTP status/result counts.
- `ModelService<TModel>` turns JSON/service/unexpected exceptions into `ApiResult` failures, but its catch-all does not log the exception. `DatabaseConfigurator` catches exceptions and includes `exception.Message` in its returned setup message. These are different diagnostic patterns, not one centralized exception pipeline.
- OTP logging currently includes the OTP and destination (see SECURITY). Admin JavaScript reports most user-facing failures through `toast`; no central browser error reporter, request correlation ID, distributed trace configuration, or API-wide structured request log was found in the inspected source.
- Do not infer that a generic `unexpected_error` response has corresponding server diagnostics; follow the actual feature’s logging path when investigating failures.

## CHANGE MANAGEMENT

- The migration chain is under `src/YemenDrive.Database/Migrations` with `YemenDriveDbContextModelSnapshot.cs`. API startup runs `MigrateAsync` whenever saved DB settings are present; database setup also migrates the selected database. The `RecreateSchema` setting calls `EnsureDeletedAsync` before migration.
- Root `stage3_*.sql`, `migration_schema_check.sql`, `DATABASE_SCHEMA_AUDIT.md`, `CODEX_PROGRESS.md` and `CODEX_WORK_LOG.md` are separate project artifacts. Their contents may describe a particular work stage; check them against current model, migration and service code. This task updates only this guide.
- Postman is an explicit manual API workflow collection with database setup, user/service/ride, wallet, payment, cancellation, cash approval, accounting and statement requests. It includes collection variables/scripts, but no Postman runner invocation is encoded as an automatic repository test.
- The financial test executable creates a uniquely named LocalDB database, runs migrations/scenarios and deletes that test database in `finally`. It requires LocalDB; it is not an in-memory fake.
- No CI workflow, admin-JavaScript test harness, or repository-wide migration review automation was found. Migrations or setup actions must not be described as verified unless a corresponding command/test was actually run.

## DEFINITION OF DONE

- No standalone repository-level Definition of Done or release gate was found.
- Observable verification assets are the solution build, the financial integration console runner, current migration/model snapshot, and manually executable Postman collection. The Postman collection is broader than the automated financial runner; neither covers the entire UI/API system.
- The financial runner is not part of `YemenDrive.sln` and depends on SQL Server LocalDB. A green solution build or `dotnet test YemenDrive.sln` therefore does not, by itself, demonstrate that its financial scenarios ran.
- No automated acceptance evidence for admin JavaScript rendering or full client workflows was found in this repository. Do not report those areas as tested unless a separate manual or external test was performed and recorded.
