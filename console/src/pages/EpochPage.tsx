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
import { useI18n } from '../i18n';
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
  const { t } = useI18n();
  const canCommand = operator?.role === 'gm';
  const [lookup, setLookup] = useState<AdminGetRoomEpochReply | null>(null);
  const [lookupError, setLookupError] = useState<string | null>(null);
  const [bump, setBump] = useState<AdminBumpRoomEpochReply | null>(null);
  const [bumpError, setBumpError] = useState<string | null>(null);

  const describe = (err: unknown, refused: string): string =>
    err instanceof AdminError && err.status === 403
      ? refused
      : err instanceof AdminError && err.status === 409
        ? t('This request id is already spent by a different request.',
          '这个请求 id 已被另一个请求用掉。')
        : t('The control plane refused or could not take this. Check the target and try again.',
          '控制面拒绝了这条请求或无法受理。请检查目标后重试。');

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
      setLookupError(describe(err, t('Your scope does not cover this room.',
        '你的作用域不覆盖这个房间。')));
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
      setBumpError(describe(err, t(
        'The control plane refused this command: your role or scope does not cover it.',
        '控制面拒绝了这条命令：你的角色或作用域不覆盖它。')));
    }
  };

  return (
    <Card title={t('Room epoch', '房间纪元')}>
      <Typography.Paragraph type="secondary">
        {t(
          'Look a room up, then bump its epoch against the expected value the lookup showed — a stale expectation is refused visibly. A bump parks the room in Recovering and fences old-epoch commits; bringing it back is the explicit recovery command on the operations page. The route sync status says whether a gateway has confirmed the new routing — committed is not the same as confirmed.',
          '先查一个房间，再对照查询显示的期望值抬它的纪元——过期的期望会被显式拒绝。抬纪元会把房间停进 Recovering 并栅化旧纪元提交；把房间带回来是操作页上显式的恢复命令。路由同步状态说明是否有网关已确认新路由——已提交不等于已确认。',
        )}
      </Typography.Paragraph>
      <Card title={t('Lookup', '查询')} style={{ marginBottom: 16 }}>
        <Form layout="vertical" name="epoch-lookup" onFinish={runLookup} disabled={!canCommand}>
          <Space wrap size="large">
            <Form.Item name="tenantId" label={t('Tenant', '租户')} rules={[{ required: true, message: t('Tenant is required.', '租户必填。') }]}>
              <Input placeholder={t('tenant id', '租户 id')} />
            </Form.Item>
            <Form.Item name="gameId" label={t('Game', '游戏')} rules={[{ required: true, message: t('Game is required.', '游戏必填。') }]}>
              <Input placeholder={t('game id', '游戏 id')} />
            </Form.Item>
            <Form.Item name="roomId" label={t('Room', '房间')} rules={[{ required: true, message: t('Room is required.', '房间必填。') }]}>
              <Input placeholder={t('room id', '房间 id')} />
            </Form.Item>
            <Button type="primary" htmlType="submit" disabled={!canCommand}>
              {t('Look up', '查询')}
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
          message={t('Your role is below the gm floor: epoch lookups and bumps both stay closed.',
            '你的角色低于 gm 地板：纪元查询与抬升都保持关闭。')}
          style={{ marginBottom: 12 }}
        />
      )}
      <Card title={t('Bump', '抬升')} style={{ marginBottom: 16 }}>
        <Form layout="vertical" name="epoch-bump" onFinish={runBump} disabled={!canCommand}>
          <Space wrap size="large">
            <Form.Item name="reason" label={t('Reason', '理由')} rules={[{ required: true, message: t('A reason is required.', '理由必填。') }]}>
              <Input placeholder={t('why this epoch bump, for the audit row', '为何抬纪元——写进审计行')} style={{ minWidth: 280 }} />
            </Form.Item>
            <Form.Item name="tenantId" label={t('Tenant', '租户')} rules={[{ required: true, message: t('Tenant is required.', '租户必填。') }]}>
              <Input placeholder={t('tenant id', '租户 id')} />
            </Form.Item>
            <Form.Item name="gameId" label={t('Game', '游戏')} rules={[{ required: true, message: t('Game is required.', '游戏必填。') }]}>
              <Input placeholder={t('game id', '游戏 id')} />
            </Form.Item>
            <Form.Item name="roomId" label={t('Room', '房间')} rules={[{ required: true, message: t('Room is required.', '房间必填。') }]}>
              <Input placeholder={t('room id', '房间 id')} />
            </Form.Item>
            <Form.Item
              name="expectedEpoch"
              label={t('Expected epoch', '期望纪元')}
              rules={[{ required: true, message: t('The expected epoch is required.', '期望纪元必填。') }]}
            >
              <InputNumber min={1} placeholder={t('the epoch the lookup showed', '查询显示的纪元')} />
            </Form.Item>
            <Form.Item name="targetEpoch" label={t('Target epoch (optional)', '目标纪元（可选）')}>
              <InputNumber min={0} placeholder={t('empty = expected + 1', '留空 = 期望 + 1')} />
            </Form.Item>
            <Button type="primary" htmlType="submit" disabled={!canCommand}>
              {t('Bump epoch', '抬升纪元')}
            </Button>
          </Space>
        </Form>
        {bumpError !== null && <Alert type="error" showIcon message={bumpError} style={{ marginBottom: 12 }} />}
        {bump !== null && (
          <>
            <Space style={{ marginTop: 12 }}>
              <Tag color={ownershipColor[bump.ownershipStatus] ?? 'default'}>{bump.ownershipStatus}</Tag>
              <Tag color={routeSyncColor[bump.routeSyncStatus] ?? 'default'}>{bump.routeSyncStatus}</Tag>
              {bump.duplicate && <Tag color="gold">duplicate</Tag>}
            </Space>
            <ReplyRows reply={bump as unknown as Record<string, unknown>} />
            <Typography.Paragraph type="secondary" style={{ marginTop: 8 }}>
              {t('Receipt', '回执')} {bump.operationId}:{' '}
              {t('replaying it answers a duplicate with the recorded values, and the receipt query on the operations page answers for this id.',
                '重放它会以重复回答记录值，操作页上的回执查询也答这个 id。')}
            </Typography.Paragraph>
          </>
        )}
      </Card>
    </Card>
  );
}
