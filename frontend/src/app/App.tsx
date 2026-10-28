import { useEffect } from 'react'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { AuthProvider } from '../features/auth/components/AuthProvider'
import { useAuth } from '../features/auth/context'
import { loadServerPlugins } from '../sdk/loader'
import { routes } from './router'

const router = createBrowserRouter(routes)

/** Loads the server's plugins once someone is signed in (their files need a session). */
function PluginLoader() {
  const { status } = useAuth()
  useEffect(() => {
    if (status === 'authenticated') {
      loadServerPlugins().catch((error: unknown) => console.error('The plugins couldn’t be listed.', error))
    }
  }, [status])
  return null
}

export function App() {
  return (
    <AuthProvider>
      <PluginLoader />
      <RouterProvider router={router} />
    </AuthProvider>
  )
}
