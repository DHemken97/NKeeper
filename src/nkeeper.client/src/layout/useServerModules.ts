import { useEffect, useState } from 'react';
import { api } from '../lib/api.ts';
import type { ServerModule } from '../lib/types.ts';

let cache: Promise<ServerModule[]> | null = null;

/** Server modules enabled on this host (GET /api/modules), fetched once per page load. */
export function useServerModules() {
  const [modules, setModules] = useState<ServerModule[]>([]);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    let active = true;
    cache ??= api<ServerModule[]>('/api/modules').catch((error) => {
      cache = null;
      throw error;
    });
    cache
      .then((list) => active && setModules(list))
      .catch(() => active && setModules([]))
      .finally(() => active && setLoaded(true));
    return () => {
      active = false;
    };
  }, []);

  return { modules, loaded };
}
