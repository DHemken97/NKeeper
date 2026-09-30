import { Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router';
import LoginPage from './auth/LoginPage.tsx';
import RequireAuth from './auth/RequireAuth.tsx';
import AppShell from './layout/AppShell.tsx';
import { Spinner } from './components/ui.tsx';
import { clientModules } from './modules/registry.ts';

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        element={
          <RequireAuth>
            <AppShell />
          </RequireAuth>
        }
      >
        {clientModules.map(({ id, path, Component }) => {
          const element = (
            <Suspense fallback={<Spinner />}>
              <Component />
            </Suspense>
          );
          return path === '/'
            ? <Route key={id} index element={element} />
            : <Route key={id} path={`${path}/*`} element={element} />;
        })}
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
