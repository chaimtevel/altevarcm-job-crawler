# Job-based Web Crawler

[![CI](https://github.com/chaimtevel/altevarcm-job-crawler/actions/workflows/ci.yml/badge.svg)](https://github.com/chaimtevel/altevarcm-job-crawler/actions/workflows/ci.yml)

Submit a URL; a worker crawls the site through RabbitMQ, stores pages and links in PostgreSQL, and a React UI shows
progress, a tree of pages with each page's **Domain Link Ratio**, and job history.

**Stack:** .NET 8 · Dapr pub/sub over RabbitMQ · PostgreSQL + EF Core · React 18 + TypeScript + Ant Design · Docker Compose

**Spec coverage:** [docs/specs/spec-checklist.md](docs/specs/spec-checklist.md)

## Run locally

Needs Docker. Then open **http://localhost:5080**.

```bash
cp .env.example .env
docker compose up -d --build
```

Try `https://books.toscrape.com/` (hits the 200-page limit). Swagger: http://localhost:5080/swagger.

Tests (needs the .NET 8 SDK):

```bash
dotnet test
```

## Architecture

```
React UI ──> Crawl API ──> Dapr ──> RabbitMQ ──> Dapr ──> Crawl Worker
               └──────────── PostgreSQL ─────────────────────┘
```

| Project | Role |
|---|---|
| `Crawler.Api` | Creates jobs, serves status, tree and history, serves the UI, runs migrations. |
| `Crawler.Worker` | One message = one page: fetch, extract links, compute ratio, save, queue children. |
| `Crawler.Domain` | Pure logic: URL normalization, ratio, page limit, SSRF rules. |
| `Crawler.Infrastructure` | EF Core, idempotent SQL (`CrawlStore`), Dapr publisher. |

**Key choices**
- **One message per page:** parallel across workers, retried per page, progress comes straight from the database.
- **Dapr:** retries and dead-lettering are config (`dapr/components/`), not code.
- **Database first:** a page row exists before its message is sent; a job completes when no page is `Pending`.
- **Live updates (SSE):** the worker sends Postgres `NOTIFY` on each save; the API `LISTEN`s and streams
  `GET /api/jobs/{id}/events` to the browser. Polling is the fallback if the stream drops.

## Message schema

Topic `crawl-page` on `crawl-pubsub`, in a CloudEvents envelope; TTL = time left until the job's 15-minute deadline.

```json
{ "jobId": "4cf766cc-…", "pageId": 8627, "url": "https://books.toscrape.com/", "depth": 0, "correlationId": "eefbd493…" }
```

## Data model and idempotency

Tables: `crawl_jobs`, `crawl_pages` (**unique `job_id, url`**), `page_links` (**PK `parent, child`**).
Delivery is at-least-once; duplicates are harmless because of:

- **Upserts** on those unique keys, so a page or edge is stored once.
- **Claim lease** (`claimed_at`, 2 min): only one worker fetches a page.
- **Publish tracking** (`enqueued_at`): a redelivery re-sends only children never published.
- **Job row lock** while saving: the page limit and completion check can't race.

## Retries, transient failures and the dead-letter queue

- **HTTP:** 10 s per attempt, 30 s total, 2 retries with backoff. Transient = network errors, timeouts, 5xx, 408, 429.
  A page that still fails is saved as `Failed`; that's a result, not a message failure.
- **Messages:** Dapr retries a failing handler 5 times with backoff (database down, deadlock, bug).
- **DLQ** `dlq-crawl-worker-crawl-page` receives messages that exhaust retries, can't be parsed, or expire.
  A sweep fails jobs older than 15 minutes, so no job stays `Running` forever.

## Crawling rules

- **Links:** relative links and `<base href>` resolved, `#fragments` dropped, only http(s), max 2,048 chars.
- **Ratio:** internal ÷ total distinct links, 0 if none. Internal = starting host or its subdomains, ignoring `www.`.
- **Limits:** depth 2 by default (max 5), 200 pages per job, HTML only, 5 MB per page.
- **Sitemaps:** the start page also reads `robots.txt` / `sitemap.xml`, so pages added by JavaScript (e.g. job lists) are found;
  they use at most half the page limit, leaving room for following links deeper.
- **Redirects:** up to 5 hops, including `https → http`. Links that redirect off-site are skipped; a redirect target
  is recorded so it isn't crawled twice.
- **SSRF guard:** private, loopback and cloud-metadata addresses are blocked, including after DNS lookups and redirects.
- **Cancel:** `POST /api/jobs/{id}/cancel`, or the Cancel button.

## What I built first, what I cut, what's next

- **First:** URL normalization and the ratio (with tests), then the schema and the idempotent message flow, because
  they carry most of the grade and are hardest to change later. UI last.
- **Cut:** authentication (not in the spec's flows), politeness rules (robots.txt `Disallow`, rate limits).
- **Next:** transactional outbox for the first publish, robots.txt `Disallow` and per-site rate limits,
  Testcontainers tests for the SQL.

## Known limitations

- No authentication; Swagger is always on; `.env` credentials are for local use only.
- The API runs database migrations at startup for convenience; production would run them as a deploy step.
- JavaScript isn't run: links added after page load are found only if the sitemap lists them.
- No rate limiting (about 10 pages in parallel), so bot protection may answer with 403/429 (shown as Failed).
- A page already crawled isn't re-crawled when a shorter path to it turns up later.
