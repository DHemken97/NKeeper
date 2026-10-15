import { Navigate, type RouteObject } from 'react-router'
import { AppLayout } from '../components/layout/AppLayout'
import { ProtectedRoute, PublicOnlyRoute } from '../features/auth/components/RouteGuards'
import { ForgotPasswordPage } from '../features/auth/pages/ForgotPasswordPage'
import { LoginPage } from '../features/auth/pages/LoginPage'
import { DashboardPage } from '../features/dashboard/pages/DashboardPage'

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
        children: [{ index: true, element: <DashboardPage /> }],
      },
    ],
  },
  { path: '*', element: <Navigate to="/" replace /> },
]
