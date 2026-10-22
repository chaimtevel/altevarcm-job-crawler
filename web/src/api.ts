import { format, formatDuration as formatDurationText, intervalToDuration } from 'date-fns';

// Mirror the API's CrawlJob limits; the API validates and enforces them.
export const MAX_DEPTH = 5;
export const DEFAULT_MAX_DEPTH = 2;
export const MAX_PAGES = 200;

export type JobStatus ='Pending' | 'Running' | 'Completed' | 'Failed' | 'Canceled';
export type PageStatus = 'Pending' | 'Crawled' | 'Failed' | 'Skipped';

export interface JobProgress {
  discovered: number;
  pending: number;
  crawled: number;
  failed: number;
  skipped: number;
}

export interface JobDetails {
  jobId: string;
  url: string;
  maxDepth: number;
  maxPages: number;
  status: JobStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  failureReason: string | null;
  progress: JobProgress;
}

export interface JobSummary {
  jobId: string;
  url: string;
  status: JobStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
}

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface TreeNode {
  pageId: number;
  url: string;
  depth: number;
  isInternal: boolean;
  status: PageStatus;
  statusReason: string | null;
  httpStatusCode: number | null;
  domainLinkRatio: number | null;
  outgoingLinkCount: number | null;
  children: TreeNode[];
}

export interface JobTree {
  jobId: string;
  status: JobStatus;
  root: TreeNode | null;
}

export interface PageLink {
  pageId: number;
  url: string;
  isInternal: boolean;
  status: PageStatus;
}

/** Error with the API's validation messages (ProblemDetails "errors"), if any. */
export class ApiError extends Error {
  constructor(message: string, public status: number, public fieldErrors: Record<string, string[]> = {}) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  });

  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new ApiError(problem?.title ?? `Request failed (${response.status})`, response.status, problem?.errors ?? {});
  }

  return response.json() as Promise<T>;
}

export const api = {
  createJob: (url: string, maxDepth?: number) =>
    request<{ jobId: string }>('/jobs', { method: 'POST', body: JSON.stringify({ url, maxDepth }) }),
  getJob: (jobId: string) => request<JobDetails>(`/jobs/${jobId}`),
  getTree: (jobId: string) => request<JobTree>(`/jobs/${jobId}/tree`),
  getPageLinks: (jobId: string, pageId: number) => request<PageLink[]>(`/jobs/${jobId}/pages/${pageId}/links`),
  listJobs: (page: number, pageSize: number) =>
    request<PagedResponse<JobSummary>>(`/jobs?page=${page}&pageSize=${pageSize}`),
};

export const isActive = (status: JobStatus | undefined) => status === 'Pending' || status === 'Running';

export const formatDate = (value: string | null) => (value ? format(value, 'PPpp') : '—');

export const formatDuration = (start: string | null, end: string | null) =>
  start && end ? formatDurationText(intervalToDuration({ start, end }), { format: ['minutes', 'seconds'] }) || '0 seconds' : '—';
