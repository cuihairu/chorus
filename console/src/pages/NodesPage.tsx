import {
  Alert,
  Button,
  Card,
  Col,
  Row,
  Select,
  Space,
  Spin,
  Table,
  Typography,
} from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { AdminError, getAdminJson } from '../api/client';
import { useAuth } from '../auth';
import { useI18n } from '../i18n';

// The nodes and processes page (FIX-WEB-06): the operator names a node (or none, for
// the whole fleet) and the OBS-07 telemetry faces answer — the machine face with each
// node's latest sample in the window and the process face with the supervised processes
// underneath. Each face answers independently, so one face's failure is that card's
// error, never a blank screen; the window is the faces' own default horizon and the
// numbers are shown as they are.

interface MachineMemory {
  totalBytes: number;
  usedBytes: number;
  usedPercent: number;
}

interface MachineLoad {
  l1: number;
  l5: number;
  l15: number;
}

interface MachineDisk {
  mount: string;
  totalBytes: number;
  usedBytes: number;
  usedPercent: number;
}

interface MachineNode {
  nodeId: string;
  sampledAtUtc: string;
  cpuPercent: number | null;
  memory: MachineMemory | null;
  load: MachineLoad | null;
  disk: MachineDisk[];
  samples: number;
}

interface ProcessEntry {
  nodeId: string;
  processInstanceId: string;
  role: string;
  sampledAtUtc: string;
  cpuPercent: number;
  memoryUsedBytes: number;
  uptimeSeconds: number;
}

interface ProcessNode {
  nodeId: string;
  processesRunning: number;
  processes: ProcessEntry[];
}

type Face<T> = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'data'; value: T };

function megabytes(bytes: number): string {
  if (bytes >= 1_073_741_824) return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
  return `${Math.round(bytes / 1_048_576)} MB`;
}

function uptime(seconds: number): string {
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}

export default function NodesPage() {
  const { token } = useAuth();
  const { t } = useI18n();
  const [nodeId, setNodeId] = useState<string>('');
  const [machines, setMachines] = useState<Face<MachineNode[]>>({ kind: 'loading' });
  const [processes, setProcesses] = useState<Face<ProcessNode[]>>({ kind: 'loading' });

  const load = useCallback(async (selected: string) => {
    if (token === null) return;
    setMachines({ kind: 'loading' });
    setProcesses({ kind: 'loading' });
    const suffix = selected === '' ? '' : `&nodeId=${encodeURIComponent(selected)}`;
    const machineFace = async () => {
      try {
        setMachines({ kind: 'data', value: await getAdminJson<MachineNode[]>(token, `/api/telemetry/machine?windowMinutes=60${suffix}`) });
      } catch (error) {
        const message = error instanceof AdminError
          ? `${error.status}`
          : error instanceof Error ? error.message : 'error';
        setMachines({ kind: 'error', message });
      }
    };
    const processFace = async () => {
      try {
        setProcesses({ kind: 'data', value: await getAdminJson<ProcessNode[]>(token, `/api/telemetry/process?windowMinutes=60${suffix}`) });
      } catch (error) {
        const message = error instanceof AdminError
          ? `${error.status}`
          : error instanceof Error ? error.message : 'error';
        setProcesses({ kind: 'error', message });
      }
    };
    await Promise.allSettled([machineFace(), processFace()]);
  }, [token]);

  useEffect(() => { void load(nodeId); }, [load, nodeId]);

  const faceCard = (title: string, state: Face<never>, render: (value: never) => ReactNode) => (
    <Card size="small" title={title}>
      {state.kind === 'loading' ? <Spin /> : state.kind === 'error'
        ? <Alert type="error" showIcon message={t('The face did not answer usable data', '该统计面未给出可用数据')} description={state.message} />
        : render(state.value)}
    </Card>
  );

  return (
    <Space direction="vertical" style={{ width: '100%' }} size={16}>
      <Space wrap>
        <Select
          allowClear
          placeholder={t('All nodes', '全部节点')}
          style={{ minWidth: 220 }}
          value={nodeId === '' ? undefined : nodeId}
          onChange={(value) => setNodeId(value ?? '')}
          options={machines.kind === 'data'
            ? machines.value.map((node) => ({ value: node.nodeId, label: node.nodeId }))
            : []}
        />
        <Button icon={<ReloadOutlined />} onClick={() => void load(nodeId)}>{t('Refresh', '刷新')}</Button>
        <Typography.Text type="secondary">
          {t('The window is the telemetry face\'s own 60 minutes.', '窗口是遥测面自己的 60 分钟。')}
        </Typography.Text>
      </Space>
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          {faceCard(t('Machines', '机器'), machines as Face<never>, (value) => {
            const nodes = value as MachineNode[];
            return (
              <Table
                size="small"
                rowKey="nodeId"
                pagination={false}
                dataSource={nodes}
                expandable={{
                  rowExpandable: (node) => node.disk.length > 0,
                  expandedRowRender: (node) => (
                    <Table
                      size="small"
                      rowKey="mount"
                      pagination={false}
                      dataSource={node.disk}
                      columns={[
                        { title: t('Mount', '挂载点'), dataIndex: 'mount' },
                        { title: t('Used', '已用'), render: (_, disk) => megabytes(disk.usedBytes) },
                        { title: t('Total', '总量'), render: (_, disk) => megabytes(disk.totalBytes) },
                        { title: t('Percent', '占比'), render: (_, disk) => `${disk.usedPercent.toFixed(1)}%` },
                      ]}
                    />
                  ),
                }}
                columns={[
                  { title: t('Node', '节点'), dataIndex: 'nodeId' },
                  { title: 'CPU', render: (_, node) => node.cpuPercent === null ? '—' : `${node.cpuPercent.toFixed(1)}%` },
                  { title: t('Memory', '内存'), render: (_, node) => node.memory === null ? '—' : `${node.memory.usedPercent.toFixed(1)}%` },
                  { title: t('Load 1m/5m/15m', '负载 1m/5m/15m'), render: (_, node) => node.load === null ? '—' : `${node.load.l1.toFixed(2)} / ${node.load.l5.toFixed(2)} / ${node.load.l15.toFixed(2)}` },
                  { title: t('Last sample', '末次样本'), render: (_, node) => new Date(node.sampledAtUtc).toLocaleString() },
                  { title: t('Samples', '样本'), dataIndex: 'samples' },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24}>
          {faceCard(t('Processes', '进程'), processes as Face<never>, (value) => {
            const nodes = value as ProcessNode[];
            const entries = nodes.flatMap((node) => node.processes);
            return (
              <Table
                size="small"
                rowKey={(entry) => `${entry.nodeId}:${entry.processInstanceId}`}
                pagination={false}
                dataSource={entries}
                columns={[
                  { title: t('Node', '节点'), dataIndex: 'nodeId' },
                  { title: t('Process', '进程'), dataIndex: 'processInstanceId' },
                  { title: t('Role', '角色'), dataIndex: 'role' },
                  { title: 'CPU', render: (_, entry) => `${entry.cpuPercent.toFixed(1)}%` },
                  { title: t('Memory', '内存'), render: (_, entry) => megabytes(entry.memoryUsedBytes) },
                  { title: t('Uptime', '运行时长'), render: (_, entry) => uptime(entry.uptimeSeconds) },
                ]}
              />
            );
          })}
        </Col>
      </Row>
    </Space>
  );
}
