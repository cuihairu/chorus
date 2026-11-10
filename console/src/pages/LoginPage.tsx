import { Alert, Button, Card, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { AdminError } from '../api/client';
import { useAuth } from '../auth';

// The operator login page (FIX-WEB-04). The credential is resolved against the
// who-am-i probe: 200 signs the operator in, 401 names a refused credential, and any
// other failure reads as the control plane being unreachable — the page never guesses
// which of the three it saw.
export default function LoginPage() {
  const { operator, login } = useAuth();
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
          ? 'The control plane refused this credential.'
          : 'The control plane is unreachable. Try again shortly.',
      );
      setBusy(false);
    }
  };

  return (
    <Card title="Operator login" style={{ maxWidth: 440, margin: '96px auto' }}>
      <Typography.Paragraph type="secondary">
        Sign in with an operator credential issued through the control plane&apos;s operator
        store. The console resolves it against who-am-i and carries it as the bearer
        credential of every admin call.
      </Typography.Paragraph>
      {error !== null && (
        <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} />
      )}
      <Form layout="vertical" onFinish={submit} disabled={busy}>
        <Form.Item
          name="token"
          label="Operator token"
          rules={[{ required: true, message: 'Enter the operator token.' }]}
        >
          <Input.Password placeholder="bearer token" autoFocus />
        </Form.Item>
        <Button type="primary" htmlType="submit" loading={busy} block>
          Sign in
        </Button>
      </Form>
    </Card>
  );
}
