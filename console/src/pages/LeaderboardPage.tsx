import {
  Alert,
  Button,
  Card,
  Descriptions,
  Form,
  Input,
  InputNumber,
  Select,
  Space,
  Table,
  Tag,
  Typography,
} from 'antd';
import { useState } from 'react';
import { createAdminClient, AdminError } from '../api/client';
import type {
  AdminLeaderboardTopReply,
  AdminLeaderboardEntryProto,
  AdminLeaderboardRankedEntryProto,
} from '../api/contract';
import { useAuth } from '../auth';

// The out-of-the-box leaderboard back office (FIX-WEB-13, on the FIX-WEB-12 read face,
// maintained through FIX-WEB-14's command face): every query names the read key only —
// tenant, game, board and the optional period and scope (empty spells the all-time
// global bucket on the wire) plus the RANK-01 subject type. The walk is a snapshot: each
// page carries the read version, a continuation resumes the same snapshot, and if the
// board changed underneath it the next page is refused with an explicit restart word —
// never a cross-version page. found=false on the rank and window faces is an answer, not
// an error. The maintenance commands (adjust, reset, rebuild — the RANK-08 GM
// corrections) sit on the gm floor: each carries the audited triple and the opaque
// version token a read named (prefilled here), and a board that changed since that read
// is refused with a reload word — never corrected onto a stale view.
const subjectOptions = [
  { value: 0, label: 'Player' },
  { value: 1, label: 'Streamer' },
  { value: 2, label: 'Faction' },
];

const restartWord = 'The board changed since this walk started — start a fresh read.';
const notFoundWord = 'That subject has no score on this board.';
const scopeWord = 'Your scope does not cover this board.';
const reloadWord = 'The board changed since your read version — reload the board and retry.';
const conflictWord = 'That operation id already answered a different projection — use a fresh one.';
const floorWord = 'Maintenance commands need the gm role.';

function describeError(err: unknown): string {
  if (err instanceof AdminError && err.status === 400 && err.message.includes('restart the read')) {
    return restartWord;
  }
  if (err instanceof AdminError && err.status === 400 && err.message.includes('reload the board')) {
    return reloadWord;
  }
  if (err instanceof AdminError && err.status === 409) {
    return conflictWord;
  }
  if (err instanceof AdminError && err.status === 403) {
    return scopeWord;
  }
  return 'The control plane refused or could not take this. Check the board key and try again.';
}

function readKey(values: Record<string, unknown>) {
  return {
    tenantId: String(values.tenantId ?? ''),
    gameId: String(values.gameId ?? ''),
    board: String(values.board ?? ''),
    periodId: String(values.periodId ?? ''),
    scopeId: String(values.scopeId ?? ''),
    subject: Number(values.subject ?? 0),
  };
}

export default function LeaderboardPage() {
  const { token, operator } = useAuth();
  const canCommand = operator?.role === 'gm';
  // The opaque version token the maintenance commands name as expected_version (WEB-14):
  // every successful read refreshes it into the command forms, so a correction names the
  // exact view the operator just saw.
  const [adjustForm] = Form.useForm();
  const [resetForm] = Form.useForm();
  const [top, setTop] = useState<AdminLeaderboardTopReply | null>(null);
  const [topEntries, setTopEntries] = useState<AdminLeaderboardEntryProto[]>([]);
  const [topValues, setTopValues] = useState<Record<string, unknown> | null>(null);
  const [topError, setTopError] = useState<string | null>(null);
  const [rank, setRank] = useState<{
    found: boolean;
    rank: number;
    score: number;
    readVersionUnixMs: number;
  } | null>(null);
  const [rankError, setRankError] = useState<string | null>(null);
  const [around, setAround] = useState<{
    found: boolean;
    entries: AdminLeaderboardRankedEntryProto[];
    readVersionUnixMs: number;
  } | null>(null);
  const [aroundError, setAroundError] = useState<string | null>(null);
  const [adjust, setAdjust] = useState<{ score: number; duplicate: boolean; readVersionUnixMs: number } | null>(null);
  const [adjustError, setAdjustError] = useState<string | null>(null);
  const [reset, setReset] = useState<{ duplicate: boolean; readVersionUnixMs: number } | null>(null);
  const [resetError, setResetError] = useState<string | null>(null);
  const [rebuild, setRebuild] = useState<{ entries: number; readVersionUnixMs: number } | null>(null);
  const [rebuildError, setRebuildError] = useState<string | null>(null);

  /** Feeds a fresh read version into the command forms — the token a correction names. */
  const pushVersion = (version: string) => {
    if (!canCommand || version.length === 0) return;
    adjustForm.setFieldsValue({ expectedVersion: version });
    resetForm.setFieldsValue({ expectedVersion: version });
  };

  const runTop = async (values: Record<string, unknown>, pageToken: string, append: boolean) => {
    setTopError(null);
    if (!append) {
      setTop(null);
      setTopEntries([]);
    }
    try {
      const reply = await createAdminClient(token ?? '').getLeaderboardTop({
        ...readKey(values),
        maxEntries: Number(values.maxEntries ?? 10),
        pageToken,
      });
      setTop(reply);
      setTopEntries((current) => (append ? [...current, ...reply.entries] : reply.entries));
      pushVersion(reply.readVersion);
    } catch (err) {
      // A refused continuation kills the walk: the operator restarts from the first page.
      setTop(null);
      setTopEntries([]);
      setTopError(describeError(err));
    }
  };

  const runRank = async (values: Record<string, unknown>) => {
    setRankError(null);
    setRank(null);
    try {
      const reply = await createAdminClient(token ?? '').getLeaderboardRank({
        ...readKey(values),
        subjectId: String(values.subjectId ?? ''),
      });
      setRank(reply);
      pushVersion(reply.readVersion);
    } catch (err) {
      setRankError(describeError(err));
    }
  };

  const runAround = async (values: Record<string, unknown>) => {
    setAroundError(null);
    setAround(null);
    try {
      const reply = await createAdminClient(token ?? '').getLeaderboardAround({
        ...readKey(values),
        subjectId: String(values.subjectId ?? ''),
        window: Number(values.window ?? 5),
      });
      setAround(reply);
      pushVersion(reply.readVersion);
    } catch (err) {
      setAroundError(describeError(err));
    }
  };

  /** The audited triple rides every command: the request id is minted per submit (each
   * submit is one operation; a replay answers Duplicate), the operator is the signed-in
   * identity, the reason is the operator's audit evidence. */
  const auditedTriple = () => ({
    requestId: crypto.randomUUID(),
    operator: operator?.operatorId ?? '',
  });

  const runAdjust = async (values: Record<string, unknown>) => {
    setAdjustError(null);
    setAdjust(null);
    try {
      const reply = await createAdminClient(token ?? '').adjustLeaderboardEntry({
        ...auditedTriple(),
        ...readKey(values),
        subjectId: String(values.subjectId ?? ''),
        delta: Number(values.delta ?? 0),
        expectedVersion: String(values.expectedVersion ?? ''),
        reason: String(values.reason ?? ''),
      });
      setAdjust(reply);
      pushVersion(reply.readVersion);
    } catch (err) {
      setAdjustError(describeError(err));
    }
  };

  const runReset = async (values: Record<string, unknown>) => {
    setResetError(null);
    setReset(null);
    try {
      const reply = await createAdminClient(token ?? '').resetLeaderboardEntry({
        ...auditedTriple(),
        ...readKey(values),
        subjectId: String(values.subjectId ?? ''),
        expectedVersion: String(values.expectedVersion ?? ''),
        reason: String(values.reason ?? ''),
      });
      setReset(reply);
      pushVersion(reply.readVersion);
    } catch (err) {
      setResetError(describeError(err));
    }
  };

  const runRebuild = async (values: Record<string, unknown>) => {
    setRebuildError(null);
    setRebuild(null);
    try {
      const reply = await createAdminClient(token ?? '').rebuildLeaderboard({
        ...auditedTriple(),
        ...readKey(values),
        reason: String(values.reason ?? ''),
      });
      setRebuild(reply);
      pushVersion(reply.readVersion);
    } catch (err) {
      setRebuildError(describeError(err));
    }
  };

  const keyFields = () => (
    <>
      <Form.Item name="tenantId" label="Tenant" rules={[{ required: true, message: 'Tenant is required.' }]}>
        <Input placeholder="tenant id" />
      </Form.Item>
      <Form.Item name="gameId" label="Game" rules={[{ required: true, message: 'Game is required.' }]}>
        <Input placeholder="game id" />
      </Form.Item>
      <Form.Item name="board" label="Board" rules={[{ required: true, message: 'Board is required.' }]}>
        <Input placeholder="board id" />
      </Form.Item>
      <Form.Item name="periodId" label="Period (optional)">
        <Input placeholder="empty = all time" />
      </Form.Item>
      <Form.Item name="scopeId" label="Scope (optional)">
        <Input placeholder="empty = global" />
      </Form.Item>
      <Form.Item name="subject" label="Subject type" initialValue={0}>
        <Select options={subjectOptions} style={{ minWidth: 120 }} />
      </Form.Item>
    </>
  );

  return (
    <Card title="Leaderboards">
      <Typography.Paragraph type="secondary">
        Leaderboard queries and maintenance. Empty period and scope name the all-time
        global bucket. A walk reads one snapshot: every page carries its read version,
        and a board that changes under the walk is refused with a restart word — start
        the walk over rather than mixing versions. found=false means no score, not a
        failure. The maintenance commands carry the version token a read named back: a
        board that changed since that read is refused with a reload word, never corrected
        onto a stale view.
      </Typography.Paragraph>
      <Card title="Top N" style={{ marginBottom: 16 }}>
        <Form
          layout="vertical"
          name="leaderboard-top"
          onFinish={(values) => {
            setTopValues(values);
            return runTop(values, '', false);
          }}
        >
          <Space wrap size="large">
            {keyFields()}
            <Form.Item name="maxEntries" label="Max entries" initialValue={10}>
              <InputNumber min={1} max={1000} />
            </Form.Item>
            <Button type="primary" htmlType="submit">
              Read top
            </Button>
          </Space>
        </Form>
        {topError !== null && <Alert type="error" showIcon message={topError} style={{ marginTop: 12 }} />}
        {top !== null && (
          <>
            <Space style={{ marginTop: 12 }}>
              <Tag color="blue">read version {top.readVersionUnixMs}</Tag>
              {top.hasMore && topValues !== null && (
                <Button onClick={() => runTop(topValues, top.nextPageToken, true)}>
                  Continue
                </Button>
              )}
            </Space>
            <Table
              size="small"
              style={{ marginTop: 12 }}
              rowKey={(_, index) => String(index)}
              pagination={false}
              columns={[
                { title: '#', dataIndex: 'index', key: 'index', render: (_v, _r, i) => i + 1 },
                { title: 'Subject', dataIndex: 'subjectId', key: 'subjectId' },
                { title: 'Score', dataIndex: 'score', key: 'score' },
              ]}
              dataSource={topEntries}
            />
          </>
        )}
      </Card>
      <Card title="Rank" style={{ marginBottom: 16 }}>
        <Form layout="vertical" name="leaderboard-rank" onFinish={runRank}>
          <Space wrap size="large">
            {keyFields()}
            <Form.Item
              name="subjectId"
              label="Subject ID"
              rules={[{ required: true, message: 'The subject id is required.' }]}
            >
              <Input placeholder="subject id" />
            </Form.Item>
            <Button type="primary" htmlType="submit">
              Find rank
            </Button>
          </Space>
        </Form>
        {rankError !== null && <Alert type="error" showIcon message={rankError} style={{ marginTop: 12 }} />}
        {rank !== null && !rank.found && (
          <Alert type="info" showIcon message={notFoundWord} style={{ marginTop: 12 }} />
        )}
        {rank !== null && rank.found && (
          <>
            <Space style={{ marginTop: 12 }}>
              <Tag color="blue">read version {rank.readVersionUnixMs}</Tag>
            </Space>
            <Descriptions size="small" column={1} style={{ marginTop: 12 }}>
              <Descriptions.Item label="rank">{rank.rank}</Descriptions.Item>
              <Descriptions.Item label="score">{rank.score}</Descriptions.Item>
            </Descriptions>
          </>
        )}
      </Card>
      <Card title="Around" style={{ marginBottom: 16 }}>
        <Form layout="vertical" name="leaderboard-around" onFinish={runAround}>
          <Space wrap size="large">
            {keyFields()}
            <Form.Item
              name="subjectId"
              label="Subject ID"
              rules={[{ required: true, message: 'The subject id is required.' }]}
            >
              <Input placeholder="subject id" />
            </Form.Item>
            <Form.Item name="window" label="Window" initialValue={5}>
              <InputNumber min={1} max={50} />
            </Form.Item>
            <Button type="primary" htmlType="submit">
              Read window
            </Button>
          </Space>
        </Form>
        {aroundError !== null && (
          <Alert type="error" showIcon message={aroundError} style={{ marginTop: 12 }} />
        )}
        {around !== null && !around.found && (
          <Alert type="info" showIcon message={notFoundWord} style={{ marginTop: 12 }} />
        )}
        {around !== null && around.found && (
          <>
            <Space style={{ marginTop: 12 }}>
              <Tag color="blue">read version {around.readVersionUnixMs}</Tag>
            </Space>
            <Table
            size="small"
            style={{ marginTop: 12 }}
            rowKey={(entry) => `${entry.rank}:${entry.subjectId}`}
            pagination={false}
            columns={[
              { title: 'Rank #', dataIndex: 'rank', key: 'rank' },
              { title: 'Subject', dataIndex: 'subjectId', key: 'subjectId' },
              { title: 'Score', dataIndex: 'score', key: 'score' },
            ]}
            dataSource={around.entries}
          />
          </>
        )}
      </Card>
      <Card title="Maintenance" style={{ marginBottom: 16 }}>
        {!canCommand && (
          <Alert type="info" showIcon message={floorWord} style={{ marginTop: 12 }} />
        )}
        {canCommand && (
          <>
            <Card title="Adjustment" style={{ marginBottom: 16 }}>
              <Form layout="vertical" name="leaderboard-adjust" form={adjustForm} onFinish={runAdjust}>
                <Space wrap size="large">
                  {keyFields()}
                  <Form.Item
                    name="subjectId"
                    label="Subject ID"
                    rules={[{ required: true, message: 'The subject id is required.' }]}
                  >
                    <Input placeholder="subject id" />
                  </Form.Item>
                  <Form.Item name="delta" label="Delta">
                    <InputNumber />
                  </Form.Item>
                  <Form.Item name="expectedVersion" label="Expected version">
                    <Input placeholder="empty = no guard" style={{ minWidth: 260 }} />
                  </Form.Item>
                  <Form.Item
                    name="reason"
                    label="Reason"
                    rules={[{ required: true, message: 'The audit reason is required.' }]}
                  >
                    <Input placeholder="why this correction" style={{ minWidth: 200 }} />
                  </Form.Item>
                  <Button type="primary" htmlType="submit">
                    Apply adjustment
                  </Button>
                </Space>
              </Form>
              {adjustError !== null && (
                <Alert type="error" showIcon message={adjustError} style={{ marginTop: 12 }} />
              )}
              {adjust !== null && (
                <Space style={{ marginTop: 12 }} wrap>
                  <Tag color="blue">score {adjust.score}</Tag>
                  {adjust.duplicate && <Tag color="orange">duplicate</Tag>}
                  <Tag color="blue">read version {adjust.readVersionUnixMs}</Tag>
                </Space>
              )}
            </Card>
            <Card title="Reset" style={{ marginBottom: 16 }}>
              <Form layout="vertical" name="leaderboard-reset" form={resetForm} onFinish={runReset}>
                <Space wrap size="large">
                  {keyFields()}
                  <Form.Item name="subjectId" label="Subject ID">
                    <Input placeholder="empty = whole board" />
                  </Form.Item>
                  <Form.Item name="expectedVersion" label="Expected version">
                    <Input placeholder="empty = no guard" style={{ minWidth: 260 }} />
                  </Form.Item>
                  <Form.Item
                    name="reason"
                    label="Reason"
                    rules={[{ required: true, message: 'The audit reason is required.' }]}
                  >
                    <Input placeholder="why this reset" style={{ minWidth: 200 }} />
                  </Form.Item>
                  <Button type="primary" htmlType="submit">
                    Reset entry
                  </Button>
                </Space>
              </Form>
              {resetError !== null && (
                <Alert type="error" showIcon message={resetError} style={{ marginTop: 12 }} />
              )}
              {reset !== null && (
                <Space style={{ marginTop: 12 }} wrap>
                  {reset.duplicate && <Tag color="orange">duplicate</Tag>}
                  <Tag color="blue">read version {reset.readVersionUnixMs}</Tag>
                </Space>
              )}
            </Card>
            <Card title="Rebuild" style={{ marginBottom: 16 }}>
              <Form layout="vertical" name="leaderboard-rebuild" onFinish={runRebuild}>
                <Space wrap size="large">
                  {keyFields()}
                  <Form.Item
                    name="reason"
                    label="Reason"
                    rules={[{ required: true, message: 'The audit reason is required.' }]}
                  >
                    <Input placeholder="why this rebuild" style={{ minWidth: 200 }} />
                  </Form.Item>
                  <Button type="primary" htmlType="submit">
                    Rebuild board
                  </Button>
                </Space>
              </Form>
              {rebuildError !== null && (
                <Alert type="error" showIcon message={rebuildError} style={{ marginTop: 12 }} />
              )}
              {rebuild !== null && (
                <Space style={{ marginTop: 12 }} wrap>
                  <Tag color="blue">rebuilt {rebuild.entries} entries</Tag>
                  <Tag color="blue">read version {rebuild.readVersionUnixMs}</Tag>
                </Space>
              )}
            </Card>
          </>
        )}
      </Card>
    </Card>
  );
}
