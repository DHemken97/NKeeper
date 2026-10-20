import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { AuthProvider } from '../features/auth/components/AuthProvider'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../test/http'
import { routes } from './router'

const emptyInventory = { 'GET /api/hosts': () => jsonResponse(200, []) }

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
    const fetchMock = mockFetch({
      'GET /api/auth/me': () => jsonResponse(401, { status: 401 }),
      'GET /api/auth/options': () => jsonResponse(200, { singleSignOnName: null }),
    })

    const { router } = renderApp('/')

    expect(await screen.findByRole('heading', { name: 'Sign in to NKeeper' })).toBeInTheDocument()
    expect(screen.getByText('Your remote connections, organized.')).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(screen.queryByRole('button', { name: /Sign in with/ })).not.toBeInTheDocument()
  })

  it('shows the workspace for an existing session', async () => {
    mockFetch({ ...emptyInventory, 'GET /api/auth/me': () => jsonResponse(200, adminUser) })

    renderApp('/')

    expect(await screen.findByText('Your inventory is empty')).toBeInTheDocument()
  })

  it('signs in, lands on the workspace, and signs out again', async () => {
    mockFetch({
      ...antiforgeryRoute,
      ...emptyInventory,
      'GET /api/auth/me': () => jsonResponse(401, { status: 401 }),
      'POST /api/auth/login': () => jsonResponse(200, adminUser),
      'POST /api/auth/logout': () => jsonResponse(204),
    })
    const { user, router } = renderApp('/')

    await user.type(await screen.findByLabelText('Username or email'), 'admin')
    await user.type(screen.getByLabelText('Password'), 'correct-password')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('Your inventory is empty')).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/')

    await user.click(screen.getByRole('button', { name: /admin/ }))
    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))

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

describe('single sign-on', () => {
  it('offers the identity provider and explains a refusal', async () => {
    mockFetch({
      'GET /api/auth/me': () => jsonResponse(401, { status: 401 }),
      'GET /api/auth/options': () => jsonResponse(200, { singleSignOnName: 'Microsoft' }),
    })

    renderApp('/login?sso=refused')

    expect(await screen.findByRole('button', { name: 'Sign in with Microsoft' })).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('isn’t allowed to sign in')
  })
})

describe('email sign-in codes', () => {
  it('asks for the emailed code after the password, then signs in', async () => {
    let verifiedWith: unknown
    mockFetch({
      ...antiforgeryRoute,
      ...emptyInventory,
      'GET /api/auth/me': () => jsonResponse(401, { status: 401 }),
      'GET /api/auth/options': () => jsonResponse(200, { singleSignOnName: null }),
      'POST /api/auth/login': () => jsonResponse(202, { twoFactorRequired: true, codeSentTo: 'a•••@example.com' }),
      'POST /api/auth/two-factor': (_url, init) => {
        verifiedWith = JSON.parse(String(init.body))
        return jsonResponse(200, adminUser)
      },
    })
    const { user } = renderApp('/')

    await user.type(await screen.findByLabelText('Username or email'), 'admin')
    await user.type(screen.getByLabelText('Password'), 'correct-password')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { name: 'Check your email' })).toBeInTheDocument()
    expect(screen.getByText(/a•••@example.com/)).toBeInTheDocument()
    await user.type(screen.getByLabelText('Sign-in code'), '123 456 789')
    await user.click(screen.getByRole('button', { name: 'Verify' }))

    expect(await screen.findByText('Your inventory is empty')).toBeInTheDocument()
    expect(verifiedWith).toEqual({ code: '123 456 789' })
  })

  it('explains a wrong code and lets the user try again', async () => {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(401, { status: 401 }),
      'GET /api/auth/options': () => jsonResponse(200, { singleSignOnName: null }),
      'GET /api/auth/two-factor': () => jsonResponse(200, { twoFactorRequired: true, codeSentTo: 'a•••@example.com' }),
      'POST /api/auth/two-factor': () => jsonResponse(401, { status: 401, code: 'invalid_code' }),
    })
    const { user } = renderApp('/login?twoFactor=1')

    await user.type(await screen.findByLabelText('Sign-in code'), '000000000')
    await user.click(screen.getByRole('button', { name: 'Verify' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('That code isn’t right.')
    expect(screen.getByLabelText('Sign-in code')).toHaveValue('')
  })

  it('goes back to the password form when the sign-in expired', async () => {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(401, { status: 401 }),
      'GET /api/auth/options': () => jsonResponse(200, { singleSignOnName: null }),
      'GET /api/auth/two-factor': () => jsonResponse(200, { twoFactorRequired: true, codeSentTo: 'a•••@example.com' }),
      'POST /api/auth/two-factor': () => jsonResponse(401, { status: 401, code: 'challenge_expired' }),
    })
    const { user } = renderApp('/login?twoFactor=1')

    await user.type(await screen.findByLabelText('Sign-in code'), '123456789')
    await user.click(screen.getByRole('button', { name: 'Verify' }))

    expect(await screen.findByRole('heading', { name: 'Sign in to NKeeper' })).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Sign in again to get a new code.')
  })
})
