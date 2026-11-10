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

// The out-of-the-box leaderboard back office (FIX-WEB-13, on the FIX-WEB-12 read face):
// every query names the read key only — tenant, game, board and the optional period and
// scope (empty spells the all-time global bucket on the wire) plus the RANK-01 subject
// type. The walk is a snapshot: each page carries the read version, a continuation
// resumes the same snapshot, and if the board changed underneath it the next page is
// refused with an explicit restart word — never a cross-version page. found=false on
// the rank and window faces is an answer, not an error. Maintenance commands (reset,
// rebuild, GM adjustments) arrive with WEB-14.
const subjectOptions = [
  { value: 0, label: 'Player' },
  { value: 1, label: 'Streamer' },
  { value: 2, label: 'Faction' },
];

const restartWord = 'The board changed since this walk started — start a fresh read.';
const notFoundWord = 'That subject has no score on this board.';
const scopeWord = 'Your scope does not cover this board.';

function describeError(err: unknown): string {
  if (err instanceof AdminError && err.status === 400 && err.message.includes('restart the read')) {
    return restartWord;
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
  const { token } = useAuth();
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
    } catch (err) {
      setAroundError(describeError(err));
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
        Read-only leaderboard queries. Empty period and scope name the all-time global
        bucket. A walk reads one snapshot: every page carries its read version, and a
        board that changes under the walk is refused with a restart word — start the walk
        over rather than mixing versions. found=false means no score, not a failure.
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
    </Card>
  );
}
