import {
  Alert,
  Button,
  Card,
  Descriptions,
  Form,
  Input,
  InputNumber,
  Space,
  Table,
  Tabs,
  Tag,
  Typography,
} from 'antd';
import { useState } from 'react';
import type { ReactNode } from 'react';
import { createAdminClient, AdminError } from '../api/client';
import type { AdminGetOperationStatusReply, AdminAuditEntryProto } from '../api/contract';
import { useAuth } from '../auth';
import { useI18n } from '../i18n';

// The management operations and receipts page (FIX-WEB-10, architecture 12.1): commands
// are submitted with a fresh request id and the signed-in operator as the body identity
// (the server refuses a forged one), and every submission's reply carries the receipt —
// queryable afterwards by the operation id whether the answer lives in the data plane
// (Accepted → Applied at a committed tick, FIX-GM-07) or the audit trail (Audited,
// FIX-WEB-09). Accepted, applied and unknown are never folded together. Every command
// sits on the gm floor (FIX-WEB-01), so a viewer gets the receipts but the command tabs
// stay closed. The audit trail query (FIX-WEB-15) sits beside the receipts: the log is
// a read on the viewer floor — newest first, exact-word filters, an audit-id keyset
// cursor walking strictly older rows. The epoch bump gets its own page later (FIX-WEB-11).
type CommandKey =
  | 'suspendMatchQueue'
  | 'resumeMatchQueue'
  | 'retryRoomRecovery'
  | 'drainProcess'
  | 'retrySettlement'
  | 'reconcilePlatformTask'
  | 'dispatchRoomCommand';

type Reply = Record<string, unknown>;

const stageColor: Record<string, string> = {
  Applied: 'green',
  Accepted: 'orange',
  DurableAccepted: 'orange',
  Audited: 'blue',
  NotFound: 'red',
};

function newRequestId(): string {
  return crypto.randomUUID();
}

function ReplyCard({ title, reply }: { title: string; reply: Reply }) {
  const stage = typeof reply.stage === 'string' ? reply.stage : undefined;
  const duplicate = reply.duplicate === true;
  return (
    <Card size="small" title={title} style={{ marginBottom: 16 }}>
      <Descriptions size="small" column={1}>
        {Object.entries(reply).map(([key, value]) => (
          <Descriptions.Item key={key} label={key}>
            {String(value ?? '')}
          </Descriptions.Item>
        ))}
      </Descriptions>
      {(stage !== undefined || duplicate) && (
        <Space style={{ marginTop: 8 }}>
          {stage !== undefined && <Tag color={stageColor[stage] ?? 'default'}>{stage}</Tag>}
          {duplicate && <Tag color="gold">duplicate</Tag>}
        </Space>
      )}
    </Card>
  );
}

export default function OperationsPage() {
  const { token, operator } = useAuth();
  const { t } = useI18n();
  const canCommand = operator?.role === 'gm';
  const [receipt, setReceipt] = useState<AdminGetOperationStatusReply | null>(null);
  const [queryError, setQueryError] = useState<string | null>(null);
  const [commandReply, setCommandReply] = useState<{ command: CommandKey; reply: Reply } | null>(null);
  const [commandError, setCommandError] = useState<string | null>(null);
  const [querying, setQuerying] = useState(false);
  // The audit trail walk (FIX-WEB-15): newest first, one page at a time — the continuation
  // carries the server's cursor and appends strictly older rows.
  const [auditEntries, setAuditEntries] = useState<AdminAuditEntryProto[]>([]);
  const [auditState, setAuditState] = useState<{ hasMore: boolean; nextPageToken: string } | null>(null);
  const [auditError, setAuditError] = useState<string | null>(null);
  const [auditValues, setAuditValues] = useState<Record<string, unknown> | null>(null);

  const commandLabels: Record<CommandKey, string> = {
    suspendMatchQueue: t('Suspend match queue', '暂停匹配队列'),
    resumeMatchQueue: t('Resume match queue', '恢复匹配队列'),
    retryRoomRecovery: t('Retry room recovery', '重试房间恢复'),
    drainProcess: t('Drain process', '排空进程'),
    retrySettlement: t('Retry settlement', '重试结算'),
    reconcilePlatformTask: t('Reconcile platform task', '对账平台任务'),
    dispatchRoomCommand: t('Dispatch room command', '下发房间命令'),
  };

  const queryReceipt = async ({ operationId }: { operationId: string }) => {
    setQuerying(true);
    setQueryError(null);
    setReceipt(null);
    try {
      // The scope dims ride along empty: an empty dimension constrains nothing
      // (FIX-WEB-09), so the id alone finds the answer.
      setReceipt(await createAdminClient(token ?? '').getOperationStatus({
        operationId, tenantId: '', gameId: '', roomId: '',
      }));
    } catch (err) {
      setQueryError(err instanceof AdminError && err.status === 403
        ? t('Your scope does not cover this operation.', '你的作用域不覆盖这条操作。')
        : t('The control plane is unreachable. Try again shortly.', '控制面当前不可达，请稍后再试。'));
    } finally {
      setQuerying(false);
    }
  };

  const runCommand = async (command: CommandKey, values: Record<string, unknown>) => {
    setCommandError(null);
    setCommandReply(null);
    const common = { requestId: newRequestId(), operator: operator?.operatorId ?? '' };
    try {
      const client = createAdminClient(token ?? '');
      let reply: Reply;
      switch (command) {
        case 'suspendMatchQueue':
        case 'resumeMatchQueue': {
          const request = {
            ...common,
            reason: String(values.reason ?? ''),
            partitionKey: String(values.partitionKey ?? ''),
          };
          reply = { ...(command === 'suspendMatchQueue'
            ? await client.suspendMatchQueue(request)
            : await client.resumeMatchQueue(request)) };
          break;
        }
        case 'retryRoomRecovery':
          reply = { ...(await client.retryRoomRecovery({
            ...common,
            reason: String(values.reason ?? ''),
            tenantId: String(values.tenantId ?? ''),
            gameId: String(values.gameId ?? ''),
            roomId: String(values.roomId ?? ''),
            toInstanceId: String(values.toInstanceId ?? ''),
            tickMs: Number(values.tickMs ?? 0),
          })) };
          break;
        case 'drainProcess':
          reply = { ...(await client.drainProcess({
            ...common,
            reason: String(values.reason ?? ''),
            processInstanceId: String(values.processInstanceId ?? ''),
          })) };
          break;
        case 'retrySettlement':
          reply = { ...(await client.retrySettlement({
            ...common,
            reason: String(values.reason ?? ''),
            roomId: String(values.roomId ?? ''),
          })) };
          break;
        case 'reconcilePlatformTask':
          reply = { ...(await client.reconcilePlatformTask({
            ...common,
            reason: String(values.reason ?? ''),
            taskId: String(values.taskId ?? ''),
          })) };
          break;
        case 'dispatchRoomCommand':
          reply = { ...(await client.dispatchRoomCommand({
            ...common,
            reason: String(values.reason ?? ''),
            tenantId: String(values.tenantId ?? ''),
            gameId: String(values.gameId ?? ''),
            roomId: String(values.roomId ?? ''),
            expectedEpoch: Number(values.expectedEpoch ?? 0),
            participantId: String(values.participantId ?? ''),
            payloadJson: String(values.payloadJson ?? ''),
          })) };
          break;
      }
      setCommandReply({ command, reply });
    } catch (err) {
      setCommandError(err instanceof AdminError && err.status === 403
        ? t('The control plane refused this command: your role or scope does not cover it.',
          '控制面拒绝了这条命令：你的角色或作用域不覆盖它。')
        : err instanceof AdminError && err.status === 409
          ? t('This request id is already spent by a different request.',
            '这个请求 id 已被另一个请求用掉。')
          : t('The control plane refused or could not take this command. Check the target and try again.',
            '控制面拒绝了这条命令或无法受理。请检查目标后重试。'));
    }
  };

  const runAudit = async (values: Record<string, unknown>, pageToken: string, append: boolean) => {
    setAuditError(null);
    setAuditValues(values);
    if (!append) {
      setAuditEntries([]);
      setAuditState(null);
    }
    try {
      const reply = await createAdminClient(token ?? '').getAuditTrail({
        command: String(values.command ?? ''),
        operator: String(values.operator ?? ''),
        maxEntries: Number(values.maxEntries ?? 20),
        pageToken,
      });
      setAuditEntries((current) => (append ? [...current, ...reply.entries] : reply.entries));
      setAuditState({ hasMore: reply.hasMore, nextPageToken: reply.nextPageToken });
    } catch {
      setAuditEntries([]);
      setAuditState(null);
      setAuditError(t('The control plane refused or could not read the audit trail. Try again shortly.',
        '控制面拒绝了这次审计读取或无法读取。请稍后再试。'));
    }
  };

  const reasonField = (
    <Form.Item name="reason" label={t('Reason', '理由')} rules={[{ required: true, message: t('A reason is required.', '理由必填。') }]}>
      <Input placeholder={t('why this command, for the audit row', '为何下这条命令——写进审计行')} />
    </Form.Item>
  );
  const scopeFields = (
    <>
      <Form.Item name="tenantId" label={t('Tenant', '租户')} rules={[{ required: true, message: t('Tenant is required.', '租户必填。') }]}>
        <Input placeholder={t('tenant id', '租户 id')} />
      </Form.Item>
      <Form.Item name="gameId" label={t('Game', '游戏')} rules={[{ required: true, message: t('Game is required.', '游戏必填。') }]}>
        <Input placeholder={t('game id', '游戏 id')} />
      </Form.Item>
    </>
  );
  const roomField = (
    <Form.Item name="roomId" label={t('Room', '房间')} rules={[{ required: true, message: t('Room is required.', '房间必填。') }]}>
      <Input placeholder={t('room id', '房间 id')} />
    </Form.Item>
  );

  const tabs: Array<{ key: CommandKey; label: string; content: ReactNode }> = [
    {
      key: 'suspendMatchQueue',
      label: commandLabels.suspendMatchQueue,
      content: (
        <>
          {reasonField}
          <Form.Item name="partitionKey" label={t('Partition key', '分区键')} rules={[{ required: true, message: t('Partition key is required.', '分区键必填。') }]}>
            <Input placeholder={t('tenant/game the pairing gate parks', '配对门停下的 tenant/game')} />
          </Form.Item>
        </>
      ),
    },
    {
      key: 'resumeMatchQueue',
      label: commandLabels.resumeMatchQueue,
      content: (
        <>
          {reasonField}
          <Form.Item name="partitionKey" label={t('Partition key', '分区键')} rules={[{ required: true, message: t('Partition key is required.', '分区键必填。') }]}>
            <Input placeholder={t('tenant/game the pairing gate resumes', '配对门恢复的 tenant/game')} />
          </Form.Item>
        </>
      ),
    },
    {
      key: 'retryRoomRecovery',
      label: commandLabels.retryRoomRecovery,
      content: (
        <>
          {reasonField}
          {scopeFields}
          {roomField}
          <Form.Item name="toInstanceId" label={t('To instance (optional)', '目标实例（可选）')}>
            <Input placeholder={t('leave empty for the coordinator to place', '留空由协调者放置')} />
          </Form.Item>
          <Form.Item name="tickMs" label={t('Tick ms (optional)', 'Tick 毫秒（可选）')}>
            <InputNumber min={0} placeholder={t('0 = module default', '0 = 模块默认')} style={{ width: '100%' }} />
          </Form.Item>
        </>
      ),
    },
    {
      key: 'drainProcess',
      label: commandLabels.drainProcess,
      content: (
        <>
          {reasonField}
          <Form.Item name="processInstanceId" label={t('Process instance', '进程实例')} rules={[{ required: true, message: t('Process instance is required.', '进程实例必填。') }]}>
            <Input placeholder={t('process instance id to drain', '要排空的进程实例 id')} />
          </Form.Item>
        </>
      ),
    },
    {
      key: 'retrySettlement',
      label: commandLabels.retrySettlement,
      content: (
        <>
          {reasonField}
          <Form.Item name="roomId" label={t('Room', '房间')} rules={[{ required: true, message: t('Room is required.', '房间必填。') }]}>
            <Input placeholder={t('room id whose settlement retries', '要重试结算的房间 id')} />
          </Form.Item>
        </>
      ),
    },
    {
      key: 'reconcilePlatformTask',
      label: commandLabels.reconcilePlatformTask,
      content: (
        <>
          {reasonField}
          <Form.Item name="taskId" label={t('Task', '任务')} rules={[{ required: true, message: t('Task id is required.', '任务 id 必填。') }]}>
            <Input placeholder={t('platform task id to reconcile', '要对账的平台任务 id')} />
          </Form.Item>
        </>
      ),
    },
    {
      key: 'dispatchRoomCommand',
      label: commandLabels.dispatchRoomCommand,
      content: (
        <>
          {reasonField}
          {scopeFields}
          {roomField}
          <Form.Item name="expectedEpoch" label={t('Expected epoch (optional)', '期望纪元（可选）')}>
            <InputNumber min={0} placeholder={t('0 = whatever the ownership row holds now', '0 = 所有权行当前值')} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="participantId" label={t('Participant', '参与者')} rules={[{ required: true, message: t('Participant is required.', '参与者必填。') }]}>
            <Input placeholder={t('the participant whose seat the command plays as', '命令以其座位身份行动的参与者')} />
          </Form.Item>
          <Form.Item
            name="payloadJson"
            label={t('Payload (module JSON)', '载荷（模块 JSON）')}
            rules={[
              { required: true, message: t('The module payload is required.', '模块载荷必填。') },
              {
                validator: (_, value) => {
                  if (!value) return Promise.resolve();
                  try {
                    JSON.parse(String(value));
                    return Promise.resolve();
                  } catch {
                    return Promise.reject(new Error(t('The payload is not valid JSON.', '载荷不是合法 JSON。')));
                  }
                },
              },
            ]}
          >
            <Input.TextArea rows={3} placeholder={'{"action":"boost","level":1}'} />
          </Form.Item>
        </>
      ),
    },
  ];

  return (
    <Card title={t('Operations and receipts', '操作与回执')}>
      <Typography.Paragraph type="secondary">
        {t(
          'Every command carries a fresh request id and your own identity; the audit row is written before anything acts, so a replayed request id answers as a visible duplicate instead of acting twice. Submit below, then query the receipt by its operation id above.',
          '每条命令都带着新铸的请求 id 和你自己的身份；审计行先于任何动作落账，重放的请求 id 会以可见的重复作答而不是二次执行。在下方提交，再在上方按操作 id 查回执。',
        )}
      </Typography.Paragraph>
      <Card title={t('Operation receipt', '操作回执')} style={{ marginBottom: 16 }}>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 12 }}>
          {t(
            'A room command answers Accepted or Applied at a committed tick from the data plane; a control-plane command answers Audited with its recorded evidence; an unknown id answers NotFound — never folded into an accepted answer.',
            '房间命令由数据面在已提交 tick 上回答 Accepted 或 Applied；控制面命令回答 Audited 并附其记录的证据；未知 id 回答 NotFound——绝不折叠进已接受的回答。',
          )}
        </Typography.Paragraph>
        {queryError !== null && (
          <Alert type="error" showIcon message={queryError} style={{ marginBottom: 12 }} />
        )}
        <Form layout="inline" colon={false} name="receipt-query" onFinish={queryReceipt} disabled={querying}>
          <Form.Item
            name="operationId"
            rules={[{ required: true, message: t('Enter the operation id.', '请输入操作 id。') }]}
            style={{ minWidth: 320 }}
          >
            {/* The inline layout renders no visible label element; the aria-label is
                the input's accessible name. */}
            <Input aria-label={t('Operation id', '操作 id')} placeholder={t('operation id', '操作 id')} allowClear />
          </Form.Item>
          <Button type="primary" htmlType="submit" loading={querying}>
            {t('Query receipt', '查回执')}
          </Button>
        </Form>
        {receipt !== null && (
          <>
            <Space style={{ marginTop: 12 }}>
              <Tag color={stageColor[receipt.stage] ?? 'default'}>{receipt.stage}</Tag>
            </Space>
            <Descriptions size="small" column={1} style={{ marginTop: 8 }}>
              <Descriptions.Item label="found">{String(receipt.found)}</Descriptions.Item>
              <Descriptions.Item label="platform">{receipt.platform}</Descriptions.Item>
              <Descriptions.Item label={t('applied tick', '已应用 tick')}>
                {receipt.appliedTick === 0 ? '—' : receipt.appliedTick}
              </Descriptions.Item>
              <Descriptions.Item label="note">{receipt.note}</Descriptions.Item>
            </Descriptions>
          </>
        )}
      </Card>
      <Card title={t('Audit trail', '审计轨迹')} style={{ marginBottom: 16 }}>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 12 }}>
          {t(
            'Every audited command, newest first — one exact word per filter, empty means unfiltered. The walk continues strictly older rows; the log is append-only, so a page never reorders.',
            '每条被审计的命令，最新在前——每个过滤维恰好一个精确词，留空即不过滤。走板只续更老的行；日志只追加，页从不重排。',
          )}
        </Typography.Paragraph>
        {auditError !== null && (
          <Alert type="error" showIcon message={auditError} style={{ marginBottom: 12 }} />
        )}
        <Form
          layout="inline"
          colon={false}
          name="audit-query"
          onFinish={(values) => runAudit(values as Record<string, unknown>, '', false)}
        >
          <Form.Item name="command" style={{ minWidth: 220 }}>
            <Input aria-label={t('Command filter', '命令过滤')} placeholder={t('command word, empty = all', '命令词，留空 = 全部')} allowClear />
          </Form.Item>
          <Form.Item name="operator" style={{ minWidth: 180 }}>
            <Input aria-label={t('Operator filter', '操作者过滤')} placeholder={t('operator, empty = all', '操作者，留空 = 全部')} allowClear />
          </Form.Item>
          <Form.Item name="maxEntries" initialValue={20}>
            <InputNumber aria-label={t('Max entries', '条数上限')} min={1} max={200} />
          </Form.Item>
          <Button type="primary" htmlType="submit">
            {t('Query audit', '查审计')}
          </Button>
        </Form>
        {auditEntries.length > 0 && (
          <>
            <Space style={{ marginTop: 12 }}>
              {auditState?.hasMore && auditValues !== null && (
                <Button onClick={() => runAudit(auditValues, auditState.nextPageToken, true)}>
                  {t('Continue', '继续')}
                </Button>
              )}
            </Space>
            <Table
              size="small"
              style={{ marginTop: 12 }}
              rowKey={(entry) => `${entry.createdAtUnixMs}:${entry.requestId}`}
              pagination={false}
              scroll={{ x: 900 }}
              columns={[
                {
                  title: t('Time', '时间'),
                  dataIndex: 'createdAtUnixMs',
                  key: 'time',
                  render: (ms: number) => new Date(ms).toISOString().replace('T', ' ').slice(0, 19),
                },
                { title: t('Command', '命令'), dataIndex: 'command', key: 'command' },
                { title: t('Operator', '操作者'), dataIndex: 'operator', key: 'operator' },
                { title: t('Request id', '请求 id'), dataIndex: 'requestId', key: 'requestId' },
                { title: t('Reason', '理由'), dataIndex: 'reason', key: 'reason' },
                { title: t('Scope', '作用域'), dataIndex: 'scope', key: 'scope' },
                { title: t('Detail', '详情'), dataIndex: 'detail', key: 'detail' },
              ]}
              dataSource={auditEntries}
            />
          </>
        )}
      </Card>
      {!canCommand && (
        <Alert
          type="info"
          showIcon
          message={t('Your role is below the command floor: receipts stay open, commands stay closed.',
            '你的角色低于命令地板：回执保持开放，命令保持关闭。')}
          style={{ marginBottom: 12 }}
        />
      )}
      {commandError !== null && (
        <Alert type="error" showIcon message={commandError} style={{ marginBottom: 12 }} />
      )}
      {commandReply !== null && (
        <ReplyCard title={commandLabels[commandReply.command]} reply={commandReply.reply} />
      )}
      <Tabs
        items={tabs.map(({ key, label, content }) => ({
          key,
          label,
          disabled: !canCommand,
          children: (
            <Form
              layout="vertical"
              name={`command-${key}`}
              onFinish={(values) => runCommand(key, values as Record<string, unknown>)}
              disabled={!canCommand}
            >
              {content}
              <Button type="primary" htmlType="submit">
                {t('Submit', '提交')}
              </Button>
            </Form>
          ),
        }))}
      />
    </Card>
  );
}
