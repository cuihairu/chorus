import {
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Form,
  Input,
  InputNumber,
  List,
  Modal,
  Popconfirm,
  Row,
  Space,
  Spin,
  Table,
  Tag,
  Typography,
} from 'antd';
import { DeleteOutlined, EditOutlined, PlayCircleOutlined, PlusOutlined, ReloadOutlined, StopOutlined, ThunderboltOutlined } from '@ant-design/icons';
import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { AdminError, deleteAdminJson, getAdminJson, postAdminJson } from '../api/client';
import { useAuth } from '../auth';
import { useI18n } from '../i18n';

// The bots page (BOTS-2/BOTS-3): the fleet's own faces — scenarios the operator
// keeps, runs the fleet service is playing — the pressure runs the load-tool driver
// plays against the ingress face (with the one-click dotnet-trace window and its
// hottest frames read straight off the speedscope), plus the same ingress and
// delivery statistics faces the overview reads (the panel never builds a second
// statistics plane). Every face answers independently, so one face's failure is
// that card's error, never a blank screen. The commands (save/delete a scenario,
// start/stop a run, start/stop a pressure run) are the admin group's bots and
// pressure routes; a start only speaks to the fleet service, which parses the
// script and validates the request up front — a bad one answers at the request,
// not mid-run.

interface ScenarioRow {
  id: string;
  name: string;
  toml: string;
  updatedBy: string;
  updatedAtMs: number;
}

interface RunRow {
  id: string;
  scenarioId: string;
  state: string;
  startedAt: string | null;
  finishedAt: string | null;
  error: string | null;
  deviations: string[];
  snapshot: {
    scheduled: number;
    posted: number;
    poster: { accepted: number; duplicated: number; unauthorized: number; rateLimited: number; other: number };
  } | null;
}

interface PressureRow {
  id: string;
  state: string;
  sample: {
    sent: number;
    accepted: number;
    duplicated: number;
    rateLimited: number;
    laneFull: number;
    unauthorized: number;
    other: number;
    targetPerSecond: number;
    elapsedSeconds: number;
    achievedPerSecond: number;
  } | null;
  startedAt: string | null;
  finishedAt: string | null;
  error: string | null;
  tracePath: string | null;
  speedscopePath: string | null;
  hotFrames: { frame: string; selfSharePercent: number }[];
}

interface IngressStats {
  accepted: number;
  duplicates: number;
  unverified: number;
  unmapped: number;
  rateLimited: number;
  tooLarge: number;
  timedOut: number;
}

interface DeliveryStats {
  ingested: number;
  applied: number;
  merged: number;
  dropped: number;
  delivered: number;
  effectsPending: number;
  effectsFailed: number;
}

interface OpsAlertItem {
  kind: string;
  subject: string;
  observed: number;
  threshold: number;
  detail: string;
}

const terminalStates = ['Completed', 'Reconciled', 'Deviated', 'Failed', 'Stopped'];
const pressureTerminalStates = ['Completed', 'Stopped', 'Failed'];

type Face<T> = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'data'; value: T };

interface Faces {
  scenarios: Face<ScenarioRow[]>;
  runs: Face<RunRow[]>;
  pressure: Face<PressureRow[]>;
  ingress: Face<IngressStats>;
  delivery: Face<DeliveryStats>;
  alerts: Face<OpsAlertItem[]>;
}

const loadingFaces: Faces = {
  scenarios: { kind: 'loading' }, runs: { kind: 'loading' }, pressure: { kind: 'loading' },
  ingress: { kind: 'loading' }, delivery: { kind: 'loading' }, alerts: { kind: 'loading' },
};

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

// Each face owns its JSON shape; a 200 that doesn't carry the face's own discriminators
// is that card's error, never a page crash.
const faceGuards: Record<keyof Faces, (value: unknown) => boolean> = {
  scenarios: (value) => Array.isArray(value) && value.every((row) => isRecord(row) && typeof (row as { id?: unknown }).id === 'string' && typeof (row as { toml?: unknown }).toml === 'string'),
  runs: (value) => Array.isArray(value) && value.every((row) => isRecord(row) && typeof (row as { id?: unknown }).id === 'string' && typeof (row as { state?: unknown }).state === 'string'),
  pressure: (value) => Array.isArray(value) && value.every((row) => isRecord(row) && typeof (row as { id?: unknown }).id === 'string' && typeof (row as { state?: unknown }).state === 'string'),
  ingress: (value) => isRecord(value) && typeof value.accepted === 'number',
  delivery: (value) => isRecord(value) && typeof value.ingested === 'number',
  alerts: (value) => Array.isArray(value),
};

interface PressureForm {
  baseUrl: string;
  platform: string;
  eventsPerSecond: number;
  sessions: number;
  producerConnections: number;
  durationSeconds: number;
  signSecret?: string;
  profileSeconds?: number;
  profileProcessId?: number;
  rootCaPemPath?: string;
}

interface StartForm {
  streamerEndpoint: string;
  ingressBaseUrl: string;
  appId: string;
  gameId: string;
  simulatorSigningKeyHex: string;
  clusterId: string;
  clientCertificatePemPath: string;
  clientKeyPemPath: string;
  rootCaPemPath: string;
  nodeAuthorityPemPath: string;
  serverRole: string;
  viewerCount: number;
  reconcileRoomId?: string;
  reconcileFaction?: string;
  reconcileConnectionString?: string;
}

export default function BotsPage() {
  const { token } = useAuth();
  const { t } = useI18n();
  const [faces, setFaces] = useState<Faces>(loadingFaces);
  const [editor, setEditor] = useState<{ open: boolean; scenario: ScenarioRow | null }>({ open: false, scenario: null });
  const [startTarget, setStartTarget] = useState<ScenarioRow | null>(null);
  const [pressureOpen, setPressureOpen] = useState(false);
  const [commandError, setCommandError] = useState<string | null>(null);

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
      settle('/api/bots/scenarios', 'scenarios', faceGuards.scenarios),
      settle('/api/bots/runs', 'runs', faceGuards.runs),
      settle('/api/pressure/runs', 'pressure', faceGuards.pressure),
      settle('/api/ingress/stats', 'ingress', faceGuards.ingress),
      settle('/api/delivery/stats', 'delivery', faceGuards.delivery),
      settle('/api/ops/alerts', 'alerts', faceGuards.alerts),
    ]);
  }, [token]);

  useEffect(() => { void load(); }, [load]);

  const run = async (command: () => Promise<unknown>) => {
    setCommandError(null);
    try {
      await command();
      setEditor({ open: false, scenario: null });
      setStartTarget(null);
      setPressureOpen(false);
      await load();
      return true;
    } catch (error) {
      setCommandError(error instanceof AdminError
        ? `${error.status}: ${error.message}`
        : error instanceof Error ? error.message : 'error');
      return false;
    }
  };

  const faceCard = (title: string, state: Face<never>, render: (value: never) => ReactNode) => (
    <Card size="small" title={title}>
      {state.kind === 'loading' ? <Spin /> : state.kind === 'error'
        ? <Alert type="error" showIcon message={t('The face did not answer usable data', '该统计面未给出可用数据')} description={state.message} />
        : render(state.value)}
    </Card>
  );

  const scenarioEditor = (
    <Modal
      title={editor.scenario ? t('Edit scenario', '编辑场景') : t('New scenario', '新场景')}
      open={editor.open}
      onCancel={() => setEditor({ open: false, scenario: null })}
      footer={null}
      destroyOnHidden
    >
      <Form
        layout="vertical"
        initialValues={editor.scenario
          ? { id: editor.scenario.id, name: editor.scenario.name, toml: editor.scenario.toml }
          : undefined}
        onFinish={(values: { id: string; name?: string; toml: string }) =>
          void run(async () => postAdminJson(token ?? '', '/api/bots/scenarios', {
            id: values.id, name: values.name ?? '', toml: values.toml,
            updatedBy: '', updatedAtMs: 0,
          }))}
      >
        <Form.Item name="id" label={t('Id', '标识')} rules={[{ required: true }]}>
          <Input disabled={editor.scenario !== null} />
        </Form.Item>
        <Form.Item name="name" label={t('Name', '名称')}>
          <Input />
        </Form.Item>
        <Form.Item name="toml" label={t('Script (TOML)', '剧本（TOML）')} rules={[{ required: true }]}>
          <Input.TextArea rows={12} style={{ fontFamily: 'monospace' }} />
        </Form.Item>
        {commandError !== null && <Alert type="error" showIcon message={commandError} />}
        <Button type="primary" htmlType="submit">{t('Save', '保存')}</Button>
      </Form>
    </Modal>
  );

  const startDialog = (
    <Modal
      title={t('Start run', '启动运行')}
      open={startTarget !== null}
      onCancel={() => setStartTarget(null)}
      footer={null}
      destroyOnHidden
    >
      {startTarget !== null && (
        <Form
          layout="vertical"
          initialValues={{ serverRole: 'streamer-gateway', viewerCount: 8 } as Partial<StartForm>}
          onFinish={(values: StartForm) =>
            void run(async () => postAdminJson(token ?? '', `/api/bots/scenarios/${startTarget.id}/start`, {
              streamerEndpoint: values.streamerEndpoint,
              ingressBaseUrl: values.ingressBaseUrl,
              appId: values.appId,
              gameId: values.gameId,
              simulatorSigningKeyHex: values.simulatorSigningKeyHex,
              clusterId: values.clusterId,
              clientCertificatePemPath: values.clientCertificatePemPath,
              clientKeyPemPath: values.clientKeyPemPath,
              rootCaPemPath: values.rootCaPemPath,
              nodeAuthorityPemPath: values.nodeAuthorityPemPath,
              serverRole: values.serverRole || 'streamer-gateway',
              viewerCount: values.viewerCount || 8,
              reconcileRoomId: values.reconcileRoomId || null,
              reconcileFaction: values.reconcileFaction || null,
              reconcileConnectionString: values.reconcileConnectionString || null,
            }))}
        >
          <Form.Item name="streamerEndpoint" label={t('Streamer endpoint', '主播网关端点')} rules={[{ required: true }]}>
            <Input placeholder="https://streamer-gateway:443" />
          </Form.Item>
          <Form.Item name="ingressBaseUrl" label={t('Ingress base URL', '入口基址')} rules={[{ required: true }]}>
            <Input placeholder="https://platform-gateway:443" />
          </Form.Item>
          <Form.Item name="appId" label={t('App id', '应用')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="gameId" label={t('Game id', '游戏')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="simulatorSigningKeyHex" label={t('Signing key (hex)', '签名密钥（hex）')} rules={[{ required: true }]}>
            <Input.Password />
          </Form.Item>
          <Form.Item name="clusterId" label={t('Cluster id', '集群')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="clientCertificatePemPath" label={t('Client certificate (PEM path)', '客户端证书（PEM 路径）')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="clientKeyPemPath" label={t('Client key (PEM path)', '客户端私钥（PEM 路径）')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="rootCaPemPath" label={t('Root CA (PEM path)', '根证书（PEM 路径）')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="nodeAuthorityPemPath" label={t('Node authority (PEM path)', '节点签发（PEM 路径）')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="serverRole" label={t('Server role', '服务角色')}>
            <Input />
          </Form.Item>
          <Form.Item name="viewerCount" label={t('Viewer count', '观众数')}>
            <InputNumber min={1} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="reconcileRoomId" label={t('Reconcile room', '对账房间')}>
            <Input />
          </Form.Item>
          <Form.Item name="reconcileFaction" label={t('Reconcile faction', '对账阵营')}>
            <Input />
          </Form.Item>
          <Form.Item name="reconcileConnectionString" label={t('Data-plane connection', '数据面连接串')}>
            <Input />
          </Form.Item>
          {commandError !== null && <Alert type="error" showIcon message={commandError} />}
          <Button type="primary" htmlType="submit">{t('Start', '启动')}</Button>
        </Form>
      )}
    </Modal>
  );

  const pressureDialog = (
    <Modal
      title={t('New pressure run', '新压力运行')}
      open={pressureOpen}
      onCancel={() => setPressureOpen(false)}
      footer={null}
      destroyOnHidden
    >
      <Form
        layout="vertical"
        initialValues={{ platform: 'douyin', eventsPerSecond: 50, sessions: 4, producerConnections: 4, durationSeconds: 30, profileSeconds: 30 } as Partial<PressureForm>}
        onFinish={(values: PressureForm) =>
          void run(async () => postAdminJson(token ?? '', '/api/pressure/runs', {
            baseUrl: values.baseUrl,
            platform: values.platform,
            eventsPerSecond: values.eventsPerSecond,
            sessions: values.sessions,
            producerConnections: values.producerConnections,
            durationSeconds: values.durationSeconds,
            signSecret: values.signSecret || null,
            profileSeconds: values.profileSeconds || 0,
            profileProcessId: values.profileProcessId || 0,
            rootCaPemPath: values.rootCaPemPath || '',
          }))}
      >
        <Form.Item name="baseUrl" label={t('Ingress base URL', '入口基址')} rules={[{ required: true }]}>
          <Input placeholder="https://platform-gateway:443" />
        </Form.Item>
        <Form.Item name="platform" label={t('Platform', '平台')} rules={[{ required: true }]}>
          <Input />
        </Form.Item>
        <Form.Item name="eventsPerSecond" label={t('Events / second', '事件/秒')} rules={[{ required: true }]}>
          <InputNumber min={1} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item name="sessions" label={t('Sessions', '会话数')} rules={[{ required: true }]}>
          <InputNumber min={1} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item name="producerConnections" label={t('Producers', '生产者数')} rules={[{ required: true }]}>
          <InputNumber min={1} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item name="durationSeconds" label={t('Duration (s)', '时长（秒）')} rules={[{ required: true }]}>
          <InputNumber min={1} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item name="signSecret" label={t('Signing secret', '签名密钥')}>
          <Input.Password />
        </Form.Item>
        <Form.Item name="profileSeconds" label={t('Profile window (s, 0 = off)', '采样窗口（秒，0=关）')}>
          <InputNumber min={0} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item name="profileProcessId" label={t('Profile process id (0 = this host)', '采样进程号（0=本机）')}>
          <InputNumber min={0} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item name="rootCaPemPath" label={t('Root CA (PEM path, optional)', '根证书（PEM 路径，可选）')}>
          <Input />
        </Form.Item>
        {commandError !== null && <Alert type="error" showIcon message={commandError} />}
        <Button type="primary" htmlType="submit">{t('Start', '启动')}</Button>
      </Form>
    </Modal>
  );

  return (
    <Space direction="vertical" style={{ width: '100%' }} size={16}>
      <Space>
        <Button icon={<ReloadOutlined />} onClick={() => void load()}>{t('Refresh', '刷新')}</Button>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => { setCommandError(null); setEditor({ open: true, scenario: null }); }}>
          {t('New scenario', '新场景')}
        </Button>
        <Button type="primary" icon={<ThunderboltOutlined />} onClick={() => { setCommandError(null); setPressureOpen(true); }}>
          {t('New pressure run', '新压力运行')}
        </Button>
        <Typography.Text type="secondary">
          {t('Runs ride the real chain — mount, ingress, ticks — and reconcile against the data plane.', '运行走真实链路——挂载、入口、tick——并与数据面对账。')}
        </Typography.Text>
      </Space>
      <Row gutter={[16, 16]}>
        <Col xs={24} xl={12}>
          {faceCard(t('Scenarios', '场景'), faces.scenarios as Face<never>, (value) => {
            const scenarios = value as ScenarioRow[];
            return (
              <Table
                size="small"
                rowKey="id"
                pagination={false}
                dataSource={scenarios}
                columns={[
                  { title: t('Id', '标识'), dataIndex: 'id' },
                  { title: t('Name', '名称'), dataIndex: 'name' },
                  { title: t('Updated', '更新于'), render: (_, scenario) => new Date(scenario.updatedAtMs).toLocaleString() },
                  { title: t('By', '操作人'), dataIndex: 'updatedBy' },
                  {
                    title: t('Actions', '操作'),
                    render: (_, scenario) => (
                      <Space size={4}>
                        <Button size="small" icon={<PlayCircleOutlined />} onClick={() => { setCommandError(null); setStartTarget(scenario); }}>
                          {t('Start', '启动')}
                        </Button>
                        <Button size="small" icon={<EditOutlined />} onClick={() => { setCommandError(null); setEditor({ open: true, scenario }); }} />
                        <Popconfirm
                          title={t('Delete scenario', '删除场景')}
                          onConfirm={() => void run(async () => deleteAdminJson(token ?? '', `/api/bots/scenarios/${scenario.id}`))}
                        >
                          <Button size="small" danger icon={<DeleteOutlined />} />
                        </Popconfirm>
                      </Space>
                    ),
                  },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Runs', '运行'), faces.runs as Face<never>, (value) => {
            const runs = value as RunRow[];
            return (
              <Table
                size="small"
                rowKey="id"
                pagination={false}
                dataSource={runs}
                expandable={{
                  expandedRowRender: (entry) => (
                    entry.error !== null || entry.deviations.length > 0 ? (
                      <Space direction="vertical">
                        {entry.error !== null && <Typography.Text type="danger">{entry.error}</Typography.Text>}
                        <List
                          size="small"
                          dataSource={entry.deviations}
                          locale={{ emptyText: t('No deviations — the run held to its word.', '无偏差——运行守住了它的诺言。') }}
                          renderItem={(deviation) => <List.Item><Typography.Text type="danger">{deviation}</Typography.Text></List.Item>}
                        />
                      </Space>
                    ) : <Typography.Text type="secondary">{t('No deviations — the run held to its word.', '无偏差——运行守住了它的诺言。')}</Typography.Text>
                  ),
                }}
                columns={[
                  { title: t('Run', '运行'), dataIndex: 'id' },
                  { title: t('Scenario', '场景'), dataIndex: 'scenarioId' },
                  {
                    title: t('State', '状态'),
                    dataIndex: 'state',
                    render: (state: string) => (
                      <Tag color={state === 'Reconciled' || state === 'Completed' ? 'green'
                        : state === 'Deviated' || state === 'Failed' ? 'red' : 'blue'}>{state}</Tag>
                    ),
                  },
                  { title: t('Posted', '已发送'), render: (_, entry) => entry.snapshot?.posted ?? '—' },
                  {
                    title: t('Face', '入口'),
                    render: (_, entry) => entry.snapshot === null ? '—'
                      : `${entry.snapshot.poster.accepted}/${entry.snapshot.poster.duplicated}/${entry.snapshot.poster.unauthorized}/${entry.snapshot.poster.rateLimited}/${entry.snapshot.poster.other}`,
                  },
                  { title: t('Started', '开始于'), render: (_, entry) => entry.startedAt === null ? '—' : new Date(entry.startedAt).toLocaleTimeString() },
                  {
                    title: t('Actions', '操作'),
                    render: (_, entry) => terminalStates.includes(entry.state) ? '—' : (
                      <Popconfirm title={t('Stop run', '停止运行')} onConfirm={() => void run(async () => postAdminJson(token ?? '', `/api/bots/runs/${entry.id}/stop`))}>
                        <Button size="small" danger icon={<StopOutlined />}>{t('Stop', '停止')}</Button>
                      </Popconfirm>
                    ),
                  },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24}>
          {faceCard(t('Pressure runs', '压力运行'), faces.pressure as Face<never>, (value) => {
            const pressure = value as PressureRow[];
            return (
              <Table
                size="small"
                rowKey="id"
                pagination={false}
                dataSource={pressure}
                expandable={{
                  expandedRowRender: (entry) => (
                    <Space direction="vertical" style={{ width: '100%' }}>
                      {entry.error !== null && <Typography.Text type="danger">{entry.error}</Typography.Text>}
                      {entry.speedscopePath !== null ? (
                        <Typography.Text code copyable={{ text: entry.speedscopePath }}>
                          {entry.speedscopePath}
                        </Typography.Text>
                      ) : entry.tracePath !== null ? (
                        <Typography.Text type="secondary">
                          {t('No speedscope landed — the trace window or its conversion was missed.', '未生成 speedscope——采样窗口或其转换未落地。')}
                        </Typography.Text>
                      ) : null}
                      {entry.hotFrames.length > 0 ? (
                        <List
                          size="small"
                          dataSource={entry.hotFrames}
                          renderItem={(frame) => (
                            <List.Item>
                              <Space>
                                <Tag color="red">{frame.selfSharePercent}%</Tag>
                                <Typography.Text code>{frame.frame}</Typography.Text>
                              </Space>
                            </List.Item>
                          )}
                        />
                      ) : null}
                    </Space>
                  ),
                }}
                columns={[
                  { title: t('Run', '运行'), dataIndex: 'id' },
                  {
                    title: t('State', '状态'),
                    dataIndex: 'state',
                    render: (state: string) => (
                      <Tag color={state === 'Completed' ? 'green'
                        : state === 'Failed' ? 'red' : state === 'Running' ? 'processing' : 'blue'}>{state}</Tag>
                    ),
                  },
                  { title: t('Sent', '已发送'), render: (_, entry) => entry.sample?.sent ?? '—' },
                  {
                    title: t('Face', '入口'),
                    render: (_, entry) => entry.sample === null ? '—'
                      : `${entry.sample.accepted}/${entry.sample.duplicated}/${entry.sample.unauthorized}/${entry.sample.rateLimited}/${entry.sample.other}`,
                  },
                  {
                    title: t('Rate', '速率'),
                    render: (_, entry) => entry.sample === null ? '—'
                      : `${entry.sample.achievedPerSecond} / ${entry.sample.targetPerSecond}`,
                  },
                  { title: t('Hot frames', '热点栈'), render: (_, entry) => entry.hotFrames.length === 0 ? '—' : entry.hotFrames[0].frame },
                  { title: t('Started', '开始于'), render: (_, entry) => entry.startedAt === null ? '—' : new Date(entry.startedAt).toLocaleTimeString() },
                  {
                    title: t('Actions', '操作'),
                    render: (_, entry) => pressureTerminalStates.includes(entry.state) ? '—' : (
                      <Popconfirm title={t('Stop run', '停止运行')} onConfirm={() => void run(async () => postAdminJson(token ?? '', `/api/pressure/runs/${entry.id}/stop`))}>
                        <Button size="small" danger icon={<StopOutlined />}>{t('Stop', '停止')}</Button>
                      </Popconfirm>
                    ),
                  },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24} xl={8}>
          {faceCard(t('Ingress face', '入口面'), faces.ingress as Face<never>, (value) => {
            const ingress = value as IngressStats;
            return (
              <Descriptions size="small" column={1} bordered>
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
        <Col xs={24} xl={8}>
          {faceCard(t('Delivery face', '投递面'), faces.delivery as Face<never>, (value) => {
            const delivery = value as DeliveryStats;
            return (
              <Descriptions size="small" column={1} bordered>
                <Descriptions.Item label="Ingested">{delivery.ingested}</Descriptions.Item>
                <Descriptions.Item label="Applied">{delivery.applied}</Descriptions.Item>
                <Descriptions.Item label="Merged">{delivery.merged}</Descriptions.Item>
                <Descriptions.Item label="Dropped">{delivery.dropped}</Descriptions.Item>
                <Descriptions.Item label="Delivered">{delivery.delivered}</Descriptions.Item>
                <Descriptions.Item label={t('Effects pending', '效果待发')}>{delivery.effectsPending}</Descriptions.Item>
                <Descriptions.Item label={t('Effects failed', '效果失败')}>{delivery.effectsFailed}</Descriptions.Item>
              </Descriptions>
            );
          })}
        </Col>
        <Col xs={24} xl={8}>
          {faceCard(t('Alerts', '告警'), faces.alerts as Face<never>, (value) => {
            const alerts = value as OpsAlertItem[];
            return alerts.length === 0 ? (
              <Typography.Text type="secondary">{t('No active alerts.', '无活动告警。')}</Typography.Text>
            ) : (
              <List
                size="small"
                dataSource={alerts}
                renderItem={(alert) => (
                  <List.Item>
                    <Space direction="vertical" style={{ width: '100%' }}>
                      <Space>
                        <Tag color="orange">{alert.kind}</Tag>
                        <Typography.Text strong>{alert.subject}</Typography.Text>
                      </Space>
                      <Typography.Text type="secondary">
                        {`${alert.observed} / ${alert.threshold}`} — {alert.detail}
                      </Typography.Text>
                    </Space>
                  </List.Item>
                )}
              />
            );
          })}
        </Col>
      </Row>
      {scenarioEditor}
      {startDialog}
      {pressureDialog}
    </Space>
  );
}
