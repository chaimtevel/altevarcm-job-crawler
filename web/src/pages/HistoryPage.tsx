import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Card, Input, Table, Typography } from 'antd';
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
  const search = searchParams.get('q') ?? '';

  const jobs = useQuery({
    queryKey: ['jobs', page, search],
    queryFn: () => api.listJobs(page, PAGE_SIZE, search),
    placeholderData: keepPreviousData,
    refetchInterval: (query) => pollWhileAnyActive(query.state.data?.items),
  });

  if (jobs.isError) {
    return <ErrorMessage error={jobs.error} />;
  }

  return (
    <Card
      title="Crawl history"
      extra={
        <Input.Search
          placeholder="Search URLs, e.g. books.toscrape"
          allowClear
          defaultValue={search}
          onSearch={(value) => setSearchParams(value.trim() ? { q: value.trim() } : {})}
          style={{ width: 280 }}
        />
      }
    >
      <Table<JobSummary>
        rowKey="jobId"
        columns={columns}
        dataSource={jobs.data?.items ?? []}
        loading={jobs.isPending}
        locale={{ emptyText: search ? `No crawls match "${search}".` : 'No crawls yet.' }}
        rowClassName="clickable"
        onRow={(job) => ({ onClick: (event) => !isLinkClick(event) && navigate(`/jobs/${job.jobId}`) })}
        pagination={{
          current: page,
          pageSize: PAGE_SIZE,
          showSizeChanger: false,
          total: jobs.data?.totalCount ?? 0,
          onChange: (next) => setSearchParams({ page: String(next), ...(search && { q: search }) }),
          showTotal: (total) => `${total} jobs`,
        }}
      />
    </Card>
  );
}
