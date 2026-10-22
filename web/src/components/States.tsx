import { Alert, Spin } from 'antd';

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="loading">
      <Spin tip={label}><div className="loading-box" /></Spin>
    </div>
  );
}

export function ErrorMessage({ error }: { error: unknown }) {
  const message = error instanceof Error ? error.message : 'Something went wrong.';
  return <Alert type="error" showIcon message="Something went wrong" description={message} />;
}
