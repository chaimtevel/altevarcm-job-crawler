# Job-based Web Crawler

Submit a crawl job for a URL; a worker crawls it asynchronously through a message broker, results are stored in
PostgreSQL, and a React UI shows live progress, a tree of discovered pages with each page's **Domain Link Ratio**,
and job history.

**Stack:** .NET 8 (ASP.NET Core minimal APIs) · Dapr pub/sub over **RabbitMQ** · PostgreSQL + EF Core ·
React 18 + TypeScript (Vite, React Router, TanStack Query, Ant Design) · Docker Compose

**Spec coverage:** [docs/specs/spec-checklist.md](docs/specs/spec-checklist.md) maps every requirement to how it was met.

---

## Run locally

Needs Docker only. Copy the demo settings, start everything, then open **http://localhost:5080**.

```bash
cp .env.example .env
docker compose up -d --build
```

Try `https://books.toscrape.com/` (hits the 200-page limit) or `https://quotes.toscrape.com/` (external links).

Other URLs: Swagger at http://localhost:5080/swagger, health at http://localhost:5080/health and
http://localhost:5081/health, and RabbitMQ at http://localhost:15672 (login from `.env`).

**Tests** (needs the .NET 8 SDK):

```bash
dotnet test
```

---

## Architecture

```
React UI ──HTTP──> Crawl API ──publish──> Dapr ──> RabbitMQ ──> Dapr ──deliver──> Crawl Worker
   (served by API)    │                                                       │
                      └──────────────── PostgreSQL (jobs, pages, links) ──────┘
```

| Project | Responsibility |
|---|---|
| `Crawler.Api` | Service A: validates and creates jobs, serves status/tree/history, serves the UI. Owns schema migrations. |
| `Crawler.Worker` | Service B: one message = one page. Fetch, parse links, compute ratio, save, publish children. Job timeout sweep. |
| `Crawler.Domain` | Pure logic, no dependencies: URL normalization, Domain Link Ratio, page limit, SSRF address rules, entities. |
| `Crawler.Infrastructure` | EF Core `DbContext`, migrations, idempotent persistence (`CrawlStore`), Dapr publisher. |
| `tests/Crawler.Tests` | Unit tests plus an HTML-fixture integration test (single test project). |
| `web/` | React UI. |

**Key choices**

- **One message per page** (not per job): natural parallelism across workers, per-page retries, and progress
  falls out of the `crawl_pages` table.
- **Dapr over RabbitMQ:** broker access, retries (resiliency policy) and dead-lettering are configuration
  (`dapr/components/`); the app code only publishes and handles HTTP. Each service has its own sidecar.
- **The database is the source of truth.** A page row exists *before* its message is published; the job
  completes when no page is `Pending`.
- **UI served by the API** in Docker: one origin, so no CORS; Vite proxies `/api` in development.

---

## Message schema

Topic `crawl-page` on pub/sub component `crawl-pubsub` (CloudEvents envelope added by Dapr):

```json
{
  "jobId": "4cf766cc-d242-4deb-9f14-ae32be5a8fc1",
  "pageId": 8627,
  "url": "https://books.toscrape.com/",
  "depth": 0,
  "correlationId": "eefbd49394a0ae502ec4617993e04cdd"
}
```

- Published with Dapr metadata `ttlInSeconds` = time left until the job's 15-minute deadline.
- `correlationId` is the W3C trace id of the originating API request; children inherit it.

---

## Data model and idempotency

| Table | Key constraints |
|---|---|
| `crawl_jobs` | PK `id`; index `created_at DESC` (history paging) |
| `crawl_pages` | **UNIQUE (`job_id`, `url`)** – a normalized URL exists once per job; index (`job_id`, `status`) for progress/completion; index `discovered_by_page_id` (tree, child publishing) |
| `page_links` | **PK (`parent_page_id`, `child_page_id`)** – an edge is stored once |

The worker assumes at-least-once delivery. Duplicates are harmless because:

1. **Unique keys + upserts:** child pages use `INSERT … ON CONFLICT (job_id, url) DO UPDATE … WHERE` (only to move
   an uncrawled page to a shallower depth); links use `ON CONFLICT DO NOTHING`.
2. **Status guard:** a message for a page that is no longer `Pending` does no crawling.
3. **Claim with lease (`claimed_at`):** concurrent duplicates of a `Pending` page — only one worker fetches it.
   Released on failure; expires after 2 minutes if a worker dies.
4. **Publish tracking (`enqueued_at`):** a redelivery republishes only children whose message was never sent
   (covers a crash between commit and publish) — no duplicate fan-out.
5. **Per-job row lock (`SELECT … FOR UPDATE`)** while saving: the page-limit reservation and the
   "no pages pending → Completed" check cannot race between workers.

---

## Retries, transient failures and the dead-letter queue

**HTTP fetches (per page)** – `Microsoft.Extensions.Http.Resilience`:
10 s per attempt, 30 s total (headers *and* body), 2 retries with exponential backoff + jitter.
Transient = network errors, timeouts, HTTP 5xx, 408, 429.
When retries are exhausted the **page** is recorded as `Failed` with a reason and the message is acknowledged —
a broken site is a result, not a message failure.

**Message processing** – Dapr resiliency policy (`dapr/components/resiliency.yaml`):
exponential backoff, max 5 retries (~1 min). Transient = anything that makes the handler fail: database or broker
unavailable, deadlocks, unexpected exceptions.

**Dead-letter queue** – `dlq-crawl-worker-crawl-page` (RabbitMQ DLX created by Dapr; `requeueInFailure=false`).
Routed there:
- messages that still fail after all Dapr retries (e.g. database down for longer than the retry window),
- poison messages that cannot be processed (e.g. malformed payload),
- messages whose TTL expired before delivery.

A job whose messages are dead-lettered or expired is failed by the **job timeout sweep** (every 30 s, jobs older
than 15 minutes → `Failed: Timed out after 15 minutes`), so no job stays `Running` forever.

---

## Crawling rules and decisions

- **Normalization:** resolve relative links (honouring `<base href>`), drop `#fragment`, lowercase scheme/host,
  drop default port and user-info; keep path case, query and trailing slash. Only `http`/`https`; `mailto:`,
  `tel:`, `javascript:` etc. are ignored. URLs over 2,048 chars are dropped (btree index limit).
- **Domain Link Ratio** = internal links / total distinct links on the page; 0 when there are none.
  "Internal" = the starting host **or any of its subdomains**, ignoring a leading `www.` (product decision; the spec
  says "host"). Scheme and port are ignored.
- **Depth:** default 2, max 5. Only internal links within the depth are crawled; external and deeper links are
  stored as `Skipped` with a reason. If a shorter path to an uncrawled page is found later, it takes the
  shallower depth.
- **Page limit:** 200 crawled pages per job (atomic reservation); the rest are `Skipped: Page limit reached`.
- **HTML only** (`text/html`, `application/xhtml+xml`, case-insensitive), max 5 MB.
- **Redirects** are followed by `PageFetcher` itself (auto-redirect off), max 5 hops in total, including
  `https → http`, which `HttpClient` would refuse but some sites use (e.g. quotes.toscrape.com). The page is crawled
  as its final URL; a redirect isn't a depth level. Loops end as `Failed: Too many redirects`. The final URL is
  stored as `Skipped: Same page as …`, so links to it (e.g. `example.com` → `www.example.com/`) aren't crawled twice.
- **Input URL:** a missing scheme defaults to `https://` (`example.com` → `https://example.com/`).
- **SSRF guard:** loopback, private, link-local (incl. cloud metadata) and reserved addresses are rejected at the
  API and blocked at connect time in the worker, which also covers DNS names and redirects.
- **Job deadline:** 15 minutes (also the message TTL).

## API

| Method | Route | |
|---|---|---|
| POST | `/api/jobs` | `{ url, maxDepth? }` → `201 { jobId }`; 400 with field errors |
| GET | `/api/jobs/{jobId}` | status, timestamps, failure reason, progress counts |
| GET | `/api/jobs/{jobId}/tree` | nested tree of crawled/reserved pages (by first discoverer) |
| GET | `/api/jobs/{jobId}/pages/{pageId}/links` | a page's outgoing links (incl. skipped/external) |
| GET | `/api/jobs?page=1&pageSize=20` | history, most recent first |

## Observability

- Structured JSON console logs (built-in .NET JSON formatter) with `JobId`, `PageId`, `CorrelationId` scopes:
  `docker compose logs -f api worker`.
- `/health` on API and worker checks PostgreSQL and the Dapr sidecar.

---

## What I built first, what I cut, what's next

**First:** the parts that carry most of the grade and are hardest to retrofit — URL normalization and the ratio
(with unit tests), the schema with unique keys, then the message flow with idempotency, retries and DLQ.
The UI came after the backend was correct end to end.

**Cut (and why):**
- **Authentication / user accounts** – not in the spec's flows or rubric; would have consumed the time budget.
- **Cancel job** (optional) – the `Canceled` status exists; the endpoint and worker check were lower value.
- **CI pipeline** (optional), SSE/SignalR (polling is enough), robots.txt and rate limiting.

**Next, with more time:**
1. Transactional outbox for the API's first publish (today a broker outage marks the job `Failed` with HTTP 503).
2. Cancel endpoint; per-host politeness (robots.txt, rate limit, concurrency cap).
3. GitHub Actions build + test; Testcontainers-based tests for `CrawlStore` SQL and the full message flow.
4. Auth if the system becomes multi-user.

## Known limitations

- No authentication; Swagger is enabled in every environment (demo).
- Credentials in `.env` are for local/demo use only; a real deployment would use a secret manager.
- No robots.txt or per-host rate limiting; up to ~10 pages fetched in parallel (RabbitMQ prefetch).
- A page already crawled via a deeper path is not re-crawled if a shorter path appears later.
- Blocked (private-address) hosts are retried twice by the HTTP policy before failing.
- The tree shows crawled/reserved pages only; skipped links are listed per page via its "N links" button.
- Following `https → http` redirects fetches those pages unencrypted; acceptable for a read-only crawler of public pages.
- The API has no app-token check because nothing calls into it from Dapr; the worker's message endpoint does.
