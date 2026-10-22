import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Card, Table, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { api, formatDate, formatDuration, isLinkClick, pollWhileAnyActive, type JobSummary } from '../api';
import { StatusBadge } from '../components/StatusBadge';
import { ErrorMessage } from '../components/States';

const PAGE_SIZE = 20;

const columns: ColumnsType<JobSummary> = [
  { title: 'URL', dataIndex: 'url', render: (url: string, job) => <Link to={`/jobs/${job.jobId}`}><Typography.Text strong>{url}</Typography.Text></Link> },
  { title: 'Status', dataIndex: 'status', width: 120, render: (status) => <StatusBadge status={status} /> },
  { title: 'Created', dataIndex: 'createdAt', width: 200, render: (value: string) => <Typography.Text type="secondary">{formatDate(value)}</Typography.Text> },
  { title: 'Duration', key: 'duration', width: 110, render: (_, job) => formatDuration(job.startedAt, job.completedAt) },
];

export function HistoryPage() {
  const navigate = useNavigate();
  // The page lives in the URL (?page=3) so Back from a job returns to it.
  const [searchParams, setSearchParams] = useSearchParams();
  const page = Math.max(1, Number(searchParams.get('page')) || 1);

  const jobs = useQuery({
    queryKey: ['jobs', page],
    queryFn: () => api.listJobs(page, PAGE_SIZE),
    placeholderData: keepPreviousData,
    refetchInterval: (query) => pollWhileAnyActive(query.state.data?.items),
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
        onRow={(job) => ({ onClick: (event) => !isLinkClick(event) && navigate(`/jobs/${job.jobId}`) })}
        pagination={{
          current: page,
          pageSize: PAGE_SIZE,
          showSizeChanger: false,
          total: jobs.data?.totalCount ?? 0,
          onChange: (next) => setSearchParams({ page: String(next) }),
          showTotal: (total) => `${total} jobs`,
        }}
      />
    </Card>
  );
}
