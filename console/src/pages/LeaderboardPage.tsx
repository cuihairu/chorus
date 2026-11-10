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
import { useI18n } from '../i18n';

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

type ErrorWords = {
  restart: string;
  reload: string;
  conflict: string;
  scope: string;
  generic: string;
};

function describeError(err: unknown, words: ErrorWords): string {
  if (err instanceof AdminError && err.status === 400 && err.message.includes('restart the read')) {
    return words.restart;
  }
  if (err instanceof AdminError && err.status === 400 && err.message.includes('reload the board')) {
    return words.reload;
  }
  if (err instanceof AdminError && err.status === 409) {
    return words.conflict;
  }
  if (err instanceof AdminError && err.status === 403) {
    return words.scope;
  }
  return words.generic;
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
  const { t } = useI18n();
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

  const errorWords: ErrorWords = {
    restart: t('The board changed since this walk started — start a fresh read.',
      '走板开始后板已变化——请重新起一条走板。'),
    reload: t('The board changed since your read version — reload the board and retry.',
      '板在你读到的版本之后已变化——请重读板后重试。'),
    conflict: t('That operation id already answered a different projection — use a fresh one.',
      '这个操作 id 已回答过另一个投影——请换一个新的。'),
    scope: t('Your scope does not cover this board.', '你的作用域不覆盖这块板。'),
    generic: t('The control plane refused or could not take this. Check the board key and try again.',
      '控制面拒绝了这条请求或无法受理。请检查板键后重试。'),
  };
  const notFoundWord = t('That subject has no score on this board.', '该主体在此板无分。');
  const floorWord = t('Maintenance commands need the gm role.', '维护命令需要 gm 角色。');
  const subjectOptions = [
    { value: 0, label: t('Player', '玩家') },
    { value: 1, label: t('Streamer', '主播') },
    { value: 2, label: t('Faction', '阵营') },
  ];

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
      setTopError(describeError(err, errorWords));
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
      setRankError(describeError(err, errorWords));
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
      setAroundError(describeError(err, errorWords));
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
      setAdjustError(describeError(err, errorWords));
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
      setResetError(describeError(err, errorWords));
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
      setRebuildError(describeError(err, errorWords));
    }
  };

  const keyFields = () => (
    <>
      <Form.Item name="tenantId" label={t('Tenant', '租户')} rules={[{ required: true, message: t('Tenant is required.', '租户必填。') }]}>
        <Input placeholder={t('tenant id', '租户 id')} />
      </Form.Item>
      <Form.Item name="gameId" label={t('Game', '游戏')} rules={[{ required: true, message: t('Game is required.', '游戏必填。') }]}>
        <Input placeholder={t('game id', '游戏 id')} />
      </Form.Item>
      <Form.Item name="board" label={t('Board', '板')} rules={[{ required: true, message: t('Board is required.', '板必填。') }]}>
        <Input placeholder={t('board id', '板 id')} />
      </Form.Item>
      <Form.Item name="periodId" label={t('Period (optional)', '周期（可选）')}>
        <Input placeholder={t('empty = all time', '留空 = 全时期')} />
      </Form.Item>
      <Form.Item name="scopeId" label={t('Scope (optional)', '作用域（可选）')}>
        <Input placeholder={t('empty = global', '留空 = 全局')} />
      </Form.Item>
      <Form.Item name="subject" label={t('Subject type', '主体类型')} initialValue={0}>
        <Select options={subjectOptions} style={{ minWidth: 120 }} />
      </Form.Item>
    </>
  );

  return (
    <Card title={t('Leaderboards', '排行榜')}>
      <Typography.Paragraph type="secondary">
        {t(
          'Leaderboard queries and maintenance. Empty period and scope name the all-time global bucket. A walk reads one snapshot: every page carries its read version, and a board that changes under the walk is refused with a restart word — start the walk over rather than mixing versions. found=false means no score, not a failure. The maintenance commands carry the version token a read named back: a board that changed since that read is refused with a reload word, never corrected onto a stale view.',
          '排行榜查询与维护。留空的周期与作用域即全时期全局桶。走板读的是一份快照：每页都带着它读到的版本，板在走板中途变化会被重启词拒绝——重头走而不是混版本。found=false 表示没有分数，不是失败。维护命令带着读回答的版本令牌：板在读之后变化过会被重读词拒绝，绝不在过期视图上修正。',
        )}
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
            <Form.Item name="maxEntries" label={t('Max entries', '条数上限')} initialValue={10}>
              <InputNumber min={1} max={1000} />
            </Form.Item>
            <Button type="primary" htmlType="submit">
              {t('Read top', '读榜首')}
            </Button>
          </Space>
        </Form>
        {topError !== null && <Alert type="error" showIcon message={topError} style={{ marginTop: 12 }} />}
        {top !== null && (
          <>
            <Space style={{ marginTop: 12 }}>
              <Tag color="blue">{t('read version', '读版本')} {top.readVersionUnixMs}</Tag>
              {top.hasMore && topValues !== null && (
                <Button onClick={() => runTop(topValues, top.nextPageToken, true)}>
                  {t('Continue', '继续')}
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
                { title: t('Subject', '主体'), dataIndex: 'subjectId', key: 'subjectId' },
                { title: t('Score', '分数'), dataIndex: 'score', key: 'score' },
              ]}
              dataSource={topEntries}
            />
          </>
        )}
      </Card>
      <Card title={t('Rank', '名次')} style={{ marginBottom: 16 }}>
        <Form layout="vertical" name="leaderboard-rank" onFinish={runRank}>
          <Space wrap size="large">
            {keyFields()}
            <Form.Item
              name="subjectId"
              label={t('Subject ID', '主体 id')}
              rules={[{ required: true, message: t('The subject id is required.', '主体 id 必填。') }]}
            >
              <Input placeholder={t('subject id', '主体 id')} />
            </Form.Item>
            <Button type="primary" htmlType="submit">
              {t('Find rank', '查名次')}
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
              <Tag color="blue">{t('read version', '读版本')} {rank.readVersionUnixMs}</Tag>
            </Space>
            <Descriptions size="small" column={1} style={{ marginTop: 12 }}>
              <Descriptions.Item label="rank">{rank.rank}</Descriptions.Item>
              <Descriptions.Item label="score">{rank.score}</Descriptions.Item>
            </Descriptions>
          </>
        )}
      </Card>
      <Card title={t('Around', '邻近窗口')} style={{ marginBottom: 16 }}>
        <Form layout="vertical" name="leaderboard-around" onFinish={runAround}>
          <Space wrap size="large">
            {keyFields()}
            <Form.Item
              name="subjectId"
              label={t('Subject ID', '主体 id')}
              rules={[{ required: true, message: t('The subject id is required.', '主体 id 必填。') }]}
            >
              <Input placeholder={t('subject id', '主体 id')} />
            </Form.Item>
            <Form.Item name="window" label={t('Window', '窗口')} initialValue={5}>
              <InputNumber min={1} max={50} />
            </Form.Item>
            <Button type="primary" htmlType="submit">
              {t('Read window', '读窗口')}
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
              <Tag color="blue">{t('read version', '读版本')} {around.readVersionUnixMs}</Tag>
            </Space>
            <Table
            size="small"
            style={{ marginTop: 12 }}
            rowKey={(entry) => `${entry.rank}:${entry.subjectId}`}
            pagination={false}
            columns={[
              { title: 'Rank #', dataIndex: 'rank', key: 'rank' },
              { title: t('Subject', '主体'), dataIndex: 'subjectId', key: 'subjectId' },
              { title: t('Score', '分数'), dataIndex: 'score', key: 'score' },
            ]}
            dataSource={around.entries}
          />
          </>
        )}
      </Card>
      <Card title={t('Maintenance', '维护')} style={{ marginBottom: 16 }}>
        {!canCommand && (
          <Alert type="info" showIcon message={floorWord} style={{ marginTop: 12 }} />
        )}
        {canCommand && (
          <>
            <Card title={t('Adjustment', '修正')} style={{ marginBottom: 16 }}>
              <Form layout="vertical" name="leaderboard-adjust" form={adjustForm} onFinish={runAdjust}>
                <Space wrap size="large">
                  {keyFields()}
                  <Form.Item
                    name="subjectId"
                    label={t('Subject ID', '主体 id')}
                    rules={[{ required: true, message: t('The subject id is required.', '主体 id 必填。') }]}
                  >
                    <Input placeholder={t('subject id', '主体 id')} />
                  </Form.Item>
                  <Form.Item name="delta" label={t('Delta', '增减')}>
                    <InputNumber />
                  </Form.Item>
                  <Form.Item name="expectedVersion" label={t('Expected version', '期望版本')}>
                    <Input placeholder={t('empty = no guard', '留空 = 不设守卫')} style={{ minWidth: 260 }} />
                  </Form.Item>
                  <Form.Item
                    name="reason"
                    label={t('Reason', '理由')}
                    rules={[{ required: true, message: t('The audit reason is required.', '审计理由必填。') }]}
                  >
                    <Input placeholder={t('why this correction', '为何做此修正')} style={{ minWidth: 200 }} />
                  </Form.Item>
                  <Button type="primary" htmlType="submit">
                    {t('Apply adjustment', '应用修正')}
                  </Button>
                </Space>
              </Form>
              {adjustError !== null && (
                <Alert type="error" showIcon message={adjustError} style={{ marginTop: 12 }} />
              )}
              {adjust !== null && (
                <Space style={{ marginTop: 12 }} wrap>
                  <Tag color="blue">{t('score', '分数')} {adjust.score}</Tag>
                  {adjust.duplicate && <Tag color="orange">duplicate</Tag>}
                  <Tag color="blue">{t('read version', '读版本')} {adjust.readVersionUnixMs}</Tag>
                </Space>
              )}
            </Card>
            <Card title={t('Reset', '重置')} style={{ marginBottom: 16 }}>
              <Form layout="vertical" name="leaderboard-reset" form={resetForm} onFinish={runReset}>
                <Space wrap size="large">
                  {keyFields()}
                  <Form.Item name="subjectId" label={t('Subject ID', '主体 id')}>
                    <Input placeholder={t('empty = whole board', '留空 = 整块板')} />
                  </Form.Item>
                  <Form.Item name="expectedVersion" label={t('Expected version', '期望版本')}>
                    <Input placeholder={t('empty = no guard', '留空 = 不设守卫')} style={{ minWidth: 260 }} />
                  </Form.Item>
                  <Form.Item
                    name="reason"
                    label={t('Reason', '理由')}
                    rules={[{ required: true, message: t('The audit reason is required.', '审计理由必填。') }]}
                  >
                    <Input placeholder={t('why this reset', '为何做此重置')} style={{ minWidth: 200 }} />
                  </Form.Item>
                  <Button type="primary" htmlType="submit">
                    {t('Reset entry', '重置条目')}
                  </Button>
                </Space>
              </Form>
              {resetError !== null && (
                <Alert type="error" showIcon message={resetError} style={{ marginTop: 12 }} />
              )}
              {reset !== null && (
                <Space style={{ marginTop: 12 }} wrap>
                  {reset.duplicate && <Tag color="orange">duplicate</Tag>}
                  <Tag color="blue">{t('read version', '读版本')} {reset.readVersionUnixMs}</Tag>
                </Space>
              )}
            </Card>
            <Card title={t('Rebuild', '重建')} style={{ marginBottom: 16 }}>
              <Form layout="vertical" name="leaderboard-rebuild" onFinish={runRebuild}>
                <Space wrap size="large">
                  {keyFields()}
                  <Form.Item
                    name="reason"
                    label={t('Reason', '理由')}
                    rules={[{ required: true, message: t('The audit reason is required.', '审计理由必填。') }]}
                  >
                    <Input placeholder={t('why this rebuild', '为何做此重建')} style={{ minWidth: 200 }} />
                  </Form.Item>
                  <Button type="primary" htmlType="submit">
                    {t('Rebuild board', '重建板')}
                  </Button>
                </Space>
              </Form>
              {rebuildError !== null && (
                <Alert type="error" showIcon message={rebuildError} style={{ marginTop: 12 }} />
              )}
              {rebuild !== null && (
                <Space style={{ marginTop: 12 }} wrap>
                  <Tag color="blue">{t('rebuilt', '重建')} {rebuild.entries} {t('entries', '条')}</Tag>
                  <Tag color="blue">{t('read version', '读版本')} {rebuild.readVersionUnixMs}</Tag>
                </Space>
              )}
            </Card>
          </>
        )}
      </Card>
    </Card>
  );
}
