import {
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Row,
  Space,
  Spin,
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

// The streamers and matching page (FIX-WEB-08): the match plane's four read faces —
// the streamer roster, the match rules in force, the queue, and the matches. Each face
// answers independently, so one face's failure is that card's error, never a blank
// screen; the numbers are the faces' own answers shown as they are. The commands over
// these faces (join, leave, cancel, finish, set rules) are the admin group's POST
// routes and are not form-ed here yet.

interface StreamerProfileRow {
  id: string;
  displayName: string;
  platform: string;
  rating: number;
  games: number;
  wins: number;
  experience: number;
  influence: number;
  updatedAtMs: number;
}

interface MatchRulesRow {
  version: number;
  season: string;
  tierWidth: number;
  widenAfterMs: number;
  maxRatingGap: number;
  timeoutMs: number;
  fillBots: boolean;
  levelExperience: number;
  starExperience: number;
  rematchCooldownMs: number;
}

interface QueueRow {
  streamerId: string;
  mode: string;
  region: string;
  platform: string;
  joinedAtMs: number;
  status: string;
  reason: string;
}

interface MatchRow {
  id: string;
  status: string;
  mode: string;
  region: string;
  season: string;
  streamerIds: string[];
  roomIds: string[];
  ratingGap: number;
  crossTier: boolean;
  filledWithBots: boolean;
  createdAtMs: number;
  winnerId: string | null;
}

type Face<T> = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'data'; value: T };

interface Faces {
  streamers: Face<StreamerProfileRow[]>;
  rules: Face<MatchRulesRow>;
  queue: Face<QueueRow[]>;
  matches: Face<MatchRow[]>;
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

// Each face owns its JSON shape; a 200 that doesn't carry the face's own discriminators
// (an older control plane, a proxy interposing) is that card's error, never a page crash.
const faceGuards: Record<keyof Faces, (value: unknown) => boolean> = {
  streamers: (value) => Array.isArray(value) && value.every((row) => isRecord(row) && typeof (row as { id?: unknown }).id === 'string'),
  rules: (value) => isRecord(value) && typeof value.tierWidth === 'number',
  queue: (value) => Array.isArray(value) && value.every((row) => isRecord(row) && typeof (row as { streamerId?: unknown }).streamerId === 'string'),
  matches: (value) => Array.isArray(value) && value.every((row) => isRecord(row) && typeof (row as { id?: unknown }).id === 'string'),
};

export default function StreamersPage() {
  const { token } = useAuth();
  const { t } = useI18n();
  const [faces, setFaces] = useState<Faces>({
    streamers: { kind: 'loading' }, rules: { kind: 'loading' },
    queue: { kind: 'loading' }, matches: { kind: 'loading' },
  });

  const load = useCallback(async () => {
    if (token === null) return;
    setFaces({
      streamers: { kind: 'loading' }, rules: { kind: 'loading' },
      queue: { kind: 'loading' }, matches: { kind: 'loading' },
    });
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
      settle('/api/streamers', 'streamers', faceGuards.streamers),
      settle('/api/rules', 'rules', faceGuards.rules),
      settle('/api/queue', 'queue', faceGuards.queue),
      settle('/api/matches', 'matches', faceGuards.matches),
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
          {t('Each card answers from its own match face.', '每张卡片各自应答于自己的匹配面。')}
        </Typography.Text>
      </Space>
      <Row gutter={[16, 16]}>
        <Col xs={24} xl={14}>
          {faceCard(t('Streamer profiles', '主播档案'), faces.streamers as Face<never>, (value) => {
            const streamers = value as StreamerProfileRow[];
            return (
              <Table
                size="small"
                rowKey="id"
                pagination={false}
                dataSource={streamers}
                columns={[
                  { title: t('Streamer', '主播'), dataIndex: 'id' },
                  { title: t('Name', '名称'), dataIndex: 'displayName' },
                  { title: t('Platform', '平台'), dataIndex: 'platform' },
                  { title: t('Rating', '评分'), dataIndex: 'rating' },
                  { title: t('Games / Wins', '局数 / 胜场'), render: (_, streamer) => `${streamer.games} / ${streamer.wins}` },
                  { title: t('Experience', '经验'), dataIndex: 'experience' },
                  { title: t('Influence', '影响力'), dataIndex: 'influence' },
                  { title: t('Updated', '更新于'), render: (_, streamer) => new Date(streamer.updatedAtMs).toLocaleString() },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24} xl={10}>
          {faceCard(t('Match rules', '匹配规则'), faces.rules as Face<never>, (value) => {
            const rules = value as MatchRulesRow;
            return (
              <Descriptions size="small" column={1} bordered>
                <Descriptions.Item label={t('Season', '赛季')}>{rules.season}</Descriptions.Item>
                <Descriptions.Item label={t('Version', '版本')}>{rules.version}</Descriptions.Item>
                <Descriptions.Item label={t('Tier width', '分段宽度')}>{rules.tierWidth}</Descriptions.Item>
                <Descriptions.Item label={t('Widen after', '扩宽等待')}>{`${rules.widenAfterMs / 1000}s`}</Descriptions.Item>
                <Descriptions.Item label={t('Max rating gap', '评分差上限')}>{rules.maxRatingGap}</Descriptions.Item>
                <Descriptions.Item label={t('Queue timeout', '排队超时')}>{`${rules.timeoutMs / 1000}s`}</Descriptions.Item>
                <Descriptions.Item label={t('Fill bots', '机器人补位')}>{rules.fillBots ? t('yes', '是') : t('no', '否')}</Descriptions.Item>
                <Descriptions.Item label={t('Level experience', '等级经验')}>{rules.levelExperience}</Descriptions.Item>
                <Descriptions.Item label={t('Star experience', '星级经验')}>{rules.starExperience}</Descriptions.Item>
                <Descriptions.Item label={t('Rematch cooldown', '再战冷却')}>{`${rules.rematchCooldownMs / 1000}s`}</Descriptions.Item>
              </Descriptions>
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Queue', '排队'), faces.queue as Face<never>, (value) => {
            const queue = value as QueueRow[];
            return (
              <Table
                size="small"
                rowKey="streamerId"
                pagination={false}
                dataSource={queue}
                columns={[
                  { title: t('Streamer', '主播'), dataIndex: 'streamerId' },
                  { title: t('Placement', '安放'), render: (_, entry) => `${entry.platform} / ${entry.mode} / ${entry.region}` },
                  { title: t('Joined', '加入于'), render: (_, entry) => new Date(entry.joinedAtMs).toLocaleString() },
                  { title: t('Status', '状态'), dataIndex: 'status', render: (status: string) => <Tag>{status}</Tag> },
                  { title: t('Reason', '原因'), dataIndex: 'reason' },
                ]}
              />
            );
          })}
        </Col>
        <Col xs={24} xl={12}>
          {faceCard(t('Matches', '对局'), faces.matches as Face<never>, (value) => {
            const matches = value as MatchRow[];
            return (
              <Table
                size="small"
                rowKey="id"
                pagination={false}
                dataSource={matches}
                columns={[
                  { title: t('Match', '对局'), dataIndex: 'id' },
                  { title: t('Status', '状态'), dataIndex: 'status', render: (status: string) => <Tag>{status}</Tag> },
                  { title: t('Placement', '安放'), render: (_, match) => `${match.mode} / ${match.region} / ${match.season}` },
                  { title: t('Streamers', '主播'), render: (_, match) => match.streamerIds.join(', ') },
                  { title: t('Rooms', '房间'), render: (_, match) => match.roomIds.join(', ') },
                  { title: t('Rating gap', '评分差'), render: (_, match) => `${match.ratingGap}${match.crossTier ? ` (${t('cross tier', '跨段')})` : ''}${match.filledWithBots ? ` (${t('bots', '机器人')})` : ''}` },
                  { title: t('Created', '创建于'), render: (_, match) => new Date(match.createdAtMs).toLocaleString() },
                  { title: t('Winner', '胜者'), dataIndex: 'winnerId', render: (winner: string | null) => winner ?? '—' },
                ]}
              />
            );
          })}
        </Col>
      </Row>
    </Space>
  );
}
