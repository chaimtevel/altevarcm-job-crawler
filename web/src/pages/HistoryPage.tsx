import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Card, Table, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { api, formatDate, formatDuration, isActive, type JobSummary } from '../api';
import { StatusBadge } from '../components/StatusBadge';
import { ErrorMessage } from '../components/States';

const PAGE_SIZE = 20;
const POLL_INTERVAL_MS = 5000;

const columns: ColumnsType<JobSummary> = [
  { title: 'URL', dataIndex: 'url', render: (url: string) => <Typography.Text strong>{url}</Typography.Text> },
  { title: 'Status', dataIndex: 'status', width: 120, render: (status) => <StatusBadge status={status} /> },
  { title: 'Created', dataIndex: 'createdAt', width: 200, render: (value: string) => <Typography.Text type="secondary">{formatDate(value)}</Typography.Text> },
  { title: 'Duration', key: 'duration', width: 110, render: (_, job) => formatDuration(job.startedAt, job.completedAt) },
];

export function HistoryPage() {
  const navigate = useNavigate();
  const [page, setPage] = useState(1);

  const jobs = useQuery({
    queryKey: ['jobs', page],
    queryFn: () => api.listJobs(page, PAGE_SIZE),
    placeholderData: keepPreviousData,
    // Poll only while a job on this page is still running.
    refetchInterval: (query) => (query.state.data?.items.some((job) => isActive(job.status)) ? POLL_INTERVAL_MS : false),
  });

  if (jobs.isError) {
    return <ErrorMessage error={jobs.error} />;
  }

  return (
    <Card title="Crawl history">
      <Table<JobSummary>
        rowKey="jobId"
        columns={columns}
        dataSource={jobs.data?.items ?? []}
        loading={jobs.isPending}
        locale={{ emptyText: 'No crawls yet.' }}
        rowClassName="clickable"
        onRow={(job) => ({ onClick: () => navigate(`/jobs/${job.jobId}`) })}
        pagination={{
          current: page,
          pageSize: PAGE_SIZE,
          total: jobs.data?.totalCount ?? 0,
          onChange: setPage,
          showTotal: (total) => `${total} jobs`,
        }}
      />
    </Card>
  );
}
