import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Alert, Button, Card, Form, Input, InputNumber, List, Typography } from 'antd';
import { api, ApiError, DEFAULT_MAX_DEPTH, formatDate, MAX_DEPTH, MAX_PAGES } from '../api';
import { StatusBadge } from '../components/StatusBadge';

export function StartCrawlPage() {
  const navigate = useNavigate();
  const [url, setUrl] = useState('');
  const [maxDepth, setMaxDepth] = useState<number | null>(null);

  const createJob = useMutation({
    mutationFn: () => api.createJob(url.trim(), maxDepth ?? undefined),
    onSuccess: ({ jobId }) => navigate(`/jobs/${jobId}`),
  });

  const recent = useQuery({ queryKey: ['jobs', 'recent'], queryFn: () => api.listJobs(1, 5) });

  const fieldErrors = createJob.error instanceof ApiError ? createJob.error.fieldErrors : {};
  const urlError = fieldErrors.url?.join(' ');
  const depthError = fieldErrors.maxDepth?.join(' ');

  return (
    <div className="narrow">
      <Card>
        <Typography.Title level={3}>Crawl a website</Typography.Title>
        <Typography.Paragraph type="secondary">
          Enter a URL. The crawler follows links on the same site up to the chosen depth (max {MAX_PAGES} pages) and
          reports each page's Domain Link Ratio — the share of its links that stay on the site.
        </Typography.Paragraph>

        <Form layout="vertical" onFinish={() => createJob.mutate()} requiredMark={false}>
          <div className="crawl-bar">
            <Form.Item label="Website URL" validateStatus={urlError ? 'error' : undefined} help={urlError} className="grow">
              <Input
                size="large"
                placeholder="https://example.com"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                autoFocus
              />
            </Form.Item>
            <Form.Item label="Max depth" validateStatus={depthError ? 'error' : undefined} help={depthError}>
              <InputNumber size="large" min={0} max={MAX_DEPTH} placeholder={String(DEFAULT_MAX_DEPTH)} value={maxDepth} onChange={setMaxDepth} />
            </Form.Item>
            <Form.Item label=" ">
              <Button type="primary" size="large" htmlType="submit" loading={createJob.isPending} disabled={!url.trim()}>
                Start crawl
              </Button>
            </Form.Item>
          </div>
        </Form>

        {createJob.isError && !urlError && !depthError && (
          <Alert type="error" showIcon message="Could not start the crawl" description={createJob.error.message} />
        )}
      </Card>

      <Card title="Recent jobs" extra={<Link to="/history">View all</Link>} className="section">
        <List
          loading={recent.isPending}
          dataSource={recent.data?.items ?? []}
          locale={{ emptyText: 'No crawls yet — start one above.' }}
          renderItem={(job) => (
            <List.Item className="clickable" onClick={() => navigate(`/jobs/${job.jobId}`)} extra={<StatusBadge status={job.status} />}>
              <List.Item.Meta title={job.url} description={formatDate(job.createdAt)} />
            </List.Item>
          )}
        />
      </Card>
    </div>
  );
}
