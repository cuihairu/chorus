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

// The operations overview (FIX-OBS-14, with the threshold words of FIX-OBS-15): one
// screen that answers "is the fleet healthy" from the statistics faces — the computed
// threshold alerts (OBS-15), machines (OBS-07), rooms and online (OBS-08/09), the raw
// ingress and the event delivery chain (OBS-10/11), the outbound platform calls
// (OBS-12), and the counters' windowed increments with the retained history behind them
// (OBS-13). Each face answers independently, so one face's failure is that card's
// error, never a blank screen; the numbers are the faces' own answers shown as they
// are — no averaging across horizons and no verdict beyond what the alerts face itself
// states.

interface MachineMemory {
  totalBytes: number;
  usedBytes: number;
  usedPercent: number;
}

interface MachineNode {
  nodeId: string;
  sampledAtUtc: string;
  cpuPercent: number | null;
  memory: MachineMemory | null;
  samples: number;
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

interface TenantGameCount {
  tenant: string;
  game: string;
  count: number;
}

interface OnlineStats {
  playersTotal: number;
  streamersTotal: number;
  playersByTenantGame: TenantGameCount[];
  streamersByTenant: { tenant: string; count: number }[];
  roomsByTenantGame: TenantGameCount[];
}

interface IngressStats {
  accepted: number;
  duplicates: number;
  unverified: number;
  unmapped: number;
  rateLimited: number;
  tooLarge: number;
  timedOut: number;
  inputEventsBySource: { source: string; count: number }[];
}

interface DeliveryStats {
  ingested: number;
  applied: number;
  merged: number;
  dropped: number;
  delivered: number;
  effectsApplied: number;
  effectsPending: number;
  effectsDispatching: number;
  effectsDone: number;
  effectsFailed: number;
}

interface PlatformEndpoint {
  endpoint: string;
  calls: number;
  failures: number;
}

interface PlatformStats {
  endpoints: PlatformEndpoint[];
}

interface OpsFamily {
  family: string;
  delta: number;
}

interface OpsWindow {
  windowMinutes: number;
  families: OpsFamily[];
}

interface OpsAlertItem {
  kind: string;
  subject: string;
  observed: number;
  threshold: number;
  detail: string;
}

type Face<T> = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'data'; value: T };

interface Faces {
  alerts: Face<OpsAlertItem[]>;
  machines: Face<MachineNode[]>;
  rooms: Face<RoomStats>;
  online: Face<OnlineStats>;
  ingress: Face<IngressStats>;
  delivery: Face<DeliveryStats>;
  platformApi: Face<PlatformStats>;
  window: Face<OpsWindow>;
}

const loadingFaces: Faces = {
  alerts: { kind: 'loading' },
  machines: { kind: 'loading' },
  rooms: { kind: 'loading' },
  online: { kind: 'loading' },
  ingress: { kind: 'loading' },
  delivery: { kind: 'loading' },
  platformApi: { kind: 'loading' },
  window: { kind: 'loading' },
};

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

// Each face owns its JSON shape; a 200 that doesn't carry the face's own discriminators
// (an older control plane, a proxy interposing) is that card's error, never a page crash.
const faceGuards: Record<keyof Faces, (value: unknown) => boolean> = {
  alerts: (value) => Array.isArray(value),
  machines: (value) => Array.isArray(value),
  rooms: (value) => isRecord(value) && typeof value.total === 'number' && isRecord(value.byState),
  online: (value) => isRecord(value) && typeof value.playersTotal === 'number',
  ingress: (value) => isRecord(value) && typeof value.accepted === 'number',
  delivery: (value) => isRecord(value) && typeof value.ingested === 'number',
  platformApi: (value) => isRecord(value) && Array.isArray(value.endpoints),
  window: (value) => isRecord(value) && Array.isArray(value.families),
};

export default function OverviewPage() {
  const { token } = useAuth();
  const { t } = useI18n();
  const [faces, setFaces] = useState<Faces>(loadingFaces);

  const load = useCallback(async () => {
    if (token === null) return;
    setFaces(loadingFaces);
    const settle = async (path: string, key: keyof Faces, guard: (value: unknown) => boolean) => {
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
    };
    await Promise.allSettled([
      settle('/ops/alerts', 'alerts', faceGuards.alerts),
      settle('/telemetry/machine?windowMinutes=15', 'machines', faceGuards.machines),
      settle('/rooms/stats', 'rooms', faceGuards.rooms),
      settle('/online/stats', 'online', faceGuards.online),
      settle('/ingress/stats', 'ingress', faceGuards.ingress),
      settle('/delivery/stats', 'delivery', faceGuards.delivery),
      settle('/platform-api/stats', 'platformApi', faceGuards.platformApi),
      settle('/ops/window?windowMinutes=60', 'window', faceGuards.window),
    ]);
  }, [token]);

  useEffect(() => { void load(); }, [load]);

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
        <Button icon={<ReloadOutlined />} onClick={() => void load()}>{t('Refresh', '刷新')}</Button>
        <Typography.Text type="secondary">
          {t('Each card answers from its own statistics face.', '每张卡片各自应答于自己的统计面。')}
        </Typography.Text
        >
      </Space>
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          {faceCard(t('Alerts', '告警'), faces.alerts as Face<never>, (value) => {
            const active = value as OpsAlertItem[];
            if (active.length === 0) {
              return <Typography.Text type="secondary">{t('No threshold breaches.', '无阈值破口。')}</Typography.Text>;
            }
            return (
              <Table
                size="small"
                rowKey={(alert) => `${alert.kind}:${alert.subject}`}
                pagination={false}
                dataSource={active}
                columns={[
                  { title: t('Kind', '类别'), dataIndex: 'kind', render: (kind: string) => <Tag color="red">{kind}</Tag> },
                  { title: t('Subject', '主体'), dataIndex: 'subject' },
                  { title: t('Observed', '读数'), dataIndex: 'observed' },
                  { title: t('Threshold', '阈值'), dataIndex: 'threshold' },
                  { title: t('Detail', '详情'), dataIndex: 'detail' },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Machines (15 min)', '机器（15 分钟）'), faces.machines as Face<never>, (value) => {
            const nodes = value as MachineNode[];
            return (
              <Table
                size="small"
                rowKey="nodeId"
                pagination={false}
                dataSource={nodes}
                columns={[
                  { title: t('Node', '节点'), dataIndex: 'nodeId' },
                  { title: 'CPU', render: (_, node) => node.cpuPercent === null ? '—' : `${node.cpuPercent.toFixed(1)}%` },
                  { title: t('Memory', '内存'), render: (_, node) => node.memory === null ? '—' : `${node.memory.usedPercent.toFixed(1)}%` },
                  { title: t('Samples', '样本'), dataIndex: 'samples' },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Rooms (live)', '房间（活值）'), faces.rooms as Face<never>, (value) => {
            const rooms = value as RoomStats;
            return (
              <>
                <Space size={24}>
                  <Statistic title={t('Total', '总数')} value={rooms.total} />
                  <Statistic title={t('Active', '活跃')} value={rooms.active} />
                </Space>
                <div style={{ marginTop: 12 }}>
                  {Object.entries(rooms.byState).map(([state, count]) => (
                    <Tag key={state}>{state}: {count}</Tag>
                  ))}
                </div>
                {rooms.byTenant.length > 0 && (
                  <div style={{ marginTop: 8 }}>
                    {rooms.byTenant.map((entry) => (
                      <Tag key={entry.tenant} color="blue">{entry.tenant}: {entry.rooms}</Tag>
                    ))}
                  </div>
                )}
              </>
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Online', '在线'), faces.online as Face<never>, (value) => {
            const online = value as OnlineStats;
            return (
              <>
                <Space size={24}>
                  <Statistic title={t('Players', '玩家')} value={online.playersTotal} />
                  <Statistic title={t('Streamers', '主播')} value={online.streamersTotal} />
                </Space>
                {online.playersByTenantGame.length > 0 && (
                  <div style={{ marginTop: 12 }}>
                    {online.playersByTenantGame.map((entry) => (
                      <Tag key={`${entry.tenant}/${entry.game}`} color="green">
                        {entry.tenant}/{entry.game}: {entry.count}
                      </Tag>
                    ))}
                  </div>
                )}
              </>
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Raw ingress', '原始进站'), faces.ingress as Face<never>, (value) => {
            const ingress = value as IngressStats;
            return (
              <Descriptions size="small" column={2}>
                <Descriptions.Item label={t('Accepted', '已接受')}>{ingress.accepted}</Descriptions.Item>
                <Descriptions.Item label={t('Duplicates', '重复')}>{ingress.duplicates}</Descriptions.Item>
                <Descriptions.Item label={t('Unverified', '未验证')}>{ingress.unverified}</Descriptions.Item>
                <Descriptions.Item label={t('Unmapped', '未映射')}>{ingress.unmapped}</Descriptions.Item>
                <Descriptions.Item label={t('Rate limited', '限流')}>{ingress.rateLimited}</Descriptions.Item>
                <Descriptions.Item label={t('Too large', '过大')}>{ingress.tooLarge}</Descriptions.Item>
                <Descriptions.Item label={t('Timed out', '超时')}>{ingress.timedOut}</Descriptions.Item>
              </Descriptions>
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Event delivery', '事件投递'), faces.delivery as Face<never>, (value) => {
            const delivery = value as DeliveryStats;
            return (
              <Descriptions size="small" column={2}>
                <Descriptions.Item label="Ingested">{delivery.ingested}</Descriptions.Item>
                <Descriptions.Item label="Applied">{delivery.applied}</Descriptions.Item>
                <Descriptions.Item label="Dropped">{delivery.dropped}</Descriptions.Item>
                <Descriptions.Item label="Delivered">{delivery.delivered}</Descriptions.Item>
                <Descriptions.Item label={t('Effects applied', '效果已落')}>{delivery.effectsApplied}</Descriptions.Item>
                <Descriptions.Item label={t('Effects pending', '效果待派')}>{delivery.effectsPending}</Descriptions.Item>
                <Descriptions.Item label={t('Effects dispatching', '效果派发中')}>{delivery.effectsDispatching}</Descriptions.Item>
                <Descriptions.Item label={t('Effects done', '效果已结')}>{delivery.effectsDone}</Descriptions.Item>
                <Descriptions.Item label={t('Effects failed', '效果已败')}>{delivery.effectsFailed}</Descriptions.Item>
              </Descriptions>
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Platform API', '平台出站'), faces.platformApi as Face<never>, (value) => {
            const platform = value as PlatformStats;
            if (platform.endpoints.length === 0) {
              return <Typography.Text type="secondary">{t('No platform calls observed.', '尚无平台调用。')}</Typography.Text>;
            }
            return (
              <Table
                size="small"
                rowKey="endpoint"
                pagination={false}
                dataSource={platform.endpoints}
                columns={[
                  { title: t('Endpoint', '端点'), dataIndex: 'endpoint' },
                  { title: t('Calls', '调用'), dataIndex: 'calls' },
                  { title: t('Failures', '失败'), dataIndex: 'failures' },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24}>
          {faceCard(t('Counter increments (60 min)', '计数器增量（60 分钟）'), faces.window as Face<never>, (value) => {
            const window = value as OpsWindow;
            if (window.families.length === 0) {
              return <Typography.Text type="secondary">{t('No counter steps in the window.', '窗口内无计数步。')}</Typography.Text>;
            }
            return (
              <Table
                size="small"
                rowKey="family"
                pagination={false}
                dataSource={window.families}
                columns={[
                  { title: t('Family', '指标族'), dataIndex: 'family' },
                  { title: t('Delta', '增量'), dataIndex: 'delta' },
                ]}
              />
            );
          })}
        </Col>
      </Row>
    </Space>
  );
}
