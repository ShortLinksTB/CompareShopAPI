SL-1116 investigation — 2026-10-29

Base: `f05129bb4245b7d9943658969bed76dbf8e699c8` (dev, PR #37).
Runtime image: `ghcr.io/shortlinkstb/compareshop-api-workers:f05129bb4245b7d9943658969bed76dbf8e699c8`.

## Verified observations

Read-only Worker logs in `compareshop-dev`, since 10:20 UTC:

- BHX: `Reason=ProductCountInvalid; ExceptionType=System.IO.InvalidDataException`.
- CellphoneS: `Reason=Unclassified; ExceptionType=System.TimeoutException`.

Both readers already had safe ExceptionType/stack diagnostics in PR #37. This PR adds source, persisted job ID and per-invocation stage; it does not claim the prior logging was absent.

A disposable local probe ran the actual readers using their existing robots checks, request guards and Worker forward proxy. Browser launch used `BhxBrowserLaunchOptions.Create(proxy.Server)`. No guard, domain policy or matching rule was relaxed. Probe job ID `0` is synthetic; it is not a dev SyncJob.

- BHX reproduces `ProductCountInvalid`, stage `ProductCount`. The rendered body text is empty, with zero `[data-product-code]` cards (also zero `[data-product-id]`, `[data-id]`, `.product-item`, `.product-info` and `h3`). An additional bounded three-second observation did not produce cards. DOM contains a hidden div, template and scripts. The browser request guard repeatedly aborts requests to `bhxcdn.vn`, outside the sole approved `www.bachhoaxanh.com` host. This is evidence of a rendering dependency denied by policy, not proof that adding that host would fix ingestion, nor evidence of a Cloudflare challenge. The existing product selectors remain unverified against a rendered catalog.
- CellphoneS reaches `ProductFields`. The first `.product-info` card has an anchor, `.product__name` and image, but zero `.product__price` or `.price` descendants. Its actual price classes are `.product__price--show` and `.product__price--through`. `InnerTextAsync` on the absent current-price locator therefore times out. The card and its `.product-info-container.product-item` parent have only a class attribute, with neither `data-product-id` nor `data-sku`; changing only the price selector would subsequently fail SKU validation. No external SKU was fabricated from a URL/name or matched automatically.

## Decision needed before ingestion repair can complete

Dev-Leader must confirm a policy-compliant BHX rendering/data strategy (including whether a narrowly audited source CDN dependency may be approved) and an authoritative CellphoneS external-ID extraction strategy. Then implement the source parsers against captured permitted markup with regression tests. Do not expand egress hosts merely to get a green import.

## Acceptance remains open

No successful dev import was produced by this PR. Do not close SL-1116 or SL-1070 on these tests or local probes. After review/merge and dev rollout, collect real job IDs, timestamps and deployed commit from `GET /admin/sync-jobs`, proving `importedCount > 0` for **both** sources, and verify real offer data through `GET /api/v1/products`. Preserve manual approval of pending mappings.

## Validation

Tests cover stage failures using injected Playwright interfaces, actual reader orchestration, persisted job forwarding, concurrent context isolation, exception identity/redaction, and guard/proxy regressions. Browser factory injection is internal only; production still uses `Playwright.CreateAsync` and the existing proxy launch options. SQL integration tests run exclusively in disposable Testcontainers, never against dev.
