# Quoting System for Techno Surfaces
A prototype for INSY7315: Information Systems 3E was created for Techno Surfaces (Pty) Ltd, a solid surface fabricator based in Cape Town with clients ranging from individual houses to 
national businesses such as Bootleggers, Vida e Caffè, and multiple airports in South Africa.

Techno Surfaces generates all of its quotes from a single Excel sheet and printed supplier pricing lists that are manually searched across twelve different worksheets. 
A price increase must be applied twelve times, and a mistyped search gives a wrong value rather than an error, which results in actual pricing errors. 
The purpose of this method is to make such errors obvious rather than invisible. 
The price of a material only shows up once its supplier, product line, colour, size, and thickness have all been selected.

This is the Task 1 prototype, which consists just of the front end and is constructed using ASP.NET Core MVC with Bootstrap. 
It needs a database and the back end logic. 
As planned for this part of the assignment, all data is seeded in memory when the application launches and nothing is stored in between searches.

Clone the repository, launch dotnet restore and then dotnet run from the project folder, and then open the local URL that appears. 
Three demo accounts, Paul Schluter as Managing Director and two estimators, Lerato Mokoena and Devan Naidoo, are created on developer machines only. Their password is set in user secrets (`Seed:DevelopmentPassword`), never in source code, and every account signs in through the normal sign-in page.

Signing in, a specific role dashboard, browsing and filtering quotes, creating a new quote using the colour to supplier material flow, the internal costing sheet, the client side quotation, the approval queue, 
version history for counteroffers and logging the resulting Pastel invoice reference are all covered by the app. 
The material catalogue, customer data, and admin screens for rates, users, quotation terms, and the audit trail are all included.

The client's actual supplier pricing lists are the source of the material costs, supplier codes, and sheet sizes. 
Since the client stated that the prices in their example workbook are not actual amounts and the actual rate card has not yet been verified, the labour and fabrication rates on the rate card are placeholders.

## Security

This section records the security controls committed to in Task 1 8, where each is implemented, and what is not done yet. Paths are relative to the repository root.

### Authentication (NFR-03, US-27)

- **ASP.NET Core Identity** with credentials managed by the application, stored in their own `auth` schema (`TechnoSurfacesApp/Identity/AuthDbContext.cs`), separate from the business tables.
- **No self-registration.** No registration route exists. Accounts are created only by the Managing Director (`TechnoSurfacesApp/Services/UserAdminService.cs`).
- **Password policy:** at least 12 characters with an upper-case letter, a lower-case letter and a digit, and no symbol rule. This favours length over composition, following NIST SP 800-63B (Task 1 8.2).
- **Lockout:** 5 failed attempts lock the account for 15 minutes (`SignInService` signs in with `lockoutOnFailure: true`).
- **Deactivated accounts are refused,** and that check runs only after the password is verified, so a wrong guess cannot reveal whether an account exists or is deactivated.
- **Session cookie:** HttpOnly, Secure, SameSite=Strict, 1-hour sliding expiry. The security stamp is re-checked every minute, so a deactivated user's open session ends within a minute rather than when the cookie expires.
- **Temporary passwords** (new accounts, and resets by the MD) are generated with `RandomNumberGenerator`. The holder must choose their own password before using anything else (`Identity/MustChangePasswordFilter.cs`, `Identity/AppClaimsPrincipalFactory.cs`).
- **No open redirects:** after sign-in, a return URL is followed only if it is local (`Url.IsLocalUrl`).
- **Seeded accounts:** the demo accounts exist on developer machines only, with their password in user secrets, never in source. In Azure they need `Seed:DemoAccounts` and are refused in Production in code. The first production Managing Director is created from Key Vault secrets (`Seed--InitialAdminEmail`, `Seed--InitialAdminName`, `Seed--InitialAdminPassword`).

### Authorisation (Task 1 §2.2)

- **Fail-closed:** a global fallback policy requires a signed-in user on every endpoint. Only the sign-in pages, static assets and the health check are `[AllowAnonymous]`, so a forgotten attribute fails closed rather than open.
- **Named policies,** defined once in `Program.cs` (`TechnoSurfacesApp/Identity/Policies.cs`):

| Policy | Who | Enforced on |
|---|---|---|
| `CanEditCatalogue` | Managing Director | Prices, rates, retiring items, quotation terms, bank details and brand warranties (`CatalogueController`) |
| `CanManageUsers` | Managing Director | Create, deactivate, reactivate and reset accounts (`AdminController`) |
| `CanViewAuditTrail` | Managing Director | `/Admin/Audit` |
| `CanApproveQuote` | Managing Director | Approval actions |
| `CanEditQuote` | MD: any quote. Estimator: only their own quote, and only while it is a Draft | Resource-based handler (`Identity/EditQuoteHandler.cs`) |
| `CanReopenQuote` | MD: any quote. Estimator: only a quote they created | Reopening after a counter-offer or lapse (`Identity/ReopenQuoteHandler.cs`) |
| `CanRecordInvoice` | Managing Director | Recording the Pastel invoice reference (US-25) |

- Estimators may view all pricing, a confirmed client decision, so viewing needs no policy.
- A refused browser request shows an access-denied page. A refused `/api` call gets a 401 or 403 status instead of a redirect.

### Audit trail (NFR-04, US-19)

- An EF Core `SaveChangesInterceptor` (`src/TechnoSurfaces.Infrastructure/Data/Auditing/AuditInterceptor.cs`) records every change to `MaterialPrice`, `RatePrice`, `Quote`, `QuoteVersion`, `CostingLine`, `Colour`, `ProductLine`, `AppUser`, `QuotationTerm` and `Brand`: entity, key, property, old and new value, user and UTC time. Because it is an interceptor, no service can skip it by forgetting to call it.
- The audit rows are written **in the same transaction** as the change. If they cannot be written, the change is rolled back.
- **Insert-only:** the database trigger `TR_AuditEntries_InsertOnly` refuses any UPDATE or DELETE on `AuditEntries` (migration `MakeAuditEntriesInsertOnly`), and no code or screen edits or deletes an entry.
- **Audit trail screen** (`/Admin/Audit`, MD only): filter by user, entity type, date range (South African time) and price changes only.
- **"Changes to this quote"** on the costing sheet and the review screen (US-19). The MD corrects an estimator's quote and approves it in one step, so this panel is how the estimator sees what was changed, by whom and when.

### CSRF, XSS and input validation (Task 1 8.4, 8.7, 8.8)

- **CSRF:** `AutoValidateAntiforgeryToken` is applied globally, so every POST, PUT and DELETE needs a token. `/api` calls send it in the `RequestVerificationToken` header. Sign-out is a POST.
- **XSS:** Razor encodes all output. There is one `Html.Raw`, in `Views/Quotes/Create.cshtml` (line 245), and it has been reviewed: it writes JSON produced by `System.Text.Json`, whose default encoder escapes `<`, `>`, `&`, `'` and `"`, so data cannot close the `<script>` block. It must never be switched to `UnsafeRelaxedJsonEscaping`.
- **Input validation:** data annotations on view models and API requests, checked again on the server by the services.

### POPIA (Task 1 8.5, NFR-09)

- **No personal data in logs:** sign-in and account events are logged without names or email addresses.
- **No personal data in URLs:** routes carry ids only, and the audit filter puts a user id, not a name, in the address.
- Customer records sit behind sign-in. Both roles maintain customers, by client decision.
- **Bank details** are entered by the Managing Director on the quotation terms screen and stored in the database, never in source code (the repository is public). Every change is audited.

### Transport and secrets (Matteo Nusca)

- HTTPS redirection and HSTS in `Program.cs`. The App Service is HTTPS-only with TLS 1.2 minimum, and so is Azure SQL (`infra/main.bicep`).
- The connection string lives in Azure Key Vault and reaches the app through a Key Vault reference, never `appsettings.json`.
- Deployment signs in to Azure with OIDC, so no Azure password is stored in GitHub.
- **Rate limiting:** at most 10 sign-in attempts per minute from one client address, then HTTP 429 (`Platform/SignInRateLimiting.cs`). Together with the account lockout this limits both guessing one account and spraying many.
- **Security headers** on every response (`Platform/SecurityHeaders.cs`): Content-Security-Policy, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-Frame-Options: DENY` and a `Permissions-Policy`. The CSP allows inline scripts (`'unsafe-inline'`) because several views still use inline handlers; moving those into script files and removing it is listed below.

### Security tests

 | Required test (Task 2 plan) | Status |
  |---|---|
 | Estimator edits another estimator's draft: refused | Unit test `EditQuoteHandlerTests`; integration test `CostingApiTests` (403) |
 | Estimator edits their own draft: allowed | Unit test, `EditQuoteHandlerTests` |
 | MD edits any quote: allowed | Unit test, `EditQuoteHandlerTests` |
 | Price change writes an `AuditEntry` with user and time | Unit test, `AuditInterceptorTests` |
 | Audit trail filters and a quote's change history | Unit test, `AuditTrailServiceTests` |
 | Anonymous request redirects to sign-in | Integration test, `PlatformTests` |
 | Estimator posts to the price editor: refused | Integration test, `SecurityTests` |
 | Estimator approves a quote: refused | Integration test, `QuoteWorkflowApiTests` (403) |
 | Deactivated user signs in: refused | Integration test, `SecurityTests` |
 | Post without an antiforgery token: rejected | Enforced globally; integration test `CostingApiTests` covers the API header |
 | Registration route does not exist | Integration test, `PlatformTests` |
 | MD changes to terms and bank details are audited | Unit test, `QuotationTermsMaintenanceTests` |
 | Estimator opens MD-only screens: refused | Integration test, `SecurityTests` |
 | Five wrong passwords lock the account | Integration test, `SecurityTests` |
 | MD price change audited through the real screen | Integration test, `SecurityTests` |

 Integration tests run in CI against a throwaway SQL Server database (`tests/TechnoSurfaces.IntegrationTests`).

### Not done, and why

| Item | Status | Reason |
|---|---|---|
| Forgot password with a single-use token | Placeholder page | There is no email service (client decision). The MD resets the password instead, and the user must change it at next sign-in |
| Activation link for new accounts | Replaced | A temporary password plus a forced change does the same job without email |
| Least-privilege database login | Deferred | The app connects as the SQL server administrator. A contained user with read/write rights only is the fix |
| Change history on prototype quotes | Empty until real quotes | The quote screens still read prototype data, so the panel shows no history until they read the database |
| Inline scripts allowed by the CSP | Partial | Views use inline `<script>` blocks and `onchange`/`onsubmit` handlers. Moving them to `.js` files would let the CSP drop `'unsafe-inline'` and block injected scripts|

Built by Brett James (ST10440287), Kallan Jones (ST10445389), Morgan Gibbon
(ST10439398), Amaan Tesfaye (ST10287107) and Matteo Nusca (ST10440432)
