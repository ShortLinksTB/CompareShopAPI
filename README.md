# CompareShopAPI

CompareShopAPI is a .NET 10 Clean Architecture service. Domain has no framework dependencies; Application references Domain; Infrastructure implements application ports; the API composes Application and Infrastructure. The separate Worker executable references Application and Infrastructure and owns browser automation dependencies.

## Run and test

```sh
dotnet restore CompareShopAPI.slnx
dotnet run --project CompareShopAPI.Api
# SQL integration tests require Docker; Testcontainers starts and removes an isolated SQL Server container.
dotnet test CompareShopAPI.slnx -c Release --settings coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResultsScoped
```

`GET /health` is anonymous liveness. `GET /health/ready` checks SQL connectivity and returns 503 if configuration is missing, the database is unreachable, or startup migration failed. Health responses do not expose connection details. `Database__ApplyMigrations` defaults to false. Enabling it never makes a failed migration terminate the host.

`GET /admin/health` requires a valid JWT and `resource_access["compareshop-admin-ui"].roles` containing `admin`. Missing issuer/audience configuration fails closed. Malformed roles and roles on other clients are denied. OpenAPI `/openapi/v1.json` and Scalar `/docs` are available only in Development.

For first-admin setup, configure `Admin__BootstrapSuperAdminSubjects__0` to the exact Keycloak JWT `sub` of the operator who will initialize this deployment. That operator must also have the `compareshop-admin-ui` client `admin` role. After applying migrations, they call `POST /admin/bootstrap/first-admin` with their normal bearer token; the endpoint grants the seeded Super Admin role once and records an audit entry. A repeat call by that same subject is idempotent; once another subject owns Super Admin, provisioning is closed. Leave the list empty after setup (the default) to deny further bootstrap attempts. No role or subject is accepted in the request body.

## Deployment configuration

Inject `ConnectionStrings__CompareShopDb` with database `CompareShop` on the shared MSSQL service in `shortlink-infra-dev`. Inject `Keycloak__Authority` and `Keycloak__Audience` for the existing realm; create client `compareshop-admin-ui` there, without creating a realm. No credentials are stored here. Redis/RabbitMQ skeleton configuration has isolated `compareshop:` / `compareshop.` prefixes; phase 0 has no consumers. OTEL defaults to service `compareshop-api` and the shared Tempo endpoint; standard `OTEL_SERVICE_NAME` / `OTEL_EXPORTER_OTLP_ENDPOINT` override them.

CORS defaults to an empty allowlist. Dev must inject exactly:

- `Cors__AllowedOrigins__0=https://compareshop-dev.giamha.online`
- `Cors__AllowedOrigins__1=https://admin-compareshop-dev.giamha.online`

Devops owns Helm chart, Argo application, secrets, TLS and public ingress `https://api-compareshop-dev.giamha.online` in `compareshop-dev`. Use `/health` for liveness and `/health/ready` for readiness. Public DNS/HTTPS, no Cloudflare Access, CORS, admin authorization and Argo Synced/Healthy require runtime verification after review and deployment.

Affiliate redirect rate limiting resolves forwarded client addresses only when the immediate socket peer is explicitly trusted. Production startup requires `RateLimiting__AffiliateRedirect__TrustedProxies__0`, `__1`, etc. to contain the exact trusted ingress proxy IPs, ordered as a set (the resolver walks `X-Forwarded-For` right-to-left). Untrusted peers' forwarded headers are ignored. Configure these values from the actual ingress-to-pod network path; do not use broad or guessed ranges. Local development may leave the list empty and will use the socket peer address.

CI tests against MSSQL and enforces 80% changed-line coverage. PRs compare to `origin/dev`; dev pushes compare to the push's before-SHA. Successful dev builds publish immutable `ghcr.io/shortlinkstb/compareshop-api:<sha>` and dispatch `compareshop-api-image-published` to Helm using `GITOPS_TOKEN`. The bootstrap probe workflow is not the application image or rollout.

Reference: ShortLinkAPI dev `69ebf93d6e4fa8ea0d833db38cfd1f132e10cc08`, Design SL-871. Compared with the reference, readiness checks SQL, migration failure preserves liveness, role authorization has no provisioning side effect, and CORS defaults empty.

## Phase 1 catalog API

Catalog routes are anonymous and read-only. Apply the additive catalog migration only when `Database:ApplyMigrations=true`; startup leaves the flag off by default. The deterministic development fixture is applied only by an explicit one-off command. Build/deploy the image, then run `dotnet CompareShopAPI.Api.dll seed-catalog` in a one-off process with `ASPNETCORE_ENVIRONMENT=Development`, the normal `ConnectionStrings__CompareShopDb`, and `Database__AllowCatalogSeed=true`. The command refuses to run outside the Development environment, unless the opt-in is enabled, or unless the connection targets a database named `CompareShop`; it checks connectivity and does not run migrations or start the HTTP server. Leave `Database__AllowCatalogSeed=false` for normal API workloads and all non-dev environments. Re-running the command is idempotent.

- `GET /api/v1/categories` returns `{ "items": [{ "id": 1, "slug": "phones", "name": "Phones" }] }`.
- `GET /api/v1/products?page=1&pageSize=20&categoryId=1&minPrice=100000&maxPrice=500000&shopId=2` returns `{ "items": [...], "page": 1, "pageSize": 20, "totalItems": 1, "totalPages": 1 }`. All filters are optional; price values are integer VND; page defaults to 1 and pageSize to 20 (maximum 100). Sorting is product name then id. Invalid page/price ranges return 400.
- `GET /api/v1/products?search=phone` also filters the active catalog by trimmed product name (`2..80` characters). Blank/omitted search preserves the existing list behavior. Search composes with category and visible-offer filters; SQL collation controls case behavior.
- `GET /api/v1/search/suggestions?q=pho&limit=8` returns `{ "items": [{ "id": 1, "slug": "sample-phone", "name": "Sample Phone" }] }`. Query is required and trimmed (`2..80` characters); limit defaults to 8 and accepts `1..10`. Prefix matches come first, followed by name and id. Suggestions do not require a visible offer; no matches return an empty `items` array.
- `GET /api/v1/products/1/price-history?from=2026-07-01T00:00:00Z&to=2026-10-01T00:00:00Z` returns `{ "items": [{ "date": "2026-08-01T00:00:00Z", "minPrice": 100000, "shopId": 2 }] }` in ascending date/id order. Dates must be UTC ISO 8601. With both dates omitted, the previous 90 days are used; if only `to` is supplied, `from` defaults to 90 days before it; if only `from` is supplied, `to` defaults to now. A range up to 365 days is accepted. Unknown/inactive products return 404, while active products without recorded snapshots return 200 with an empty list.
- `GET /api/v1/products/{slug}` returns the product, category `{id, slug, name}`, specification groups, and `{minPrice,maxPrice,offerCount}` summary.
- `GET /api/v1/products/{id}/offers` returns `{ "items": [...] }`; known products with no visible offers return an empty list.
- `GET /api/v1/shops/{slug}` returns the active shop or 404.

Only active offers refreshed within the previous 30 days, not marked as outliers, and attached to active products and active shops contribute to offer results, price filters, and summaries. Products with no visible offers remain in the product list with null min/max and zero count.

Price history is append-only. `PriceHistoryService.RecordMinimum` records the current visible minimum only when that price differs from the latest point. The additive migration verifies the existing `dbo.Products.Id` and `dbo.PartnerShops.Id` int identity primary keys before creating `dbo.PriceHistoryPoints`, and fails clearly on absent/incompatible prerequisites or partial incompatible history objects.

## BHX ingestion

The `compareshop-api-workers` executable is deployed separately from the API. Its image alone includes Microsoft.Playwright and its matching Chromium installation. The worker processes the single BHX `/bia` source at a 72-hour cadence, records durable sync jobs, imports only manually mapped SKUs, and uses the existing public offer visibility and price-history rules. `GET /admin/sync-jobs` requires `sync_jobs.read`; retrying a failed or partial job through `POST /admin/sync-jobs/{id}/retry` requires `sync_jobs.execute`.

The worker Deployment template and constrained Cilium egress policy are in `deploy/compareshop-api-workers.yaml`. Apply the ingestion migration with the existing `Database__ApplyMigrations=true` rollout mechanism. The worker uses the `ConnectionStrings__CompareShopDb` secret and does not need a Kubernetes API token. SQL-backed tests use the disposable Testcontainers.MsSql fixture.
