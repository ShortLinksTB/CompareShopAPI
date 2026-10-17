# CompareShopAPI

Phase 0 .NET 10 skeleton. Domain has no framework dependencies; Application references Domain; Infrastructure references both; Workers references Application; Api composes Application, Infrastructure and Workers. No business workers, entities, migrations or admin-user provisioning run in this phase.

## Run and test

```sh
dotnet restore CompareShopAPI.slnx
dotnet run --project CompareShopAPI.Api
# Requires a dedicated reachable MSSQL test database (CI provides SQL Server 2022).
export COMPARESHOP_SQLSERVER_TEST_CONNECTION='<test database connection>'
dotnet test CompareShopAPI.slnx -c Release --settings coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResultsScoped
```

`GET /health` is anonymous liveness. `GET /health/ready` checks SQL connectivity and returns 503 if configuration is missing, the database is unreachable, or startup migration failed. Health responses do not expose connection details. `Database__ApplyMigrations` defaults to false; this phase contains no migrations. Enabling it never makes a failed migration terminate the host.

`GET /admin/health` requires a valid JWT and `resource_access["compareshop-admin-ui"].roles` containing `admin`. Missing issuer/audience configuration fails closed. Malformed roles and roles on other clients are denied. OpenAPI `/openapi/v1.json` and Scalar `/docs` are available only in Development.

## Deployment configuration

Inject `ConnectionStrings__CompareShopDb` with database `CompareShop` on the shared MSSQL service in `shortlink-infra-dev`. Inject `Keycloak__Authority` and `Keycloak__Audience` for the existing realm; create client `compareshop-admin-ui` there, without creating a realm. No credentials are stored here. Redis/RabbitMQ skeleton configuration has isolated `compareshop:` / `compareshop.` prefixes; phase 0 has no consumers. OTEL defaults to service `compareshop-api` and the shared Tempo endpoint; standard `OTEL_SERVICE_NAME` / `OTEL_EXPORTER_OTLP_ENDPOINT` override them.

CORS defaults to an empty allowlist. Dev must inject exactly:

- `Cors__AllowedOrigins__0=https://compareshop-dev.giamha.online`
- `Cors__AllowedOrigins__1=https://admin-compareshop-dev.giamha.online`

Devops owns Helm chart, Argo application, secrets, TLS and public ingress `https://api-compareshop-dev.giamha.online` in `compareshop-dev`. Use `/health` for liveness and `/health/ready` for readiness. Public DNS/HTTPS, no Cloudflare Access, CORS, admin authorization and Argo Synced/Healthy require runtime verification after review and deployment.

CI tests against MSSQL and enforces 80% changed-line coverage. PRs compare to `origin/dev`; dev pushes compare to the push's before-SHA. Successful dev builds publish immutable `ghcr.io/shortlinkstb/compareshop-api:<sha>` and dispatch `compareshop-api-image-published` to Helm using `GITOPS_TOKEN`. The bootstrap probe workflow is not the application image or rollout.

Reference: ShortLinkAPI dev `69ebf93d6e4fa8ea0d833db38cfd1f132e10cc08`, Design SL-871. Compared with the reference, readiness checks SQL, migration failure preserves liveness, role authorization has no provisioning side effect, and CORS defaults empty.

## Phase 1 catalog API

Catalog routes are anonymous and read-only. Apply the additive catalog migration only when `Database:ApplyMigrations=true`; startup leaves the flag off by default. Fixture data is never created during startup. For an explicit dev population, temporarily set `CatalogSeed__Enabled=true` and `CatalogSeed__Environment=dev` on the dev deployment, then call `POST /admin/dev/catalog-seed` with a valid admin JWT. The route is not registered unless both settings match, and it still requires the `compareshop-admin-ui:admin` role. The operation is deterministic and idempotent; it returns 204 on success. Turn `CatalogSeed__Enabled` off after running it. Production must not use the dev environment marker. Tests can continue to call `CatalogSeed.SeedAsync` directly against a disposable database.

- `GET /api/v1/categories` returns `{ "items": [{ "id": 1, "slug": "phones", "name": "Phones" }] }`.
- `GET /api/v1/products?page=1&pageSize=20&categoryId=1&minPrice=100000&maxPrice=500000&shopId=2` returns `{ "items": [...], "page": 1, "pageSize": 20, "totalItems": 1, "totalPages": 1 }`. All filters are optional; price values are integer VND; page defaults to 1 and pageSize to 20 (maximum 100). Sorting is product name then id. Invalid page/price ranges return 400.
- `GET /api/v1/products/{slug}` returns the product, category `{id, slug, name}`, specification groups, and `{minPrice,maxPrice,offerCount}` summary.
- `GET /api/v1/products/{id}/offers` returns `{ "items": [...] }`; known products with no visible offers return an empty list.
- `GET /api/v1/shops/{slug}` returns the active shop or 404.

Only active offers refreshed within the previous 30 days, not marked as outliers, and attached to active products and active shops contribute to offer results, price filters, and summaries. Products with no visible offers remain in the product list with null min/max and zero count.
