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

const commandLabels: Record<CommandKey, string> = {
  suspendMatchQueue: 'Suspend match queue',
  resumeMatchQueue: 'Resume match queue',
  retryRoomRecovery: 'Retry room recovery',
  drainProcess: 'Drain process',
  retrySettlement: 'Retry settlement',
  reconcilePlatformTask: 'Reconcile platform task',
  dispatchRoomCommand: 'Dispatch room command',
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
        ? 'Your scope does not cover this operation.'
        : 'The control plane is unreachable. Try again shortly.');
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
        ? 'The control plane refused this command: your role or scope does not cover it.'
        : err instanceof AdminError && err.status === 409
          ? 'This request id is already spent by a different request.'
          : 'The control plane refused or could not take this command. Check the target and try again.');
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
      setAuditError('The control plane refused or could not read the audit trail. Try again shortly.');
    }
  };

  const reasonField = (
    <Form.Item name="reason" label="Reason" rules={[{ required: true, message: 'A reason is required.' }]}>
      <Input placeholder="why this command, for the audit row" />
    </Form.Item>
  );
  const scopeFields = (
    <>
      <Form.Item name="tenantId" label="Tenant" rules={[{ required: true, message: 'Tenant is required.' }]}>
        <Input placeholder="tenant id" />
      </Form.Item>
      <Form.Item name="gameId" label="Game" rules={[{ required: true, message: 'Game is required.' }]}>
        <Input placeholder="game id" />
      </Form.Item>
    </>
  );
  const roomField = (
    <Form.Item name="roomId" label="Room" rules={[{ required: true, message: 'Room is required.' }]}>
      <Input placeholder="room id" />
    </Form.Item>
  );

  const tabs: Array<{ key: CommandKey; label: string; content: ReactNode }> = [
    {
      key: 'suspendMatchQueue',
      label: commandLabels.suspendMatchQueue,
      content: (
        <>
          {reasonField}
          <Form.Item name="partitionKey" label="Partition key" rules={[{ required: true, message: 'Partition key is required.' }]}>
            <Input placeholder="tenant/game the pairing gate parks" />
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
          <Form.Item name="partitionKey" label="Partition key" rules={[{ required: true, message: 'Partition key is required.' }]}>
            <Input placeholder="tenant/game the pairing gate resumes" />
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
          <Form.Item name="toInstanceId" label="To instance (optional)">
            <Input placeholder="leave empty for the coordinator to place" />
          </Form.Item>
          <Form.Item name="tickMs" label="Tick ms (optional)">
            <InputNumber min={0} placeholder="0 = module default" style={{ width: '100%' }} />
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
          <Form.Item name="processInstanceId" label="Process instance" rules={[{ required: true, message: 'Process instance is required.' }]}>
            <Input placeholder="process instance id to drain" />
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
          <Form.Item name="roomId" label="Room" rules={[{ required: true, message: 'Room is required.' }]}>
            <Input placeholder="room id whose settlement retries" />
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
          <Form.Item name="taskId" label="Task" rules={[{ required: true, message: 'Task id is required.' }]}>
            <Input placeholder="platform task id to reconcile" />
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
          <Form.Item name="expectedEpoch" label="Expected epoch (optional)">
            <InputNumber min={0} placeholder="0 = whatever the ownership row holds now" style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="participantId" label="Participant" rules={[{ required: true, message: 'Participant is required.' }]}>
            <Input placeholder="the participant whose seat the command plays as" />
          </Form.Item>
          <Form.Item
            name="payloadJson"
            label="Payload (module JSON)"
            rules={[
              { required: true, message: 'The module payload is required.' },
              {
                validator: (_, value) => {
                  if (!value) return Promise.resolve();
                  try {
                    JSON.parse(String(value));
                    return Promise.resolve();
                  } catch {
                    return Promise.reject(new Error('The payload is not valid JSON.'));
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
    <Card title="Operations and receipts">
      <Typography.Paragraph type="secondary">
        Every command carries a fresh request id and your own identity; the audit row is
        written before anything acts, so a replayed request id answers as a visible
        duplicate instead of acting twice. Submit below, then query the receipt by its
        operation id above.
      </Typography.Paragraph>
      <Card title="Operation receipt" style={{ marginBottom: 16 }}>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 12 }}>
          A room command answers Accepted or Applied at a committed tick from the data
          plane; a control-plane command answers Audited with its recorded evidence; an
          unknown id answers NotFound — never folded into an accepted answer.
        </Typography.Paragraph>
        {queryError !== null && (
          <Alert type="error" showIcon message={queryError} style={{ marginBottom: 12 }} />
        )}
        <Form layout="inline" colon={false} name="receipt-query" onFinish={queryReceipt} disabled={querying}>
          <Form.Item
            name="operationId"
            rules={[{ required: true, message: 'Enter the operation id.' }]}
            style={{ minWidth: 320 }}
          >
            {/* The inline layout renders no visible label element; the aria-label is
                the input's accessible name. */}
            <Input aria-label="Operation id" placeholder="operation id" allowClear />
          </Form.Item>
          <Button type="primary" htmlType="submit" loading={querying}>
            Query receipt
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
              <Descriptions.Item label="applied tick">
                {receipt.appliedTick === 0 ? '—' : receipt.appliedTick}
              </Descriptions.Item>
              <Descriptions.Item label="note">{receipt.note}</Descriptions.Item>
            </Descriptions>
          </>
        )}
      </Card>
      <Card title="Audit trail" style={{ marginBottom: 16 }}>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 12 }}>
          Every audited command, newest first — one exact word per filter, empty means
          unfiltered. The walk continues strictly older rows; the log is append-only, so
          a page never reorders.
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
            <Input aria-label="Command filter" placeholder="command word, empty = all" allowClear />
          </Form.Item>
          <Form.Item name="operator" style={{ minWidth: 180 }}>
            <Input aria-label="Operator filter" placeholder="operator, empty = all" allowClear />
          </Form.Item>
          <Form.Item name="maxEntries" initialValue={20}>
            <InputNumber aria-label="Max entries" min={1} max={200} />
          </Form.Item>
          <Button type="primary" htmlType="submit">
            Query audit
          </Button>
        </Form>
        {auditEntries.length > 0 && (
          <>
            <Space style={{ marginTop: 12 }}>
              {auditState?.hasMore && auditValues !== null && (
                <Button onClick={() => runAudit(auditValues, auditState.nextPageToken, true)}>
                  Continue
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
                  title: 'Time',
                  dataIndex: 'createdAtUnixMs',
                  key: 'time',
                  render: (ms: number) => new Date(ms).toISOString().replace('T', ' ').slice(0, 19),
                },
                { title: 'Command', dataIndex: 'command', key: 'command' },
                { title: 'Operator', dataIndex: 'operator', key: 'operator' },
                { title: 'Request id', dataIndex: 'requestId', key: 'requestId' },
                { title: 'Reason', dataIndex: 'reason', key: 'reason' },
                { title: 'Scope', dataIndex: 'scope', key: 'scope' },
                { title: 'Detail', dataIndex: 'detail', key: 'detail' },
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
          message="Your role is below the command floor: receipts stay open, commands stay closed."
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
                Submit
              </Button>
            </Form>
          ),
        }))}
      />
    </Card>
  );
}
