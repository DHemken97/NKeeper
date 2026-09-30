import { Link } from 'react-router';
import { useAuth } from '../../auth/AuthContext.ts';
import { Card, PageHeader } from '../../components/ui.tsx';
import { useServerModules } from '../../layout/useServerModules.ts';

export default function DashboardPage() {
  const { user } = useAuth();
  const { modules } = useServerModules();
  const hasMfa = (user?.mfaCredentials.length ?? 0) > 0;

  return (
    <>
      <PageHeader
        title={`Welcome, ${user?.displayName ?? user?.username}`}
        description="One place for your infrastructure."
      />

      {!hasMfa && (
        <div className="mb-6 rounded-md border border-amber-800 bg-amber-950/50 px-4 py-3 text-sm text-amber-200">
          Your account has no second factor.{' '}
          <Link to="/account" className="font-medium underline underline-offset-2 hover:text-white">
            Set up an authenticator app
          </Link>{' '}
          before managing production systems.
        </div>
      )}

      <Card title="Installed modules">
        {modules.length === 0 ? (
          <p className="text-sm text-slate-400">No server modules are enabled.</p>
        ) : (
          <ul className="divide-y divide-slate-800">
            {modules.map((m) => (
              <li key={m.id} className="flex items-baseline justify-between gap-4 py-2">
                <span className="text-sm font-medium text-slate-100">{m.displayName}</span>
                <span className="text-right text-sm text-slate-400">{m.description}</span>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </>
  );
}
