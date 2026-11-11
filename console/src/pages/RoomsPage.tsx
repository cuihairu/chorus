import {
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Row,
  Space,
  Spin,
  Statistic,
  Table,
  Tag,
  Typography,
} from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { AdminError, getAdminJson } from '../api/client';
import { useAuth } from '../auth';
import { useI18n } from '../i18n';

// The rooms page (FIX-WEB-07): the control plane's room records with the OBS-08 live
// stats beside them. Each face answers independently — the stats face for the totals,
// the records face for the room table, and the detail face for the room the operator
// names by clicking its row (state, lifecycle words, pending events, live connections).
// One face's failure is that card's error, never a blank screen; the numbers are the
// faces' own answers shown as they are.

interface RoomRow {
  id: string;
  workerId: string;
  workerInstance: string;
  epoch: number;
  state: string;
  updatedAtMs: number;
  streamerId: string | null;
  platform: string;
  mode: string;
  region: string;
  matchId: string | null;
  tickId: number;
}

interface TenantRooms {
  tenant: string;
  rooms: number;
}

interface RoomStats {
  total: number;
  active: number;
  byState: Record<string, number>;
  byTenant: TenantRooms[];
}

interface RoomConnection {
  sessionId: string;
  clientId: string;
  userId: string;
  role: string;
  gatewayId: string;
  gatewayInstance: string;
  generation: string;
  lastSeenMs: number;
}

interface RoomDetail {
  room: RoomRow;
  pendingEvents: number;
  connections: RoomConnection[];
}

type Face<T> = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'data'; value: T };

interface Faces {
  stats: Face<RoomStats>;
  records: Face<RoomRow[]>;
  detail: Face<RoomDetail>;
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

// Each face owns its JSON shape; a 200 that doesn't carry the face's own discriminators
// (an older control plane, a proxy interposing) is that card's error, never a page crash.
const faceGuards: Record<'stats' | 'records' | 'detail', (value: unknown) => boolean> = {
  stats: (value) => isRecord(value) && typeof value.total === 'number' && isRecord(value.byState),
  records: (value) => Array.isArray(value) && value.every((row) => isRecord(row) && typeof (row as { id?: unknown }).id === 'string'),
  detail: (value) => isRecord(value) && isRecord(value.room)
    && typeof (value.room as { id?: unknown }).id === 'string' && typeof value.pendingEvents === 'number',
};

export default function RoomsPage() {
  const { token } = useAuth();
  const { t } = useI18n();
  const [selected, setSelected] = useState<string>('');
  const [faces, setFaces] = useState<Faces>({ stats: { kind: 'loading' }, records: { kind: 'loading' }, detail: { kind: 'loading' } });

  const settle = useCallback(async (path: string, key: keyof Faces, guard: (value: unknown) => boolean) => {
    if (token === null) return;
    try {
      const value = await getAdminJson<unknown>(token, path);
      if (!guard(value)) {
        setFaces((current) => ({ ...current, [key]: { kind: 'error', message: 'unexpected shape' } }));
        return;
      }
      setFaces((current) => ({ ...current, [key]: { kind: 'data', value: value as never } }));
    } catch (error) {
      const message = error instanceof AdminError
        ? `${error.status}`
        : error instanceof Error ? error.message : 'error';
      setFaces((current) => ({ ...current, [key]: { kind: 'error', message } }));
    }
  }, [token]);

  const load = useCallback(() => {
    setFaces((current) => ({
      stats: { kind: 'loading' },
      records: { kind: 'loading' },
      detail: current.detail,
    }));
    void Promise.allSettled([
      settle('/api/rooms/stats', 'stats', faceGuards.stats),
      settle('/api/rooms', 'records', faceGuards.records),
    ]);
  }, [settle]);

  useEffect(() => { load(); }, [load]);

  const selectRoom = (roomId: string) => {
    setSelected(roomId);
    setFaces((current) => ({ ...current, detail: { kind: 'loading' } }));
    void settle(`/api/rooms/${encodeURIComponent(roomId)}`, 'detail', faceGuards.detail);
  };

  const faceCard = (title: string, state: Face<never>, render: (value: never) => ReactNode) => (
    <Card size="small" title={title}>
      {state.kind === 'loading' ? <Spin /> : state.kind === 'error'
        ? <Alert type="error" showIcon message={t('The face did not answer usable data', '该统计面未给出可用数据')} description={state.message} />
        : render(state.value)}
    </Card>
  );

  return (
    <Space direction="vertical" style={{ width: '100%' }} size={16}>
      <Space>
        <Button icon={<ReloadOutlined />} onClick={() => {
          load();
          if (selected !== '') {
            setFaces((current) => ({ ...current, detail: { kind: 'loading' } }));
            void settle(`/api/rooms/${encodeURIComponent(selected)}`, 'detail', faceGuards.detail);
          }
        }}>{t('Refresh', '刷新')}</Button>
        <Typography.Text type="secondary">
          {t('Click a room\'s row to read its detail face.', '点击房间行读取其详情面。')}
        </Typography.Text>
      </Space>
      <Row gutter={[16, 16]}>
        <Col xs={24} xl={10}>
          {faceCard(t('Live stats', '活值统计'), faces.stats as Face<never>, (value) => {
            const stats = value as RoomStats;
            return (
              <>
                <Space size={24}>
                  <Statistic title={t('Total', '总数')} value={stats.total} />
                  <Statistic title={t('Active', '活跃')} value={stats.active} />
                </Space>
                <div style={{ marginTop: 12, display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                  {Object.entries(stats.byState).map(([state, count]) => (
                    <Tag key={state}>{`${state}: ${count}`}</Tag>
                  ))}
                  {stats.byTenant.map((entry) => (
                    <Tag key={entry.tenant} color="blue">{`${entry.tenant}: ${entry.rooms}`}</Tag>
                  ))}
                </div>
              </>
            );
          })}
        </Col>
        <Col xs={24} xl={14}>
          {faceCard(t('Room records', '房间记录'), faces.records as Face<never>, (value) => {
            const rooms = value as RoomRow[];
            return (
              <Table
                size="small"
                rowKey="id"
                pagination={false}
                dataSource={rooms}
                onRow={(room) => ({ onClick: () => selectRoom(room.id), style: { cursor: 'pointer' } })}
                rowClassName={(room) => room.id === selected ? 'ant-table-row-selected' : ''}
                columns={[
                  { title: t('Room', '房间'), dataIndex: 'id' },
                  { title: t('State', '状态'), dataIndex: 'state', render: (state: string) => <Tag>{state}</Tag> },
                  { title: t('Epoch', '纪元'), dataIndex: 'epoch' },
                  { title: t('Tick', '跳动'), dataIndex: 'tickId' },
                  { title: t('Placement', '安放'), render: (_, room) => `${room.platform} / ${room.mode} / ${room.region}` },
                  { title: t('Streamer', '主播'), dataIndex: 'streamerId', render: (streamer: string | null) => streamer ?? '—' },
                  { title: t('Updated', '更新于'), render: (_, room) => new Date(room.updatedAtMs).toLocaleString() },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24}>
          {selected === '' ? (
            <Card size="small" title={t('Room detail', '房间详情')}>
              <Typography.Text type="secondary">{t('Select a room.', '选择一个房间。')}</Typography.Text>
            </Card>
          ) : faceCard(t('Room detail', '房间详情'), faces.detail as Face<never>, (value) => {
              const detail = value as RoomDetail;
              const room = detail.room;
              return (
                <>
                  <Descriptions size="small" column={{ xs: 1, md: 2 }} bordered>
                    <Descriptions.Item label={t('State', '状态')}><Tag>{room.state}</Tag></Descriptions.Item>
                    <Descriptions.Item label={t('Epoch', '纪元')}>{room.epoch}</Descriptions.Item>
                    <Descriptions.Item label={t('Tick', '跳动')}>{room.tickId}</Descriptions.Item>
                    <Descriptions.Item label={t('Worker', '工作进程')}>{`${room.workerId} / ${room.workerInstance}`}</Descriptions.Item>
                    <Descriptions.Item label={t('Placement', '安放')}>{`${room.platform} / ${room.mode} / ${room.region}`}</Descriptions.Item>
                    <Descriptions.Item label={t('Streamer', '主播')}>{room.streamerId ?? '—'}</Descriptions.Item>
                    <Descriptions.Item label={t('Match', '匹配')}>{room.matchId ?? '—'}</Descriptions.Item>
                    <Descriptions.Item label={t('Pending events', '待处理事件')}>{detail.pendingEvents}</Descriptions.Item>
                  </Descriptions>
                  <Typography.Title level={5} style={{ marginTop: 16 }}>{t('Connections', '连接')}</Typography.Title>
                  {detail.connections.length === 0
                    ? <Typography.Text type="secondary">{t('No live connections.', '无活连接。')}</Typography.Text>
                    : (
                      <Table
                        size="small"
                        rowKey="sessionId"
                        pagination={false}
                        dataSource={detail.connections}
                        columns={[
                          { title: t('Client', '客户端'), dataIndex: 'clientId' },
                          { title: t('User', '用户'), dataIndex: 'userId' },
                          { title: t('Role', '角色'), dataIndex: 'role' },
                          { title: t('Gateway', '网关'), render: (_, connection) => `${connection.gatewayId} / ${connection.gatewayInstance}` },
                          { title: t('Generation', '代'), dataIndex: 'generation' },
                          { title: t('Last seen', '末次在线'), render: (_, connection) => new Date(connection.lastSeenMs).toLocaleString() },
                        ]}
                      />
                    )}
                </>
              );
            })}
        </Col>
      </Row>
    </Space>
  );
}
