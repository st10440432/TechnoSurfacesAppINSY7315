# Screen map

The twenty screens from the Task 1 journey maps, who can use each one, and where each screen gets its data. This is the checklist for connecting the front end to the back end.

"Ready" means the screen reads and writes through its data source on `develop`. Updated on 5 October 2026, when every signed-in screen was checked against a running copy of the app: each one reads and saves through the database, and the prototype's in-memory data has been removed. Forgot password and activate account were not part of that check, so their rows are unchanged.

## Who can use each screen

Six screens are for the Managing Director only. They do not appear in an estimator's side menu, and an estimator who opens one by its address sees a read only page that says who it is for, not an error page. The approval queue, price editor, rate card and quotation terms show their content read only with that note. Users and the audit trail are closed by policy, so they show a page saying they are for the Managing Director.

| Group | Screen | Managing Director | Estimator |
|---|---|---|---|
| Getting in | Sign in | Yes | Yes |
| | Forgot password | Yes | Yes |
| | Activate account | Yes | Yes |
| | Dashboard | Shows the approval queue | Shows their own quotes |
| Quoting | Quote list | Yes | Yes |
| | New quote | Yes | Yes |
| | Costing sheet | Edits any quote | Edits their own drafts only |
| | Customer quotation | Yes | Yes |
| | Approval queue | Yes | Managing Director only |
| | Review quote | Approves | Read only |
| | Version history | Yes | Yes |
| | Invoice record | Yes | Yes |
| Data | Material catalogue | Yes | Yes, including all prices |
| | Price editor | Edits | Managing Director only |
| | Customers | Yes | Yes |
| | Customer detail | Yes | Yes |
| Running the app | Rate card | Edits | Managing Director only |
| | Users | Yes | Managing Director only |
| | Quotation terms | Edits | Managing Director only |
| | Audit trail | Yes | Managing Director only |

Estimators can see every price, including cost prices. That is a decision the client confirmed, not an oversight.

## Where each screen gets its data

| Screen | What it needs | Comes from | Owner | Ready |
|---|---|---|---|---|
| Sign in | Credentials, lockout, deactivated accounts refused | ASP.NET Core Identity and `ISignInService` | Amaan | Yes |
| Forgot password | No email service (client decision), so the page explains that the Managing Director issues a new temporary password, which also lifts a lock | User administration, issuing a new temporary password | Amaan | Yes |
| Activate account | Sign in with the temporary password the Managing Director issued, then choose your own on the set your password screen | User administration and the forced password change | Amaan | Yes |
| Dashboard | Approval queue for the Managing Director, own quotes for an estimator | Quote workflow | Morgan | Yes |
| Quote list | Quotes filtered by status | Quote workflow | Morgan | Yes |
| New quote | Customer, contact, site, project, markup | Quote workflow | Morgan | Yes |
| Costing sheet | Supplier, product line, colour and sheet size lists; add, change and remove lines; totals | `/api/catalogue/*` and `/api/quotes/{id}/lines` endpoints | Morgan | Yes |
| Customer quotation | Quotation lines with no cost fields, standing terms, warranty by brand | Quotation generation | Morgan | Yes |
| Approval queue | Quotes waiting for approval | Approval workflow | Morgan | Yes |
| Review quote | Correct and approve in one step, the changes made to the quote | Approval workflow and the audit trail | Morgan, Amaan | Yes |
| Version history | Every version with its date, author and total | Quote versioning | Morgan | Yes |
| Invoice record | Pastel invoice number, date and amount | Invoice record | Morgan | Yes |
| Material catalogue | Suppliers, product lines, colours, sheet sizes and prices | `CatalogueService`, reading the seeded catalogue | Amaan | Yes |
| Price editor | Change a price from a date without overwriting the old one | `CatalogueService`, which saves through Kallan's `IPriceHistory.SetMaterialPriceAsync` | Amaan, with Kallan's `IPriceHistory` underneath | Yes |
| Customers | Customer list | Customers and contacts | Morgan | Yes |
| Customer detail | A customer with its contacts | Customers and contacts | Morgan | Yes |
| Rate card | Rates, including the nine still awaiting the client's figures | `CatalogueService`, which saves through Kallan's `IPriceHistory.SetRateAsync` | Amaan, with Kallan's `IPriceHistory` underneath | Yes |
| Users | List, create, deactivate, reactivate, reset password | User administration | Amaan | Yes |
| Quotation terms | Standing terms held in one place | Quotation generation | Morgan | Yes |
| Audit trail | Changes filtered by user, entity and date | Audit interceptor and user administration | Amaan | Yes |

Kallan's price resolution and calculation engine are merged and sit underneath the costing sheet. The screen reaches them through Morgan's endpoints.

## Stories the screens deliver

All 28 user stories from section 2.3 of the Task 1 document, with the acceptance criteria shortened, and the screen where each one is seen. The eight marked **Front end** are assigned to the front end in the Task 2 build plan.

| Story | What the acceptance criteria require | Screen | Front end |
|---|---|---|---|
| US-01 | Choosing a supplier limits the materials, choosing a material limits the colours, and choosing a colour fills in the price without typing | Costing sheet | **Front end** |
| US-02 | The length and width of the chosen sheet are shown next to the price | Costing sheet | **Front end** |
| US-03 | A price that cannot be found shows a visible error and stops submission. No line is ever zero | Costing sheet | **Front end** |
| US-04 | Entering a quantity on a rate line works out the line total straight away | Costing sheet | |
| US-05 | Consumables come from the total area and transport from the sheet count, and both are shown as calculated | Costing sheet | **Front end** |
| US-06 | Any rate or quantity can be changed on one quotation without changing the rate card | Costing sheet | |
| US-07 | The markup is set per quotation and applies to the sub total only | Costing sheet | |
| US-08 | A supplier discount on a material line lowers that line's cost before markup | Costing sheet | |
| US-09 | The total square metres for the quotation is shown on the summary | Costing sheet | **Front end** |
| US-10 | The customer quotation is made from the costing and its total matches the costing total | Customer quotation | |
| US-11 | No cost price, supplier discount or markup appears anywhere on the customer document | Customer quotation | **Front end** |
| US-12 | Standing terms, lead times, exclusions and warranties appear automatically and are kept in one place | Quotation terms, Customer quotation | |
| US-13 | The warranty wording follows the brand quoted | Customer quotation | |
| US-14 | Site and project are captured per quotation and printed on the document | New quote, Customer quotation | |
| US-15 | Customers and contacts can be chosen. A quotation goes to a contact and is billed to a customer | New quote, Customers, Customer detail | |
| US-16 | Submitting moves the quotation to pending and puts it in the approval queue | Costing sheet | |
| US-17 | Pending quotations are listed with customer, project and value | Approval queue | |
| US-18 | The Managing Director can edit any field on a pending quotation and approve it in the same action | Review quote | |
| US-19 | The change, who made it and when are recorded and visible on the quotation | Review quote, Audit trail | |
| US-20 | A sent quotation can be reopened, edited and resubmitted without becoming a separate quotation | Version history | |
| US-21 | Every revision is a new version, and earlier versions stay readable and cannot be changed | Version history | |
| US-22 | Prices are stored on the line when it is created, so a later catalogue change does not alter the quotation | Costing sheet, Version history | |
| US-23 | Only the Managing Director can edit prices and rates, and every change is recorded | Price editor, Rate card | |
| US-24 | A retired material cannot be chosen for a new quotation but still reads correctly on old ones | Material catalogue, Price editor | |
| US-25 | The invoice number, date and amount can be recorded against an accepted quotation | Invoice record | |
| US-26 | Accounts are created by the Managing Director, there is no self registration, and a deactivated account cannot sign in | Users, Sign in | |
| US-27 | No page except sign in can be reached without signing in | Every screen | **Front end** |
| US-28 | The system works over the internet in a laptop browser and stays usable at that screen size | Every screen | **Front end** |

US-28 only asks for a laptop. The Task 2 marking rubric asks for phones and tablets as well, so the front end is built and tested for all three.
