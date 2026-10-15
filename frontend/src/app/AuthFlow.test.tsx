import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { AuthProvider } from '../features/auth/components/AuthProvider'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../test/http'
import { routes } from './router'

const adminUser = { id: '6f1c0d4e-0000-4000-8000-000000000001', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

function renderApp(initialPath = '/') {
  const router = createMemoryRouter(routes, { initialEntries: [initialPath] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { router, user: userEvent.setup() }
}

describe('authentication flow', () => {
  it('verifies the session with the server and shows the login page when signed out', async () => {
    const fetchMock = mockFetch({ 'GET /api/auth/me': () => jsonResponse(401, { status: 401 }) })

    const { router } = renderApp('/')

    expect(await screen.findByRole('heading', { name: 'Sign in to NKeeper' })).toBeInTheDocument()
    expect(screen.getByText('Your remote connections, organized.')).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('shows the dashboard for an existing session', async () => {
    mockFetch({ 'GET /api/auth/me': () => jsonResponse(200, adminUser) })

    renderApp('/')

    expect(await screen.findByRole('heading', { name: 'Welcome to NKeeper' })).toBeInTheDocument()
  })

  it('signs in, lands on the dashboard, and signs out again', async () => {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(401, { status: 401 }),
      'POST /api/auth/login': () => jsonResponse(200, adminUser),
      'POST /api/auth/logout': () => jsonResponse(204),
    })
    const { user, router } = renderApp('/')

    await user.type(await screen.findByLabelText('Username or email'), 'admin')
    await user.type(screen.getByLabelText('Password'), 'correct-password')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { name: 'Welcome to NKeeper' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/')

    await user.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('heading', { name: 'Sign in to NKeeper' })).toBeInTheDocument()
  })

  it('offers a retry when the server cannot be reached', async () => {
    let calls = 0
    mockFetch({
      'GET /api/auth/me': () => {
        if (++calls === 1) {
          throw new TypeError('Failed to fetch')
        }
        return jsonResponse(401, { status: 401 })
      },
    })
    const { user } = renderApp('/')

    expect(await screen.findByRole('alert')).toHaveTextContent("couldn't reach the NKeeper server")
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('heading', { name: 'Sign in to NKeeper' })).toBeInTheDocument()
  })
})
