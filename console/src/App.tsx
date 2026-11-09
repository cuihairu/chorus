import {
  ClusterOutlined,
  HomeOutlined,
  PartitionOutlined,
  PlayCircleOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { PageContainer, ProLayout } from '@ant-design/pro-components';
import { Result, Spin } from 'antd';
import { useState } from 'react';

// The console shell (FIX-WEB-02). Every route the 22.16 line will fill is declared here
// so the menu is the contract of the console's shape; the pages themselves arrive with
// their backing APIs — login (WEB-04), nodes and processes (WEB-06), rooms (WEB-07),
// streamers and matching (WEB-08), operations (WEB-10).
type RouteKey = 'home' | 'login' | 'nodes' | 'rooms' | 'streamers' | 'operations';

function Placeholder({ routeKey }: { routeKey: RouteKey }) {
  const names: Record<RouteKey, string> = {
    home: 'Home',
    login: 'Operator login',
    nodes: 'Nodes and processes',
    rooms: 'Rooms',
    streamers: 'Streamers and matching',
    operations: 'Operations and receipts',
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

export default function App() {
  const [route, setRoute] = useState<RouteKey>('home');
  return (
    <ProLayout
      title="Chorus Console"
      layout="mix"
      fixedHeader
      contentWidth="Fixed"
      route={{
        path: '/',
        routes: [
          { path: '/home', name: 'Home', icon: <HomeOutlined /> },
          { path: '/login', name: 'Operator login', icon: <UserOutlined /> },
          { path: '/nodes', name: 'Nodes and processes', icon: <ClusterOutlined /> },
          { path: '/rooms', name: 'Rooms', icon: <PlayCircleOutlined /> },
          { path: '/streamers', name: 'Streamers and matching', icon: <PartitionOutlined /> },
          { path: '/operations', name: 'Operations', icon: <ClusterOutlined /> },
        ],
      }}
      menuItemRender={(item, dom) => (
        <a onClick={() => setRoute(item.path?.slice(1) as RouteKey)}>{dom}</a>
      )}
      location={{ pathname: `/${route}` }}
    >
      <Placeholder routeKey={route} />
    </ProLayout>
  );
}
