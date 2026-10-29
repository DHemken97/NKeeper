import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { User } from '../auth/types'
import type { Host } from './types'

const adminUser: User = {
  id: '6f1c0d4e-0000-4000-8000-000000000001',
  userName: 'admin',
  email: 'admin@example.com',
  roles: ['Administrator'],
  mustChangePassword: false,
  isDirectoryAccount: false,
  signInAccount: null,
}

function makeHost(id: string, name: string): Host {
  return {
    id,
    name,
    hostname: name,
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
    zabbixHostId: null,
    tags: [],
    services: [],
    credentials: [],
    trustedKeys: [],
    canManage: true,
    createdAtUtc: '2026-10-01T12:00:00Z',
    updatedAtUtc: '2026-10-01T12:00:00Z',
    version: 1,
  }
}

const hosts = [makeHost('h-fw01', 'fw01'), makeHost('h-sql01', 'sql01'), makeHost('h-sql02', 'sql02'), makeHost('h-web01', 'web01')]

function renderAt(path: string, routesOverride: Parameters<typeof mockFetch>[0] = {}) {
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, adminUser),
    'GET /api/hosts': () => jsonResponse(200, hosts),
    ...routesOverride,
  })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { router, user: userEvent.setup() }
}

const row = (name: string) => screen.getByRole('button', { name: new RegExp(`^${name}`) })

describe('keyboard', () => {
  it('makes the tree a single Tab stop and jumps to hosts by typing their names', async () => {
    const { user } = renderAt('/')
    await screen.findByRole('button', { name: /^fw01/ })
    const tree = screen.getByRole('list', { name: 'Hosts' })
    expect(within(tree).getAllByRole('button').filter((b) => b.tabIndex === 0)).toEqual([row('fw01')])

    row('fw01').focus()
    await user.keyboard('s')
    expect(row('sql01')).toHaveFocus()
    // Typed quickly, letters add up; the same letter again moves on.
    await user.keyboard('ql02')
    expect(row('sql02')).toHaveFocus()
    await new Promise((resolve) => setTimeout(resolve, 800))
    await user.keyboard('w')
    expect(row('web01')).toHaveFocus()
    expect(within(tree).getAllByRole('button').filter((b) => b.tabIndex === 0)).toEqual([row('web01')])
  })

  it('goes from the search box to the tree, and back with Ctrl+K', async () => {
    const { user } = renderAt('/')
    await screen.findByRole('button', { name: /^fw01/ })
    const search = screen.getByRole('searchbox', { name: 'Search hosts' })

    await user.click(search)
    await user.keyboard('{ArrowDown}')
    await waitFor(() => expect(row('fw01')).toHaveFocus())

    await user.keyboard('{Control>}k{/Control}')
    expect(search).toHaveFocus()
    await user.keyboard('web{Enter}')
    await waitFor(() => expect(row('web01')).toHaveFocus())
  })

  it('edits the selected host with Ctrl+E and saves with Ctrl+S', async () => {
    let saved = false
    const { user } = renderAt('/?host=h-sql01', {
      'PUT /api/hosts/h-sql01': () => {
        saved = true
        return jsonResponse(200, { ...hosts[1], version: 2 })
      },
    })
    await screen.findByRole('article', { name: 'sql01' })
    row('sql01').focus()

    await user.keyboard('{Control>}e{/Control}')
    const form = await screen.findByRole('form', { name: 'Edit sql01' })
    expect(within(form).getByLabelText(/^Name/)).toHaveFocus()

    await user.keyboard('{Control>}s{/Control}')
    await waitFor(() => expect(saved).toBe(true))
    expect(await screen.findByRole('article', { name: 'sql01' })).toBeInTheDocument()
  })

  it('selects a range with Shift+arrows, everything with Ctrl+A, and clears it with Escape', async () => {
    const { user } = renderAt('/?host=h-fw01')
    await screen.findByRole('article', { name: 'fw01' })
    row('fw01').focus()

    await user.keyboard('{Shift>}{ArrowDown}{ArrowDown}{/Shift}')
    expect(await screen.findByText(/3 hosts selected/)).toBeInTheDocument()

    await user.keyboard('{Control>}a{/Control}')
    expect(await screen.findByText(/4 hosts selected/)).toBeInTheDocument()

    await user.keyboard('{Escape}')
    expect(await screen.findByRole('article', { name: 'fw01' })).toBeInTheDocument()
  })

  it('asks before deleting the host under the focus with the Delete key', async () => {
    const { user, router } = renderAt('/', { 'DELETE /api/hosts/h-web01': () => jsonResponse(204) })
    await screen.findByRole('button', { name: /^web01/ })
    row('web01').focus()

    await user.keyboard('{Delete}')
    const confirm = await screen.findByRole('button', { name: 'Yes, delete' })
    expect(confirm).toHaveFocus()
    expect(router.state.location.search).toBe('?host=h-web01')

    await user.keyboard('{Enter}')
    await waitFor(() => expect(router.state.location.search).toBe(''))
    expect(screen.queryByRole('button', { name: /^web01/ })).not.toBeInTheDocument()
  })

  it('opens a new host with Alt+Shift+N and lists every shortcut with ?', async () => {
    const { user } = renderAt('/')
    await screen.findByRole('button', { name: /^fw01/ })
    row('fw01').focus()

    await user.keyboard('{Alt>}{Shift>}n{/Shift}{/Alt}')
    expect(await screen.findByRole('form', { name: 'New host' })).toBeInTheDocument()

    row('fw01').focus()
    await user.keyboard('?')
    const dialog = await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })
    expect(within(dialog).getByText('Close the session tab')).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})
