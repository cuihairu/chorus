import {
  ClusterOutlined,
  HomeOutlined,
  LogoutOutlined,
  PartitionOutlined,
  PlayCircleOutlined,
  ThunderboltOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { PageContainer, ProLayout } from '@ant-design/pro-components';
import { Button, Result, Spin, Tag } from 'antd';
import type { ReactNode } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { AuthProvider, useAuth } from './auth';
import EpochPage from './pages/EpochPage';
import LoginPage from './pages/LoginPage';
import OperationsPage from './pages/OperationsPage';

// The console shell (FIX-WEB-02) with the operator session wired in (FIX-WEB-04):
// every admin route sits behind the login gate, the signed-in operator's identity is
// the header word, and a stored credential survives reloads only while who-am-i still
// answers for it. The remaining pages arrive with their backing APIs — nodes and
// processes (WEB-06), rooms (WEB-07), streamers and matching (WEB-08).
type RouteKey = 'home' | 'nodes' | 'rooms' | 'streamers' | 'operations' | 'epoch';

const menuRoutes: Array<{ path: string; name: string; icon: ReactNode; routeKey: RouteKey }> = [
  { path: '/home', name: 'Home', icon: <HomeOutlined />, routeKey: 'home' },
  { path: '/nodes', name: 'Nodes and processes', icon: <ClusterOutlined />, routeKey: 'nodes' },
  { path: '/rooms', name: 'Rooms', icon: <PlayCircleOutlined />, routeKey: 'rooms' },
  { path: '/streamers', name: 'Streamers and matching', icon: <PartitionOutlined />, routeKey: 'streamers' },
  { path: '/operations', name: 'Operations', icon: <ClusterOutlined />, routeKey: 'operations' },
  { path: '/epoch', name: 'Room epoch', icon: <ThunderboltOutlined />, routeKey: 'epoch' },
];

function Placeholder({ routeKey }: { routeKey: RouteKey }) {
  const names: Record<RouteKey, string> = {
    home: 'Home',
    nodes: 'Nodes and processes',
    rooms: 'Rooms',
    streamers: 'Streamers and matching',
    operations: 'Operations and receipts',
    epoch: 'Room epoch',
  };
  return (
    <PageContainer>
      <Result
        icon={<Spin size="large" />}
        title={names[routeKey]}
        subTitle="This page arrives with its backing API line (todo 22.16)."
      />
    </PageContainer>
  );
}

/** Every console route but the login page sits behind a resolved operator identity. */
function RequireOperator({ children }: { children: ReactNode }) {
  const { token, operator, restoring } = useAuth();
  if (token === null) {
    return <Navigate to="/login" replace />;
  }
  if (restoring || operator === null) {
    return (
      <div style={{ display: 'grid', placeItems: 'center', minHeight: '60vh' }}>
        <Spin size="large" />
      </div>
    );
  }
  return children;
}

function ConsoleShell({ children }: { children: ReactNode }) {
  const { operator, logout } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  return (
    <ProLayout
      title="Chorus Console"
      layout="mix"
      fixedHeader
      contentWidth="Fixed"
      route={{
        path: '/',
        routes: menuRoutes.map(({ path, name, icon }) => ({ path, name, icon })),
      }}
      menuItemRender={(item, dom) => (
        <a onClick={() => navigate(item.path ?? '/home')}>{dom}</a>
      )}
      location={{ pathname: location.pathname }}
      avatarProps={{
        icon: <UserOutlined />,
        render: (_props, dom) => (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
            {dom}
            <Tag color="blue">{operator?.role}</Tag>
            <Button type="text" size="small" icon={<LogoutOutlined />} onClick={logout}>
              Sign out
            </Button>
          </span>
        ),
      }}
    >
      {children}
    </ProLayout>
  );
}

function ConsolePages() {
  const location = useLocation();
  const routeKey =
    menuRoutes.find((entry) => entry.path === location.pathname)?.routeKey ?? 'home';
  return (
    <ConsoleShell>
      {routeKey === 'operations' && <OperationsPage />}
      {routeKey === 'epoch' && <EpochPage />}
      {routeKey !== 'operations' && routeKey !== 'epoch' && <Placeholder routeKey={routeKey} />}
    </ConsoleShell>
  );
}

/** The routed console without the browser router — tests mount it inside MemoryRouter. */
export function ConsoleRoot() {
  return (
    <AuthProvider>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route
          path="*"
          element={
            <RequireOperator>
              <Routes>
                <Route path="/" element={<Navigate to="/home" replace />} />
                <Route path="*" element={<ConsolePages />} />
              </Routes>
            </RequireOperator>
          }
        />
      </Routes>
    </AuthProvider>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <ConsoleRoot />
    </BrowserRouter>
  );
}
