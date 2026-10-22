# Spec checklist

How each requirement in `Technical Assessment.pdf` is met. Details are in the root `README.md`.

## Architecture

| Requirement | How it's met |
|---|---|
| Two separate processes | `Crawler.Api` (Service A) and `Crawler.Worker` (Service B) are separate projects, images and compose services. |
| Service A: create jobs, serve status/results/history | Minimal API endpoints in `JobEndpoints.cs` create jobs and serve status, tree, links and paginated history. |
| Service B: fetch, parse, metrics, progress | `CrawlPageHandler` fetches one page per message, extracts links, computes the ratio and updates job state. |
| RabbitMQ or Kafka | RabbitMQ, used through Dapr pub/sub (`dapr/components/pubsub.yaml`), with sidecars for the API and worker. |
| Local setup via Docker Compose | `docker-compose.yml` runs Postgres, RabbitMQ, API, worker and both Dapr sidecars with one command. |
| SQL DB persisting jobs, pages, edges | PostgreSQL via EF Core: tables `crawl_jobs`, `crawl_pages` and `page_links` (parent → child edges). |

## Job lifecycle and API

| Requirement | How it's met |
|---|---|
| jobId (GUID), input URL | `CrawlJob` has a `Guid Id` and the normalized input `Url`, validated as an absolute http(s) URL. |
| Status Pending/Running/Completed/Failed/Canceled | `CrawlJobStatus` enum has all five values, stored as strings; Canceled exists but no endpoint sets it. |
| Timestamps created/started/completed, failure reason | `CreatedAt`, `StartedAt`, `CompletedAt` and `FailureReason` columns, set by the API, worker and timeout sweep. |
| Create job `{ url, maxDepth? = 2 }` → `{ jobId }` | `POST /api/jobs` validates the URL and depth (0–5, default 2), returns `201 { jobId }`. |
| Read job status and summary | `GET /api/jobs/{id}` returns status, timestamps, failure reason and crawled/failed/skipped/pending page counts. |
| Read hierarchical tree | `GET /api/jobs/{id}/tree` returns nested nodes built from each page's discovering parent. |
| History, paginated, most recent first | `GET /api/jobs?page=&pageSize=` orders by `created_at DESC`, backed by an index on that column. |
| Cancel a job (optional) | Not built; the status exists, the endpoint and worker check were cut for time. |

## Crawling rules

| Requirement | How it's met |
|---|---|
| HTML pages only | The fetcher accepts only `text/html` and `application/xhtml+xml`; other content types are stored as Skipped. |
| Default depth 2 | `CrawlJob.DefaultMaxDepth = 2`; links deeper than the job's max depth are stored as Skipped with a reason. |
| Collect absolute URL, outgoing links, ratio | Each page stores its absolute URL, ratio and link count; `page_links` rows list its outgoing links. |
| Relative links | AngleSharp resolves links against the page URL, honouring `<base href>`; covered by unit and fixture tests. |
| Anchor links (ignore fragment) | `UrlNormalizer` drops fragments, so `page#top` and `page` normalize to the same URL. |
| No reprocessing of duplicate URLs | A unique index on `(job_id, url)` plus upserts ensures each normalized URL is crawled once per job. |
| Max pages per job | 200 pages per job, reserved atomically under a job-row lock; extra links stored as `Page limit reached`. |
| HTTP timeouts and retries | Polly pipeline: 10 s per attempt, 2 retries with exponential backoff and jitter, 30 s total. |

## Domain Link Ratio

| Requirement | How it's met |
|---|---|
| internal links / total outgoing links | `DomainLinkRatio.Calculate` divides internal by total distinct normalized links; unit and fixture tests cover it. |
| Starting domain = host of job URL | Compared against the job URL's host; subdomains and a leading `www.` count as internal (documented decision). |
| Ignore mailto:, tel:, non-http(s) | `UrlNormalizer` keeps only http and https links, so other schemes never reach the ratio. |
| Zero links → ratio 0 | `Calculate` returns 0 for an empty list; covered by a unit test and the `about.html` fixture. |

## Event-driven behaviour

| Requirement | How it's met |
|---|---|
| Idempotent worker, no duplicate rows | Unique keys on pages and edges, `ON CONFLICT` upserts and claim leases make redelivered messages harmless. |
| Retries for transient failures | Dapr resiliency retries failed deliveries five times with exponential backoff; Polly retries transient HTTP errors. |
| Dead-letter queue | After retries, Dapr routes the message to `dlq-crawl-worker-crawl-page`; the timeout sweep fails stuck jobs. |
| README: message schema | README "Message schema" shows the `CrawlPageMessage` JSON, topic, CloudEvent envelope and TTL. |
| README: idempotency strategy | README "Data model and idempotency" lists the unique keys, upserts, claims and publish tracking. |
| README: retry policy and transient errors | README "Retries…" section lists the HTTP and delivery retry policies and what counts as transient. |
| README: DLQ handling | README names the DLQ, what gets routed there and how affected jobs are failed by the sweep. |

## Frontend (React)

| Requirement | How it's met |
|---|---|
| Start Crawl: URL, optional maxDepth, navigate | `StartCrawlPage` has URL and depth inputs; on submit it creates the job and opens Job Details. |
| Job Details: status and timestamps | `JobDetailsPage` header shows the status badge, created/started/completed times, duration and failure reason. |
| Progress indicator (polling OK) | TanStack Query polls every 2 s while active; statistic cards and a progress bar show page counts. |
| Tree view once completed | `PageTree` shows an expandable tree with status, HTTP code, ratio and links per page. |
| History: list jobs, click to open | `HistoryPage` is a server-paginated table of jobs; clicking a row opens its Job Details page. |
| Clear loading and error states | Shared `States` components show spinners and error alerts; API errors show their problem-details message. |
| Clarity over styling | Ant Design components with default theme; no custom design system. |

## Engineering quality

| Requirement | How it's met |
|---|---|
| Unit tests: URL normalization | `UrlNormalizerTests` cover relative links, fragments, schemes, default ports, length limit and invalid input. |
| Unit tests: Domain Link Ratio | `DomainLinkRatioTests` cover internal, external, subdomain, `www.`, mixed and zero-link cases. |
| Integration test with HTML fixtures | `FixtureSiteCrawlTests` serves local HTML through TestServer and runs the real fetch, parse and ratio code. |
| Structured logs with jobId/correlationId | JSON console logs with scopes adding `JobId`, `PageId` and `CorrelationId` to every worker log line. |
| Health endpoints for API and worker | `/health` on both services checks PostgreSQL and the Dapr sidecar. |
| Separation of concerns | Projects split into Api, Worker, Domain (pure logic) and Infrastructure (EF Core, Dapr publisher). |
| Naming, small methods, error handling | Small focused classes; ProblemDetails for API errors; fetch failures become page statuses instead of exceptions. |

## Delivery

| Requirement | How it's met |
|---|---|
| Compose runs API, worker, DB, broker | `docker compose up -d --build` starts all of them; the API also serves the built React UI. |
| README: how to run locally | Two copy-paste commands at the top of the README, plus the UI URL and test sites. |
| README: architectural choices | README "Architecture" section has a diagram, project responsibilities and key choices with reasons. |
| README: known limitations | README "Known limitations" lists auth, robots.txt, rate limiting, re-crawl depth and other gaps. |
| README: built first, cut, next | README section "What I built first, what I cut, what's next" covers all three. |
| CI pipeline (optional) | Not built; listed under "Next" in the README as GitHub Actions build and test. |
