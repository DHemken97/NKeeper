import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import { generatePassword, grantMatches, type UserDetails, type UserSummary } from './types'

const admin = { id: 'u-admin', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }
const bobUser = { id: 'u-bob', userName: 'bob', email: 'bob@example.com', roles: [], mustChangePassword: false }

function host(id: string, name: string, tags: Host['tags'] = [], canManage = true): Host {
  return {
    id,
    name,
    hostname: `${name}.example.com`,
    ipAddress: null,
    macAddress: null,
    description: null,
    isVirtualMachine: false,
    hypervisorId: null,
    virtualMachineId: null,
    hypervisorSeenAtUtc: null,
    cpuCores: null,
    memoryMiB: null,
    serialNumber: null,
    jumpHostId: null,
    jumpCredentialId: null,
    plugins: {},
    tags,
    services: [],
    credentials: [],
    trustedKeys: [],
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
    version: 1,
    canManage,
  }
}

const hosts = [
  host('h1', 'web01', [{ name: 'site', value: 'DC1' }]),
  host('h2', 'web02', [{ name: 'site', value: 'DC2' }]),
  host('h3', 'sql01', [{ name: 'site', value: 'dc1' }]),
]

const bobSummary: UserSummary = {
  id: 'u-bob',
  userName: 'bob',
  email: 'bob@example.com',
  role: 'User',
  isDisabled: false,
  mustChangePassword: false,
  isLockedOut: false,
  isDirectoryAccount: false,
  grantCount: 1,
  createdAtUtc: '2026-10-01T00:00:00Z',
}
const bobDetails: UserDetails = {
  ...bobSummary,
  grants: [{ id: 'g1', hostId: null, tagName: 'site', tagValue: 'DC1', level: 'Use' }],
}

function renderAt(path: string, me: object, extra: Parameters<typeof mockFetch>[0] = {}) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, me),
    'GET /api/hosts': () => jsonResponse(200, hosts),
    'GET /api/users': () => jsonResponse(200, [{ ...bobSummary, id: 'u-admin', userName: 'admin', role: 'Administrator' }, bobSummary]),
    'GET /api/users/u-bob': () => jsonResponse(200, bobDetails),
    ...extra,
  })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { router, fetchMock, user: userEvent.setup() }
}

describe('users page', () => {
  it('lets administrators grant a user groups and hosts, previewing what each covers', async () => {
    let sent: unknown
    const { user } = renderAt('/users?user=u-bob', admin, {
      'PUT /api/users/u-bob/grants': (_url, init) => {
        sent = JSON.parse(String(init.body))
        return jsonResponse(200, bobDetails)
      },
    })

    expect(await screen.findByRole('heading', { name: 'bob' })).toBeInTheDocument()
    const grants = screen.getByRole('list', { name: 'Access grants' })
    // site: DC1 matches web01 and sql01 (case-insensitive).
    expect(within(grants).getByText('2 hosts')).toBeInTheDocument()
    expect(screen.getByText('Can use 2 of 3 hosts.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Add host' }))
    await user.selectOptions(screen.getByLabelText('Host for grant 2'), 'h2')
    await user.selectOptions(screen.getByLabelText('Access level for grant 2'), 'Manage')
    expect(screen.getByText('Can use 3 of 3 hosts, manage 1.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Save access' }))

    await waitFor(() =>
      expect(sent).toEqual({
        grants: [
          { hostId: null, tagName: 'site', tagValue: 'DC1', level: 'Use' },
          { hostId: 'h2', tagName: null, tagValue: null, level: 'Manage' },
        ],
      }),
    )
    expect(await screen.findByText(/Access saved/)).toBeInTheDocument()
  })

  it('creates a user with a generated password', async () => {
    let sent: Record<string, unknown> | undefined
    const { user, router } = renderAt('/users', admin, {
      'POST /api/users': (_url, init) => {
        sent = JSON.parse(String(init.body))
        return jsonResponse(201, { ...bobDetails, id: 'u-carol', userName: 'carol' })
      },
    })

    await user.click(await screen.findByRole('button', { name: 'New user' }))
    const dialog = screen.getByRole('dialog', { name: 'New user' })
    await user.type(within(dialog).getByLabelText('Username'), 'carol')
    await user.type(within(dialog).getByLabelText('Email'), 'carol@example.com')
    await user.click(within(dialog).getByRole('button', { name: 'Generate' }))
    await user.click(within(dialog).getByRole('button', { name: 'Create user' }))

    await waitFor(() => expect(router.state.location.search).toBe('?user=u-carol'))
    expect(sent).toMatchObject({ userName: 'carol', email: 'carol@example.com', role: 'User', mustChangePassword: true })
    expect(String(sent?.password)).toMatch(/^(?=.*[A-Z])(?=.*[a-z])(?=.*\d).{16}$/)
  })

  it('is only for administrators', async () => {
    const { router } = renderAt('/users', bobUser, { 'GET /api/hosts': () => jsonResponse(200, [host('h1', 'web01', [], false)]) })

    await screen.findByRole('button', { name: /web01/ })
    expect(router.state.location.pathname).toBe('/')
    expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New host' })).not.toBeInTheDocument()
  })
})

describe('permissions in the workspace', () => {
  it('hides editing for hosts the user can only use', async () => {
    const { user } = renderAt('/?host=h1', bobUser, { 'GET /api/hosts': () => jsonResponse(200, [host('h1', 'web01', [], false)]) })

    expect(await screen.findByRole('article', { name: 'web01' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()

    // Even a hand-typed edit URL shows the details instead of the editor.
    await user.click(screen.getByRole('button', { name: /web01/ }))
    expect(screen.queryByRole('form')).not.toBeInTheDocument()
  })

  it('explains an empty tree to users without access', async () => {
    renderAt('/', bobUser, { 'GET /api/hosts': () => jsonResponse(200, []) })

    expect(await screen.findByText(/No hosts have been shared with you/, { selector: 'p:not([class*=sidebar])' })).toBeInTheDocument()
  })
})

describe('forced password change', () => {
  it('replaces the app until the user chooses a new password', async () => {
    let sent: unknown
    const { user } = renderAt('/', { ...bobUser, mustChangePassword: true }, {
      'POST /api/account/password': (_url, init) => {
        sent = JSON.parse(String(init.body))
        return jsonResponse(204)
      },
    })

    expect(await screen.findByRole('heading', { name: 'Choose a new password' })).toBeInTheDocument()
    await user.type(screen.getByLabelText('Current password'), 'Temp-Passw0rd-1')
    await user.type(screen.getByLabelText('New password'), 'Bob-Own-Passw0rd')
    await user.type(screen.getByLabelText('Confirm new password'), 'Bob-Own-Passw0rd-typo')
    await user.click(screen.getByRole('button', { name: 'Save password and continue' }))
    expect(screen.getByText('The passwords don’t match.')).toBeInTheDocument()

    await user.clear(screen.getByLabelText('Confirm new password'))
    await user.type(screen.getByLabelText('Confirm new password'), 'Bob-Own-Passw0rd')
    await user.click(screen.getByRole('button', { name: 'Save password and continue' }))

    expect(await screen.findByRole('list', { name: 'Hosts' })).toBeInTheDocument()
    expect(sent).toEqual({ currentPassword: 'Temp-Passw0rd-1', newPassword: 'Bob-Own-Passw0rd' })
  })

  it('shows a wrong current password next to the field', async () => {
    const { user } = renderAt('/', { ...bobUser, mustChangePassword: true }, {
      'POST /api/account/password': () => jsonResponse(400, { status: 400, errors: { currentPassword: ['Incorrect password.'] } }),
    })

    await user.type(await screen.findByLabelText('Current password'), 'wrong')
    await user.type(screen.getByLabelText('New password'), 'Bob-Own-Passw0rd')
    await user.type(screen.getByLabelText('Confirm new password'), 'Bob-Own-Passw0rd')
    await user.click(screen.getByRole('button', { name: 'Save password and continue' }))

    expect(await screen.findByText('Incorrect password.')).toBeInTheDocument()
  })
})

describe('helpers', () => {
  it('generates passwords that meet the policy', () => {
    for (let i = 0; i < 200; i++) {
      expect(generatePassword()).toMatch(/^(?=.*[A-Z])(?=.*[a-z])(?=.*\d).{16}$/)
    }
  })

  it('matches grants like the server', () => {
    expect(grantMatches({ hostId: null, tagName: 'SITE', tagValue: 'dc1' }, hosts[0])).toBe(true)
    expect(grantMatches({ hostId: null, tagName: 'site', tagValue: null }, hosts[1])).toBe(true)
    expect(grantMatches({ hostId: null, tagName: 'site', tagValue: 'DC1' }, hosts[1])).toBe(false)
    expect(grantMatches({ hostId: 'h2', tagName: null, tagValue: null }, hosts[1])).toBe(true)
  })
})
