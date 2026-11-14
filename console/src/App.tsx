import {
  ClusterOutlined,
  HomeOutlined,
  LogoutOutlined,
  PartitionOutlined,
  PlayCircleOutlined,
  RobotOutlined,
  ThunderboltOutlined,
  TrophyOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { ProLayout } from '@ant-design/pro-components';
import { Button, Spin, Tag } from 'antd';
import type { ReactNode } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { AuthProvider, useAuth } from './auth';
import { LanguageProvider, LanguageToggle, useI18n } from './i18n';
import BotsPage from './pages/BotsPage';
import EpochPage from './pages/EpochPage';
import LeaderboardPage from './pages/LeaderboardPage';
import LoginPage from './pages/LoginPage';
import NodesPage from './pages/NodesPage';
import OperationsPage from './pages/OperationsPage';
import OverviewPage from './pages/OverviewPage';
import RoomsPage from './pages/RoomsPage';
import StreamersPage from './pages/StreamersPage';

// The console shell (FIX-WEB-02) with the operator session wired in (FIX-WEB-04):
// every admin route sits behind the login gate, the signed-in operator's identity is
// the header word, and a stored credential survives reloads only while who-am-i still
// answers for it. The header carries the language toggle (FIX-WEB-16): the two words
// for a menu entry ride the route table itself. Home is the operations overview
// (FIX-OBS-14), nodes and processes is the telemetry page (FIX-WEB-06), rooms is the
// room detail page (FIX-WEB-07), streamers and matching is the match plane's read
// faces (FIX-WEB-08).
type RouteKey = 'home' | 'nodes' | 'rooms' | 'streamers' | 'bots' | 'operations' | 'epoch' | 'leaderboard';

const menuRoutes: Array<{
  path: string;
  nameEn: string;
  nameZh: string;
  icon: ReactNode;
  routeKey: RouteKey;
}> = [
  { path: '/home', nameEn: 'Home', nameZh: '主页', icon: <HomeOutlined />, routeKey: 'home' },
  { path: '/nodes', nameEn: 'Nodes and processes', nameZh: '节点与进程', icon: <ClusterOutlined />, routeKey: 'nodes' },
  { path: '/rooms', nameEn: 'Rooms', nameZh: '房间', icon: <PlayCircleOutlined />, routeKey: 'rooms' },
  { path: '/streamers', nameEn: 'Streamers and matching', nameZh: '主播与匹配', icon: <PartitionOutlined />, routeKey: 'streamers' },
  { path: '/bots', nameEn: 'Bots', nameZh: '机器人', icon: <RobotOutlined />, routeKey: 'bots' },
  { path: '/operations', nameEn: 'Operations', nameZh: '操作', icon: <ClusterOutlined />, routeKey: 'operations' },
  { path: '/epoch', nameEn: 'Room epoch', nameZh: '房间纪元', icon: <ThunderboltOutlined />, routeKey: 'epoch' },
  { path: '/leaderboard', nameEn: 'Leaderboards', nameZh: '排行榜', icon: <TrophyOutlined />, routeKey: 'leaderboard' },
];

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
  const { t } = useI18n();
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
        routes: menuRoutes.map(({ path, nameEn, nameZh, icon }) => ({
          path, name: t(nameEn, nameZh), icon,
        })),
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
            <LanguageToggle />
            <Button type="text" size="small" icon={<LogoutOutlined />} onClick={logout}>
              {t('Sign out', '退出')}
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
      {routeKey === 'home' && <OverviewPage />}
      {routeKey === 'nodes' && <NodesPage />}
      {routeKey === 'rooms' && <RoomsPage />}
      {routeKey === 'streamers' && <StreamersPage />}
      {routeKey === 'bots' && <BotsPage />}
      {routeKey === 'operations' && <OperationsPage />}
      {routeKey === 'epoch' && <EpochPage />}
      {routeKey === 'leaderboard' && <LeaderboardPage />}
    </ConsoleShell>
  );
}

/** The routed console without the browser router — tests mount it inside MemoryRouter. */
export function ConsoleRoot() {
  return (
    <LanguageProvider>
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
    </LanguageProvider>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <ConsoleRoot />
    </BrowserRouter>
  );
}
