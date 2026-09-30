import { useMemo } from 'react';
import { NavLink, Outlet } from 'react-router';
import { useAuth } from '../auth/AuthContext.ts';
import { LogoutIcon } from '../components/icons.tsx';
import { visibleModules } from '../modules/registry.ts';
import type { ClientModule } from '../modules/types.ts';
import { useServerModules } from './useServerModules.ts';

function NavItem({ module }: { module: ClientModule }) {
  const Icon = module.icon;
  return (
    <NavLink
      to={module.path}
      end={module.path === '/'}
      className={({ isActive }) =>
        'flex items-center gap-3 rounded-md px-3 py-2 text-sm transition ' +
        (isActive ? 'bg-slate-800 text-white' : 'text-slate-400 hover:bg-slate-800/60 hover:text-slate-100')
      }
    >
      <Icon className="size-4 shrink-0" />
      <span className="truncate">{module.title}</span>
    </NavLink>
  );
}

export default function AppShell() {
  const { user, signOut } = useAuth();
  const { modules: serverModules } = useServerModules();

  const nav = useMemo(() => {
    const ids = new Set(serverModules.map((m) => m.id));
    const withNav = visibleModules(ids, user?.isAdmin ?? false).filter((m) => m.nav);
    const byOrder = (a: ClientModule, b: ClientModule) => (a.nav ? a.nav.order : 0) - (b.nav ? b.nav.order : 0);
    return {
      main: withNav.filter((m) => m.nav && m.nav.section === 'main').sort(byOrder),
      footer: withNav.filter((m) => m.nav && m.nav.section === 'footer').sort(byOrder),
    };
  }, [serverModules, user?.isAdmin]);

  return (
    <div className="flex h-full">
      <aside className="flex w-56 shrink-0 flex-col border-r border-slate-800 bg-slate-900/40">
        <div className="px-4 py-4">
          <span className="text-lg font-semibold tracking-tight text-white">NKeeper</span>
        </div>

        <nav className="flex-1 space-y-1 px-2" aria-label="Modules">
          {nav.main.map((m) => (
            <NavItem key={m.id} module={m} />
          ))}
        </nav>

        <div className="space-y-1 border-t border-slate-800 px-2 py-3">
          {nav.footer.map((m) => (
            <NavItem key={m.id} module={m} />
          ))}
          <button
            type="button"
            onClick={signOut}
            className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm text-slate-400 transition hover:bg-slate-800/60 hover:text-slate-100"
          >
            <LogoutIcon className="size-4" />
            Sign out
          </button>
          <p className="truncate px-3 pt-2 text-xs text-slate-500" title={user?.username}>
            {user?.username} · {user?.authSource}
          </p>
        </div>
      </aside>

      <main className="min-w-0 flex-1 overflow-y-auto px-6 py-6">
        <Outlet />
      </main>
    </div>
  );
}
