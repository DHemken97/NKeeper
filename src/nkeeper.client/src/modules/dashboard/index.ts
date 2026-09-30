import { lazy } from 'react';
import { HomeIcon } from '../../components/icons.tsx';
import type { ClientModule } from '../types.ts';

const dashboard: ClientModule = {
  id: 'dashboard',
  title: 'Dashboard',
  path: '/',
  icon: HomeIcon,
  Component: lazy(() => import('./DashboardPage.tsx')),
  nav: { section: 'main', order: 0 },
};

export default dashboard;
