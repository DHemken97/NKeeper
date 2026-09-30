import type { ClientModule, ConnectionHandler } from './types.ts';
import dashboard from './dashboard/index.ts';
import inventory from './inventory/index.ts';
import account from './account/index.ts';

/** Every client module. Register new modules here. */
export const clientModules: ClientModule[] = [dashboard, inventory, account];

/** Modules visible to this user, given the server modules that are enabled. */
export function visibleModules(serverModuleIds: ReadonlySet<string>, isAdmin: boolean): ClientModule[] {
  return clientModules.filter(
    (m) => (!m.serverModule || serverModuleIds.has(m.id)) && (!m.adminOnly || isAdmin),
  );
}

export function findConnectionHandler(protocol: string): ConnectionHandler | undefined {
  for (const module of clientModules) {
    const handler = module.connectionHandlers?.find((h) => h.protocol === protocol);
    if (handler) return handler;
  }
  return undefined;
}
