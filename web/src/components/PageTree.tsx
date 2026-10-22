import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Button, Drawer, List, Table, Tag, Tooltip, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { api, type TreeNode } from '../api';
import { StatusBadge } from './StatusBadge';
import { ErrorMessage } from './States';

/** antd's tree table expects leaf rows without a `children` array. */
type Row = Omit<TreeNode, 'children'> & { children?: Row[] };

const toRow = (node: TreeNode): Row => ({
  ...node,
  children: node.children.length > 0 ? node.children.map(toRow) : undefined,
});

export function PageTree({ jobId, root }: { jobId: string; root: TreeNode }) {
  const [linksFor, setLinksFor] = useState<Row | null>(null);

  const columns: ColumnsType<Row> = [
    {
      title: 'Page',
      dataIndex: 'url',
      render: (url: string) => <a href={url} target="_blank" rel="noreferrer">{url}</a>,
    },
    {
      title: 'Status',
      dataIndex: 'status',
      width: 200,
      render: (_, row) => (
        <>
          <StatusBadge status={row.status} />
          {row.statusReason && (
            <Tooltip title={row.statusReason}>
              <Typography.Text type="secondary" className="reason">{row.statusReason}</Typography.Text>
            </Tooltip>
          )}
        </>
      ),
    },
    { title: 'HTTP', dataIndex: 'httpStatusCode', width: 70, render: (code: number | null) => code ?? '—' },
    {
      title: 'Ratio',
      dataIndex: 'domainLinkRatio',
      width: 80,
      align: 'right',
      render: (ratio: number | null) => (ratio === null ? '—' : ratio.toFixed(2)),
    },
    {
      title: 'Links',
      dataIndex: 'outgoingLinkCount',
      width: 90,
      align: 'right',
      render: (count: number | null, row) =>
        count ? <Button type="link" size="small" onClick={() => setLinksFor(row)}>{count} links</Button> : '—',
    },
  ];

  return (
    <>
      <Table<Row>
        rowKey="pageId"
        size="small"
        columns={columns}
        dataSource={[toRow(root)]}
        expandable={{ defaultExpandedRowKeys: [root.pageId] }}
        pagination={false}
      />
      <Drawer
        title={linksFor ? `Outgoing links of ${linksFor.url}` : ''}
        width={640}
        open={linksFor !== null}
        onClose={() => setLinksFor(null)}
        destroyOnClose
      >
        {linksFor && <OutgoingLinks jobId={jobId} pageId={linksFor.pageId} />}
      </Drawer>
    </>
  );
}

function OutgoingLinks({ jobId, pageId }: { jobId: string; pageId: number }) {
  const links = useQuery({
    queryKey: ['links', jobId, pageId],
    queryFn: () => api.getPageLinks(jobId, pageId),
  });

  if (links.isError) {
    return <ErrorMessage error={links.error} />;
  }

  return (
    <List
      size="small"
      loading={links.isPending}
      dataSource={links.data ?? []}
      renderItem={(link) => (
        <List.Item>
          <Tag color={link.isInternal ? 'green' : 'orange'}>{link.isInternal ? 'internal' : 'external'}</Tag>
          <a href={link.url} target="_blank" rel="noreferrer" className="grow">{link.url}</a>
          <StatusBadge status={link.status} />
        </List.Item>
      )}
    />
  );
}
