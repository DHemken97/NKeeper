import { useEffect, useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router';
import { Button, ErrorBanner, Field, Input, Select } from '../components/ui.tsx';
import { api, ApiError, errorMessage } from '../lib/api.ts';
import type { AuthDriverInfo, AuthDriverType } from '../lib/types.ts';
import { useAuth } from './AuthContext.ts';

type Stage = { kind: 'credentials' } | { kind: 'totp'; mfaToken: string };

export default function LoginPage() {
  const { user, signIn, verifyTotp } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const returnTo = (location.state as { from?: string } | null)?.from ?? '/';

  const [drivers, setDrivers] = useState<AuthDriverInfo[]>([]);
  const [driver, setDriver] = useState<AuthDriverType>('LocalDatabase');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [stage, setStage] = useState<Stage>({ kind: 'credentials' });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api<AuthDriverInfo[]>('/api/auth/drivers')
      .then((list) => {
        setDrivers(list);
        if (list.length > 0 && !list.some((d) => d.type === 'LocalDatabase')) setDriver(list[0].type);
      })
      .catch((e) => setError(errorMessage(e)));
  }, []);

  if (user) return <Navigate to={returnTo} replace />;

  async function submitCredentials(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = await signIn(username, password, driver);
      setPassword('');
      if (result.status === 'MfaRequired') {
        setStage({ kind: 'totp', mfaToken: result.mfaToken });
      } else {
        navigate(returnTo, { replace: true });
      }
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function submitCode(event: FormEvent) {
    event.preventDefault();
    if (stage.kind !== 'totp') return;
    setBusy(true);
    setError(null);
    try {
      await verifyTotp(stage.mfaToken, code);
      navigate(returnTo, { replace: true });
    } catch (e) {
      setCode('');
      setError(errorMessage(e));
      // An expired mfa_pending token means starting over.
      if (e instanceof ApiError && /expired/i.test(e.message)) setStage({ kind: 'credentials' });
    } finally {
      setBusy(false);
    }
  }

  function backToCredentials() {
    setStage({ kind: 'credentials' });
    setCode('');
    setError(null);
  }

  return (
    <div className="flex min-h-full items-center justify-center px-4 py-12">
      <div className="w-full max-w-sm">
        <div className="mb-8 text-center">
          <h1 className="text-2xl font-semibold tracking-tight text-white">NKeeper</h1>
          <p className="mt-1 text-sm text-slate-400">Infrastructure access gateway</p>
        </div>

        <div className="rounded-xl border border-slate-800 bg-slate-900/60 p-6 shadow-xl">
          {error && <ErrorBanner message={error} />}

          {stage.kind === 'credentials' ? (
            <form onSubmit={submitCredentials} className="space-y-4">
              {drivers.length > 1 && (
                <Field label="Sign in with">
                  <Select value={driver} onChange={(e) => setDriver(e.target.value as AuthDriverType)}>
                    {drivers.map((d) => (
                      <option key={d.type} value={d.type}>
                        {d.displayName}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              <Field label="Username">
                <Input
                  autoFocus
                  autoComplete="username"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  placeholder={driver === 'ActiveDirectory' ? 'jdoe or CORP\\jdoe' : undefined}
                  required
                />
              </Field>
              <Field label="Password">
                <Input
                  type="password"
                  autoComplete="current-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                />
              </Field>
              <Button type="submit" className="w-full" disabled={busy}>
                {busy ? 'Signing in…' : 'Sign in'}
              </Button>
            </form>
          ) : (
            <form onSubmit={submitCode} className="space-y-4">
              <p className="text-sm text-slate-300">Enter the 6-digit code from your authenticator app.</p>
              <Field label="Verification code">
                <Input
                  autoFocus
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  maxLength={7}
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  className="text-center font-mono text-lg tracking-[0.4em]"
                  required
                />
              </Field>
              <Button type="submit" className="w-full" disabled={busy}>
                {busy ? 'Verifying…' : 'Verify'}
              </Button>
              <button
                type="button"
                className="w-full text-sm text-slate-400 hover:text-slate-200"
                onClick={backToCredentials}
              >
                Back
              </button>
            </form>
          )}
        </div>
      </div>
    </div>
  );
}
