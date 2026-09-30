import { lazy } from 'react';
import { UserIcon } from '../../components/icons.tsx';
import type { ClientModule } from '../types.ts';

const account: ClientModule = {
  id: 'account',
  title: 'Account & security',
  path: '/account',
  icon: UserIcon,
  Component: lazy(() => import('./AccountPage.tsx')),
  nav: { section: 'footer', order: 0 },
};

export default account;
