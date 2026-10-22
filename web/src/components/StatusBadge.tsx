import { Tag } from 'antd';
import type { JobStatus, PageStatus } from '../api';

const colors: Record<JobStatus | PageStatus, string> = {
  Pending: 'gold',
  Running: 'processing',
  Completed: 'success',
  Crawled: 'success',
  Failed: 'error',
  Skipped: 'default',
  Canceled: 'default',
};

export function StatusBadge({ status }: { status: JobStatus | PageStatus }) {
  return <Tag color={colors[status]}>{status}</Tag>;
}
