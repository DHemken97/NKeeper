import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from './types'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

function makeHost(overrides: Partial<Host>): Host {
  return {
    id: crypto.randomUUID(),
    name: 'host',
    hostname: null,
    ipAddress: '10.0.0.1',
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
    tags: [],
    services: [],
    credentials: [],
    trustedKeys: [],
    canManage: true,
    createdAtUtc: '2026-10-01T12:00:00Z',
    updatedAtUtc: '2026-10-01T12:00:00Z',
    version: 1,
    ...overrides,
  }
}

const bastion = makeHost({
  id: 'h-bastion',
  name: 'bastion',
  services: [{ protocol: 'Ssh', port: null, label: null }],
  credentials: [
    { id: 'c-ops', label: 'Ops key', username: 'ops', domain: null, notes: null, hasPassword: false, hasPrivateKey: true },
    { id: 'c-nouser', label: 'No user', username: null, domain: null, notes: null, hasPassword: true, hasPrivateKey: false },
  ],
})
// Not offered as a jump host: no SSH service.
const winbox = makeHost({ id: 'h-win', name: 'winbox', services: [{ protocol: 'Rdp', port: null, label: null }] })
// Not offered either: it is itself reached through a jump host.
const inner = makeHost({ id: 'h-inner', name: 'inner', jumpHostId: 'h-bastion', services: [{ protocol: 'Ssh', port: null, label: null }] })
const db01 = makeHost({
  id: 'h-db01',
  name: 'db01',
  services: [
    { protocol: 'Ssh', port: null, label: null },
    { protocol: 'Rdp', port: null, label: null },
  ],
  credentials: [{ id: 'c-root', label: null, username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  version: 7,
})

function renderAt(path: string, hosts: Host[], routesOverride: Parameters<typeof mockFetch>[0] = {}) {
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
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

describe('jump hosts', () => {
  it('offers only SSH hosts reached directly, and saves the jump host with one of its usable credentials', async () => {
    let body: Record<string, unknown> | undefined
    const { user } = renderAt('/?host=h-db01&edit=1', [bastion, winbox, inner, db01], {
      'PUT /api/hosts/h-db01': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, { ...db01, jumpHostId: 'h-bastion', jumpCredentialId: 'c-ops', version: 8 })
      },
    })

    const form = await screen.findByRole('form', { name: 'Edit db01' })
    const through = within(form).getByLabelText('Reach through')
    expect(within(through).getAllByRole('option').map((o) => o.textContent)).toEqual(['Directly (no jump host)', 'bastion'])

    await user.selectOptions(through, 'h-bastion')
    const signIn = within(form).getByLabelText('Sign in to bastion as')
    expect(within(signIn).getAllByRole('option').map((o) => o.textContent)).toEqual(['Its first usable credential', 'Ops key'])
    await user.selectOptions(signIn, 'c-ops')
    await user.click(within(form).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(body).toBeDefined())
    expect(body).toMatchObject({ jumpHostId: 'h-bastion', jumpCredentialId: 'c-ops', version: 7 })
  })

  it('clears the jump credential when the host goes back to connecting directly', async () => {
    let body: Record<string, unknown> | undefined
    const reached = { ...db01, jumpHostId: 'h-bastion', jumpCredentialId: 'c-ops' }
    const { user } = renderAt('/?host=h-db01&edit=1', [bastion, reached], {
      'PUT /api/hosts/h-db01': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, db01)
      },
    })

    const form = await screen.findByRole('form', { name: 'Edit db01' })
    await user.selectOptions(within(form).getByLabelText('Reach through'), '')
    await user.click(within(form).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(body).toBeDefined())
    expect(body).toMatchObject({ jumpHostId: null, jumpCredentialId: null })
  })

  it('shows the jump host in the details, and disables the sessions that can’t go through it', async () => {
    const reached = { ...db01, jumpHostId: 'h-bastion' }
    const { user, router } = renderAt('/?host=h-db01', [bastion, reached])

    const details = await screen.findByRole('article', { name: 'db01' })
    expect(within(details).getByText('Reached through')).toBeInTheDocument()

    fireEvent.contextMenu(screen.getByRole('button', { name: /db01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    expect(screen.getByRole('menuitem', { name: /^RDP.*Not through a jump host yet/ })).toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByRole('menuitem', { name: /^SSH/ })).not.toHaveAttribute('aria-disabled', 'true')
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' })

    await user.click(within(details).getByRole('button', { name: 'bastion' }))
    expect(router.state.location.search).toBe('?host=h-bastion')
  })
})
