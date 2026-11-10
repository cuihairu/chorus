import {
  Alert,
  Button,
  Card,
  Descriptions,
  Form,
  Input,
  InputNumber,
  Space,
  Tag,
  Typography,
} from 'antd';
import { useState } from 'react';
import { createAdminClient, AdminError } from '../api/client';
import type { AdminGetRoomEpochReply, AdminBumpRoomEpochReply } from '../api/contract';
import { useAuth } from '../auth';

// The standalone room-epoch form (FIX-WEB-11, architecture 4.5 + GM-01..05): the
// operator first looks a room up (read-only GetRoomEpoch), then bumps its epoch with
// the expected epoch the lookup just showed — the stored transaction refuses a stale
// expectation visibly, never acts on one. A bump lifts both ownership and room rows to
// the target (default expected+1), keeps the owner, and parks the room in Recovering:
// old-epoch commits fence from here on, and bringing the room back is the explicit
// retry-room-recovery command, not an implicit side effect. The reply's route sync
// status is the honest split between "the store committed" (always, if this answers)
// and "a gateway confirmed the new routing" (Pending until it happens). A replayed
// request id answers as a duplicate carrying the recorded values (GM-05). The whole
// floor is gm (FIX-WEB-17 acceptance, GM-06's tested decision): the lookup carries the
// audited triple and feeds the bump, so a viewer gets neither half of the page.
const ownershipColor: Record<string, string> = {
  Active: 'green',
  Recovering: 'orange',
  Released: 'default',
};

const routeSyncColor: Record<string, string> = {
  Synced: 'green',
  Pending: 'orange',
  Removed: 'red',
};

function newRequestId(): string {
  return crypto.randomUUID();
}

function ReplyRows({ reply }: { reply: Record<string, unknown> }) {
  return (
    <Descriptions size="small" column={1}>
      {Object.entries(reply).map(([key, value]) => (
        <Descriptions.Item key={key} label={key}>
          {String(value ?? '')}
        </Descriptions.Item>
      ))}
    </Descriptions>
  );
}

export default function EpochPage() {
  const { token, operator } = useAuth();
  const canCommand = operator?.role === 'gm';
  const [lookup, setLookup] = useState<AdminGetRoomEpochReply | null>(null);
  const [lookupError, setLookupError] = useState<string | null>(null);
  const [bump, setBump] = useState<AdminBumpRoomEpochReply | null>(null);
  const [bumpError, setBumpError] = useState<string | null>(null);

  const describe = (err: unknown, refused: string): string =>
    err instanceof AdminError && err.status === 403
      ? refused
      : err instanceof AdminError && err.status === 409
        ? 'This request id is already spent by a different request.'
        : 'The control plane refused or could not take this. Check the target and try again.';

  const runLookup = async (values: Record<string, unknown>) => {
    setLookupError(null);
    setLookup(null);
    try {
      setLookup(await createAdminClient(token ?? '').getRoomEpoch({
        requestId: newRequestId(),
        operator: operator?.operatorId ?? '',
        reason: 'epoch lookup',
        tenantId: String(values.tenantId ?? ''),
        gameId: String(values.gameId ?? ''),
        roomId: String(values.roomId ?? ''),
      }));
    } catch (err) {
      setLookupError(describe(err, 'Your scope does not cover this room.'));
    }
  };

  const runBump = async (values: Record<string, unknown>) => {
    setBumpError(null);
    setBump(null);
    try {
      setBump(await createAdminClient(token ?? '').bumpRoomEpoch({
        requestId: newRequestId(),
        operator: operator?.operatorId ?? '',
        reason: String(values.reason ?? ''),
        tenantId: String(values.tenantId ?? ''),
        gameId: String(values.gameId ?? ''),
        roomId: String(values.roomId ?? ''),
        expectedEpoch: Number(values.expectedEpoch ?? 0),
        targetEpoch: Number(values.targetEpoch ?? 0),
      }));
    } catch (err) {
      setBumpError(describe(err, 'The control plane refused this command: your role or scope does not cover it.'));
    }
  };

  return (
    <Card title="Room epoch">
      <Typography.Paragraph type="secondary">
        Look a room up, then bump its epoch against the expected value the lookup showed —
        a stale expectation is refused visibly. A bump parks the room in Recovering and
        fences old-epoch commits; bringing it back is the explicit recovery command on the
        operations page. The route sync status says whether a gateway has confirmed the
        new routing — committed is not the same as confirmed.
      </Typography.Paragraph>
      <Card title="Lookup" style={{ marginBottom: 16 }}>
        <Form layout="vertical" name="epoch-lookup" onFinish={runLookup} disabled={!canCommand}>
          <Space wrap size="large">
            <Form.Item name="tenantId" label="Tenant" rules={[{ required: true, message: 'Tenant is required.' }]}>
              <Input placeholder="tenant id" />
            </Form.Item>
            <Form.Item name="gameId" label="Game" rules={[{ required: true, message: 'Game is required.' }]}>
              <Input placeholder="game id" />
            </Form.Item>
            <Form.Item name="roomId" label="Room" rules={[{ required: true, message: 'Room is required.' }]}>
              <Input placeholder="room id" />
            </Form.Item>
            <Button type="primary" htmlType="submit" disabled={!canCommand}>
              Look up
            </Button>
          </Space>
        </Form>
        {lookupError !== null && <Alert type="error" showIcon message={lookupError} style={{ marginTop: 12 }} />}
        {lookup !== null && (
          <>
            <Space style={{ marginTop: 12 }}>
              <Tag color={ownershipColor[lookup.status] ?? 'default'}>{lookup.status}</Tag>
            </Space>
            <ReplyRows reply={lookup as unknown as Record<string, unknown>} />
          </>
        )}
      </Card>
      {!canCommand && (
        <Alert
          type="info"
          showIcon
          message="Your role is below the gm floor: epoch lookups and bumps both stay closed."
          style={{ marginBottom: 12 }}
        />
      )}
      <Card title="Bump" style={{ marginBottom: 16 }}>
        <Form layout="vertical" name="epoch-bump" onFinish={runBump} disabled={!canCommand}>
          <Space wrap size="large">
            <Form.Item name="reason" label="Reason" rules={[{ required: true, message: 'A reason is required.' }]}>
              <Input placeholder="why this epoch bump, for the audit row" style={{ minWidth: 280 }} />
            </Form.Item>
            <Form.Item name="tenantId" label="Tenant" rules={[{ required: true, message: 'Tenant is required.' }]}>
              <Input placeholder="tenant id" />
            </Form.Item>
            <Form.Item name="gameId" label="Game" rules={[{ required: true, message: 'Game is required.' }]}>
              <Input placeholder="game id" />
            </Form.Item>
            <Form.Item name="roomId" label="Room" rules={[{ required: true, message: 'Room is required.' }]}>
              <Input placeholder="room id" />
            </Form.Item>
            <Form.Item
              name="expectedEpoch"
              label="Expected epoch"
              rules={[{ required: true, message: 'The expected epoch is required.' }]}
            >
              <InputNumber min={1} placeholder="the epoch the lookup showed" />
            </Form.Item>
            <Form.Item name="targetEpoch" label="Target epoch (optional)">
              <InputNumber min={0} placeholder="empty = expected + 1" />
            </Form.Item>
            <Button type="primary" htmlType="submit" disabled={!canCommand}>
              Bump epoch
            </Button>
          </Space>
        </Form>
        {bumpError !== null && <Alert type="error" showIcon message={bumpError} style={{ marginTop: 12 }} />}
        {bump !== null && (
          <>
            <Space style={{ marginTop: 12 }}>
              <Tag color={ownershipColor[bump.ownershipStatus] ?? 'default'}>{bump.ownershipStatus}</Tag>
              <Tag color={routeSyncColor[bump.routeSyncStatus] ?? 'default'}>{bump.routeSyncStatus}</Tag>
              {bump.duplicate && <Tag color="gold">duplicate</Tag>}
            </Space>
            <ReplyRows reply={bump as unknown as Record<string, unknown>} />
            <Typography.Paragraph type="secondary" style={{ marginTop: 8 }}>
              Receipt {bump.operationId}: replaying it answers a duplicate with the recorded
              values, and the receipt query on the operations page answers for this id.
            </Typography.Paragraph>
          </>
        )}
      </Card>
    </Card>
  );
}
