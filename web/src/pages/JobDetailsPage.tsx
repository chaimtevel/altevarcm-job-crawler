import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Card, Col, Popconfirm, Progress, Result, Row, Space, Statistic, Typography } from 'antd';
import { api, ApiError, formatDate, formatDuration, isActive, type JobDetails, type JobStatus } from '../api';
import { StatusBadge } from '../components/StatusBadge';
import { ErrorMessage, Loading } from '../components/States';
import { PageTree } from '../components/PageTree';

const POLL_INTERVAL_MS = 2000;

export function JobDetailsPage() {
  const { jobId = '' } = useParams();

  const queryClient = useQueryClient();
  const live = useJobEvents(jobId);

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => api.getJob(jobId),
    // Live updates arrive over SSE; poll only while the stream is down and the job still runs.
    refetchInterval: (query) => (!live && isActive(query.state.data?.status) ? POLL_INTERVAL_MS : false),
  });

  const cancelJob = useMutation({
    // 409 = the job finished on its own just before the click; the refresh below shows its final status.
    mutationFn: () => api.cancelJob(jobId).catch((error) => {
      if (!(error instanceof ApiError && error.status === 409)) {
        throw error;
      }
    }),
    // Refresh the job and job lists so every screen shows Canceled right away.
    onSettled: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ['job', jobId] }),
      queryClient.invalidateQueries({ queryKey: ['jobs'] }),
    ]),
  });

  const finished = job.data !== undefined && !isActive(job.data.status);

  const tree = useQuery({
    queryKey: ['tree', jobId],
    queryFn: () => api.getTree(jobId),
    enabled: finished,
  });

  if (job.isPending) {
    return <Loading label="Loading job…" />;
  }
  if (job.error instanceof ApiError && job.error.status === 404) {
    return (
      <Result
        status="404"
        title="Job not found"
        subTitle="This crawl job doesn't exist, or the database was reset."
        extra={<Link to="/history">Back to history</Link>}
      />
    );
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
  const canceled = data.status === 'Canceled';

  const progressText = () => {
    if (data.status === 'Pending') {
      return 'Queued, waiting for a worker to pick up the start page…';
    }
    if (running) {
      const updates = live ? 'live' : `updates every ${POLL_INTERVAL_MS / 1000}s`;
      return `Crawling… ${done} of ${toFetch} pages fetched (limit ${data.maxPages}, ${updates})`;
    }
    if (canceled) {
      return `Canceled after ${done} pages fetched · ${progress.discovered} URLs recorded`;
    }
    return `${progress.crawled} pages crawled · ${progress.discovered} URLs recorded`;
  };

  return (
    <Space direction="vertical" size="large" className="full">
      <Card>
        <Space align="center" wrap>
          <Typography.Title level={3} className="no-margin" copyable={{ text: data.url, tooltips: ['Copy URL', 'Copied'] }}>
            <a href={data.url} target="_blank" rel="noreferrer">{data.url}</a>
          </Typography.Title>
          <StatusBadge status={data.status} />
          {running && (
            <Popconfirm
              title="Cancel this crawl?"
              description="Pages not fetched yet will be skipped."
              okText="Cancel crawl"
              cancelText="Keep running"
              onConfirm={() => cancelJob.mutate()}
            >
              <Button danger size="small" loading={cancelJob.isPending}>Cancel</Button>
            </Popconfirm>
          )}
        </Space>
        {cancelJob.isError && running && <Alert type="error" showIcon message="Could not cancel the crawl" description={cancelJob.error.message} />}
        <Typography.Paragraph type="secondary" className="meta">
          Created {formatDate(data.createdAt)} · Started {formatDate(data.startedAt)} · Completed{' '}
          {formatDate(data.completedAt)} · Duration {formatDuration(data.startedAt, data.completedAt)} · Max depth{' '}
          {data.maxDepth} · Limit {data.maxPages} pages · Job <Typography.Text code copyable={{ tooltips: ['Copy job ID', 'Copied'] }}>{data.jobId}</Typography.Text>
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
        {/* Cancel skips all pending pages, so a percentage would read 100%; the text says what happened. */}
        {!canceled && <Progress percent={running ? percent : 100} status={progressStatus(data.status)} />}
        <Typography.Text type="secondary">{progressText()}</Typography.Text>
      </Card>

      {finished && (
        <Card title="Discovered pages">
          <Typography.Paragraph type="secondary">
            <b>Domain Link Ratio</b> = links that stay on the starting site ÷ all links on the page (1.00 = all internal).
            Expand a row to see the pages it led to; click "links" to see every outgoing link. The tree lists
            fetched pages only: links skipped without a fetch (external, too deep, over the page limit) appear in
            each page's links, with the reason.
          </Typography.Paragraph>
          {tree.isPending && <Loading label="Loading pages…" />}
          {tree.isError && <ErrorMessage error={tree.error} />}
          {tree.data && (tree.data.root ? <PageTree jobId={jobId} root={tree.data.root} /> : <Typography.Text type="secondary">No pages were crawled.</Typography.Text>)}
        </Card>
      )}
    </Space>
  );
}

/**
 * Subscribes to the job's Server-Sent Events and writes each update into the query cache.
 * Returns whether the stream is connected; the browser reconnects on its own after errors.
 */
function useJobEvents(jobId: string) {
  const queryClient = useQueryClient();
  const [live, setLive] = useState(false);

  useEffect(() => {
    const events = new EventSource(`/api/jobs/${jobId}/events`);
    events.onopen = () => setLive(true);
    events.onerror = () => setLive(false);
    events.onmessage = (message) => {
      const job = JSON.parse(message.data) as JobDetails;
      queryClient.setQueryData(['job', jobId], job);
      if (!isActive(job.status)) {
        // The server ends the stream for finished jobs; close so the browser doesn't reconnect.
        events.close();
        setLive(false);
      }
    };
    return () => events.close();
  }, [jobId, queryClient]);

  return live;
}

function progressStatus(status: JobStatus) {
  switch (status) {
    case 'Pending':
    case 'Running':
      return 'active';
    case 'Failed':
      return 'exception';
    default:
      return 'success';
  }
}
