import { Alert, Button, Card, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { AdminError } from '../api/client';
import { useAuth } from '../auth';
import { LanguageToggle, useI18n } from '../i18n';

// The operator login page (FIX-WEB-04). The credential is resolved against the
// who-am-i probe: 200 signs the operator in, 401 names a refused credential, and any
// other failure reads as the control plane being unreachable — the page never guesses
// which of the three it saw.
export default function LoginPage() {
  const { operator, login } = useAuth();
  const { t } = useI18n();
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (operator !== null) {
    return <Navigate to="/home" replace />;
  }

  const submit = async ({ token }: { token: string }) => {
    setBusy(true);
    setError(null);
    try {
      await login(token);
      navigate('/home', { replace: true });
    } catch (err) {
      setError(
        err instanceof AdminError && err.status === 401
          ? t('The control plane refused this credential.', '控制面拒绝了这份凭据。')
          : t('The control plane is unreachable. Try again shortly.',
            '控制面当前不可达，请稍后再试。'),
      );
      setBusy(false);
    }
  };

  return (
    <Card title={t('Operator login', '操作员登录')} style={{ maxWidth: 440, margin: '96px auto' }}>
      <Typography.Paragraph type="secondary">
        {t(
          'Sign in with an operator credential issued through the control plane’s operator store. The console resolves it against who-am-i and carries it as the bearer credential of every admin call.',
          '使用控制面操作员库签发的操作员凭据登录。控制台通过 who-am-i 解析凭据，并将其作为此后每个管理调用的 bearer 凭据。',
        )}
      </Typography.Paragraph>
      {error !== null && (
        <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} />
      )}
      <Form layout="vertical" onFinish={submit} disabled={busy}>
        <Form.Item
          name="token"
          label={t('Operator token', '操作员令牌')}
          rules={[{ required: true, message: t('Enter the operator token.', '请输入操作员令牌。') }]}
        >
          <Input.Password placeholder={t('bearer token', 'bearer 令牌')} autoFocus />
        </Form.Item>
        <Button type="primary" htmlType="submit" loading={busy} block>
          {t('Sign in', '登录')}
        </Button>
        <div style={{ marginTop: 12, textAlign: 'right' }}>
          <LanguageToggle />
        </div>
      </Form>
    </Card>
  );
}
