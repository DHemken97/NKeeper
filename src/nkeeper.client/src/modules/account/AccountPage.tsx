import { useState, type FormEvent } from 'react';
import { QRCodeSVG } from 'qrcode.react';
import { useAuth } from '../../auth/AuthContext.ts';
import { Button, Card, ErrorBanner, Field, Input, PageHeader } from '../../components/ui.tsx';
import { api, errorMessage } from '../../lib/api.ts';

interface TotpEnrollment {
  credentialId: string;
  secret: string;
  otpAuthUri: string;
}

function TotpEnrollmentCard({ onDone }: { onDone: () => Promise<void> }) {
  const [enrollment, setEnrollment] = useState<TotpEnrollment | null>(null);
  const [name, setName] = useState('Authenticator app');
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function begin() {
    setBusy(true);
    setError(null);
    try {
      setEnrollment(await api<TotpEnrollment>('/api/auth/mfa/totp/enroll', { method: 'POST', body: { name } }));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function confirm(event: FormEvent) {
    event.preventDefault();
    if (!enrollment) return;
    setBusy(true);
    setError(null);
    try {
      await api('/api/auth/mfa/totp/confirm', {
        method: 'POST',
        body: { credentialId: enrollment.credentialId, code },
      });
      setEnrollment(null);
      setCode('');
      await onDone();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card title="Add an authenticator app">
      {error && <ErrorBanner message={error} />}
      {!enrollment ? (
        <div className="flex flex-wrap items-end gap-3">
          <div className="min-w-56 flex-1">
            <Field label="Name">
              <Input value={name} onChange={(e) => setName(e.target.value)} maxLength={128} />
            </Field>
          </div>
          <Button onClick={begin} disabled={busy}>
            Start setup
          </Button>
        </div>
      ) : (
        <form onSubmit={confirm} className="grid gap-6 md:grid-cols-[auto_1fr]">
          <div className="rounded-lg bg-white p-3">
            <QRCodeSVG value={enrollment.otpAuthUri} size={176} />
          </div>
          <div className="space-y-4">
            <p className="text-sm text-slate-300">
              Scan the code with your authenticator app, or enter this key manually:
            </p>
            <code className="block break-all rounded-md bg-slate-950 px-3 py-2 font-mono text-sm text-sky-300">
              {enrollment.secret.match(/.{1,4}/g)?.join(' ')}
            </code>
            <Field label="Code from the app">
              <Input
                autoFocus
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={7}
                value={code}
                onChange={(e) => setCode(e.target.value)}
                className="max-w-40 font-mono tracking-[0.3em]"
                required
              />
            </Field>
            <div className="flex gap-2">
              <Button type="submit" disabled={busy}>
                Confirm
              </Button>
              <Button type="button" variant="ghost" onClick={() => setEnrollment(null)}>
                Cancel
              </Button>
            </div>
          </div>
        </form>
      )}
    </Card>
  );
}

export default function AccountPage() {
  const { user, refreshUser } = useAuth();
  if (!user) return null;

  return (
    <>
      <PageHeader title="Account & security" />

      <div className="grid max-w-3xl gap-6">
        <Card title="Profile">
          <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2 text-sm">
            <dt className="text-slate-400">Username</dt>
            <dd>{user.username}</dd>
            <dt className="text-slate-400">Display name</dt>
            <dd>{user.displayName ?? '—'}</dd>
            <dt className="text-slate-400">Email</dt>
            <dd>{user.email ?? '—'}</dd>
            <dt className="text-slate-400">Identity source</dt>
            <dd>{user.authSource}</dd>
            <dt className="text-slate-400">Role</dt>
            <dd>{user.isAdmin ? 'Administrator' : 'User'}</dd>
          </dl>
        </Card>

        <Card title="Second factors">
          {user.mfaCredentials.length === 0 ? (
            <p className="text-sm text-slate-400">None yet. Sign-in currently requires only a password.</p>
          ) : (
            <ul className="divide-y divide-slate-800">
              {user.mfaCredentials.map((c) => (
                <li key={c.id} className="flex items-baseline justify-between gap-4 py-2 text-sm">
                  <span>
                    <span className="font-medium text-slate-100">{c.name}</span>
                    <span className="ml-2 text-xs uppercase text-slate-500">{c.type}</span>
                  </span>
                  <span className="text-slate-400">
                    {c.lastUsedAtUtc ? `Last used ${new Date(c.lastUsedAtUtc).toLocaleString()}` : 'Never used'}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Card>

        <TotpEnrollmentCard onDone={refreshUser} />
      </div>
    </>
  );
}
