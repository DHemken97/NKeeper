import { Navigate, type RouteObject } from 'react-router'
import { AppLayout } from '../components/layout/AppLayout'
import { AdminRoute, ProtectedRoute, PublicOnlyRoute } from '../features/auth/components/RouteGuards'
import { ForgotPasswordPage } from '../features/auth/pages/ForgotPasswordPage'
import { LoginPage } from '../features/auth/pages/LoginPage'
import { WorkspacePage } from '../features/inventory/pages/WorkspacePage'
import { IpAddressReport } from '../features/reports/pages/IpAddressReport'
import { ReportsLayout } from '../features/reports/pages/ReportsLayout'
import { VmResourcesReport } from '../features/reports/pages/VmResourcesReport'
import { HealthReport } from '../features/reports/pages/HealthReport'
import { InventoryReport } from '../features/reports/pages/InventoryReport'
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
        children: [
          { index: true, element: <WorkspacePage /> },
          {
            path: '/reports',
            element: <ReportsLayout />,
            children: [
              { index: true, element: <Navigate to="ip-addresses" replace /> },
              { path: 'ip-addresses', element: <IpAddressReport /> },
              { path: 'vm-resources', element: <VmResourcesReport /> },
              { path: 'health', element: <HealthReport /> },
              { path: 'inventory', element: <InventoryReport /> },
              {
                element: <AdminRoute />,
                children: [
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
