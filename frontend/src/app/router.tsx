import { Navigate, type RouteObject } from 'react-router'
import { AppLayout } from '../components/layout/AppLayout'
import { AdminRoute, ProtectedRoute, PublicOnlyRoute, SystemAdminRoute } from '../features/auth/components/RouteGuards'
import { ForgotPasswordPage } from '../features/auth/pages/ForgotPasswordPage'
import { LoginPage } from '../features/auth/pages/LoginPage'
import { IpAddressReport } from '../features/reports/pages/IpAddressReport'
import { ReportsLayout } from '../features/reports/pages/ReportsLayout'
import { VmResourcesReport } from '../features/reports/pages/VmResourcesReport'
import { HealthReport } from '../features/reports/pages/HealthReport'
import { InventoryReport } from '../features/reports/pages/InventoryReport'
import { DuplicatesReport } from '../features/reports/pages/DuplicatesReport'
import { InsuranceReport } from '../features/reports/pages/InsuranceReport'
import { ZabbixReport } from '../features/zabbix/pages/ZabbixReport'
import { AuditLogReport } from '../features/audit/pages/AuditLogReport'
import { RecordingPlayer } from '../features/audit/pages/RecordingPlayer'
import { TransferPage } from '../features/transfer/pages/TransferPage'
import { SettingsPage } from '../features/settings/pages/SettingsPage'
import { UsersPage } from '../features/users/pages/UsersPage'

/** Route table, kept separate from the router instance so tests can mount it in memory. */
export const routes: RouteObject[] = [
  {
    element: <PublicOnlyRoute />,
    children: [
      { path: '/login', element: <LoginPage /> },
      { path: '/forgot-password', element: <ForgotPasswordPage /> },
    ],
  },
  {
    element: <ProtectedRoute />,
    children: [
      {
        element: <AppLayout />,
        // The workspace is a single page; view state (selection, search, grouping) lives in the query string.
        // AppLayout renders it rather than this route, so its sessions stay open on the other pages.
        children: [
          { index: true, element: null },
          {
            path: '/reports',
            element: <ReportsLayout />,
            children: [
              { index: true, element: <Navigate to="ip-addresses" replace /> },
              { path: 'ip-addresses', element: <IpAddressReport /> },
              { path: 'vm-resources', element: <VmResourcesReport /> },
              { path: 'health', element: <HealthReport /> },
              { path: 'inventory', element: <InventoryReport /> },
              { path: 'duplicates', element: <DuplicatesReport /> },
              // Hidden: not in the list, and only for the system administrator.
              { element: <SystemAdminRoute />, children: [{ path: 'insurance', element: <InsuranceReport /> }] },
              {
                element: <AdminRoute />,
                children: [
                  { path: 'zabbix', element: <ZabbixReport /> },
                  { path: 'audit', element: <AuditLogReport /> },
                  { path: 'audit/recordings/:id', element: <RecordingPlayer /> },
                ],
              },
            ],
          },
          {
            element: <AdminRoute />,
            children: [
              { path: '/users', element: <UsersPage /> },
              { path: '/transfer', element: <TransferPage /> },
              { path: '/settings', element: <SettingsPage /> },
            ],
          },
        ],
      },
    ],
  },
  { path: '*', element: <Navigate to="/" replace /> },
]
