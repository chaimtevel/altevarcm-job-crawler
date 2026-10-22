import { Layout, Menu, Result, Typography } from 'antd';
import { Link, Route, Routes, useLocation } from 'react-router-dom';
import { StartCrawlPage } from './pages/StartCrawlPage';
import { JobDetailsPage } from './pages/JobDetailsPage';
import { HistoryPage } from './pages/HistoryPage';

export function App() {
  const { pathname } = useLocation();
  const selected = pathname.startsWith('/history') ? 'history' : pathname === '/' ? 'new' : '';

  return (
    <Layout className="app">
      <Layout.Header className="app-header">
        <Typography.Title level={4} className="app-title">Web Crawler</Typography.Title>
        <Menu
          theme="dark"
          mode="horizontal"
          selectedKeys={[selected]}
          items={[
            { key: 'new', label: <Link to="/">New crawl</Link> },
            { key: 'history', label: <Link to="/history">History</Link> },
          ]}
          className="app-menu"
        />
      </Layout.Header>
      <Layout.Content className="app-content">
        <Routes>
          <Route path="/" element={<StartCrawlPage />} />
          <Route path="/jobs/:jobId" element={<JobDetailsPage />} />
          <Route path="/history" element={<HistoryPage />} />
          <Route path="*" element={<Result status="404" title="Page not found" />} />
        </Routes>
      </Layout.Content>
    </Layout>
  );
}
