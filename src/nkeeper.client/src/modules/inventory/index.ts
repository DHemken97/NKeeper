import { lazy } from 'react';
import { ServerIcon } from '../../components/icons.tsx';
import type { ClientModule } from '../types.ts';

const inventory: ClientModule = {
  id: 'inventory',
  title: 'Inventory',
  path: '/inventory',
  icon: ServerIcon,
  Component: lazy(() => import('./InventoryPage.tsx')),
  nav: { section: 'main', order: 10 },
  serverModule: true,
};

export default inventory;
