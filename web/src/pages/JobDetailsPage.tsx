import { useMemo, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Alert, Card, Col, Progress, Row, Space, Statistic, Switch, Typography } from 'antd';
import { api, formatDate, formatDuration, isActive, type TreeNode } from '../api';
import { StatusBadge } from '../components/StatusBadge';
import { ErrorMessage, Loading } from '../components/States';
import { PageTree } from '../components/PageTree';

const POLL_INTERVAL_MS = 2000;

export function JobDetailsPage() {
  const { jobId = '' } = useParams();
  const [hideSkipped, setHideSkipped] = useState(false);

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => api.getJob(jobId),
    // Poll while the job runs; stop once it reaches a final status.
    refetchInterval: (query) => (isActive(query.state.data?.status) ? POLL_INTERVAL_MS : false),
  });

  const finished = job.data !== undefined && !isActive(job.data.status);

  const tree = useQuery({
    queryKey: ['tree', jobId],
    queryFn: () => api.getTree(jobId),
    enabled: finished,
  });

  const root = useMemo(
    () => (tree.data?.root && hideSkipped ? withoutSkipped(tree.data.root) : tree.data?.root ?? null),
    [tree.data, hideSkipped],
  );

  if (job.isPending) {
    return <Loading label="Loading job…" />;
  }
  if (job.isError) {
    return <ErrorMessage error={job.error} />;
  }

  const { data } = job;
  const { progress } = data;
  // Only pages to fetch count; external, too-deep and over-limit links are Skipped without a fetch.
  const done = progress.crawled + progress.failed;
  const toFetch = done + progress.pending;
  const percent = toFetch === 0 ? 0 : Math.round((done / toFetch) * 100);
  const running = isActive(data.status);

  return (
    <Space direction="vertical" size="large" className="full">
      <Card>
        <Space align="center" wrap>
          <Typography.Title level={3} className="no-margin">
            <a href={data.url} target="_blank" rel="noreferrer">{data.url}</a>
          </Typography.Title>
          <StatusBadge status={data.status} />
        </Space>
        <Typography.Paragraph type="secondary" className="meta">
          Created {formatDate(data.createdAt)} · Started {formatDate(data.startedAt)} · Completed{' '}
          {formatDate(data.completedAt)} · Duration {formatDuration(data.startedAt, data.completedAt)} · Max depth{' '}
          {data.maxDepth} · Limit {data.maxPages} pages · Job <Typography.Text code>{data.jobId}</Typography.Text>
        </Typography.Paragraph>
        {data.failureReason && <Alert type="error" showIcon message="Job failed" description={data.failureReason} />}
      </Card>

      <Row gutter={16}>
        <Col xs={12} md={6}><Card><Statistic title="Crawled" value={progress.crawled} valueStyle={{ color: '#3f8600' }} /></Card></Col>
        <Col xs={12} md={6}><Card><Statistic title="Failed" value={progress.failed} valueStyle={{ color: progress.failed ? '#cf1322' : undefined }} /></Card></Col>
        <Col xs={12} md={6}><Card><Statistic title="Skipped" value={progress.skipped} /></Card></Col>
        <Col xs={12} md={6}><Card><Statistic title="Pending" value={progress.pending} /></Card></Col>
      </Row>

      <Card size="small">
        <Progress percent={running ? percent : 100} status={running ? 'active' : data.status === 'Failed' ? 'exception' : 'success'} />
        <Typography.Text type="secondary">
          {running
            ? `Crawling… ${done} of ${toFetch} pages fetched (limit ${data.maxPages}, updates every ${POLL_INTERVAL_MS / 1000}s)`
            : `${progress.discovered} pages discovered`}
        </Typography.Text>
      </Card>

      {finished && (
        <Card
          title="Discovered pages"
          extra={<Space><Switch size="small" checked={hideSkipped} onChange={setHideSkipped} /> Hide skipped</Space>}
        >
          <Typography.Paragraph type="secondary">
            <b>Domain Link Ratio</b> = links that stay on the starting site ÷ all links on the page (1.00 = all internal).
            Expand a row to see the pages it led to; click "links" to see every outgoing link. The tree lists
            fetched pages only: links skipped without a fetch (external, too deep, over the page limit) appear in
            each page's links, with the reason.
          </Typography.Paragraph>
          {tree.isPending && <Loading label="Loading pages…" />}
          {tree.isError && <ErrorMessage error={tree.error} />}
          {tree.data && (root ? <PageTree jobId={jobId} root={root} /> : <Typography.Text type="secondary">No pages were crawled.</Typography.Text>)}
        </Card>
      )}
    </Space>
  );
}

function withoutSkipped(node: TreeNode): TreeNode {
  return { ...node, children: node.children.filter((c) => c.status !== 'Skipped').map(withoutSkipped) };
}
