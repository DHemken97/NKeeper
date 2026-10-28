import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from './types'

const adminUser = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

function makeHost(name: string, overrides: Partial<Host> = {}): Host {
  return {
    id: `h-${name}`,
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

const hosts = [
  makeHost('app1', { tags: [{ name: 'site', value: 'DC-A' }, { name: 'env', value: 'test' }] }),
  makeHost('app2', { tags: [{ name: 'site', value: 'DC-A' }] }),
  makeHost('app3', {
    tags: [{ name: 'site', value: 'DC-B' }],
    services: [{ protocol: 'Ssh', port: null, label: 'Shell' }],
    credentials: [{ id: 'c-app3', label: null, username: 'admin', domain: 'CORP', notes: null, hasPassword: true, hasPrivateKey: false }],
  }),
  makeHost('node1', { services: [{ protocol: 'Kvm', port: null, label: null, rdpSecurity: null }] }),
  makeHost('locked', { tags: [{ name: 'site', value: 'DC-A' }], canManage: false }),
]

function renderAt(path: string, onBulk?: (body: Record<string, unknown>) => Host[]) {
  const bodies: Record<string, unknown>[] = []
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, adminUser),
    'GET /api/hosts': () => jsonResponse(200, hosts),
    'POST /api/hosts/bulk': (_url, init) => {
      const body = JSON.parse(String(init.body)) as Record<string, unknown>
      bodies.push(body)
      return jsonResponse(200, onBulk?.(body) ?? [])
    },
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: [path] })} />
    </AuthProvider>,
  )
  return { bodies, user: userEvent.setup() }
}

const row = (name: string) => screen.getByRole('button', { name: new RegExp(`^${name}`) })

describe('bulk changes', () => {
  it('Ctrl/⌘+click selects several hosts, whose tags and hypervisor change together', async () => {
    const { bodies, user } = renderAt('/', (body) =>
      (body.hostIds as string[]).map((id) => {
        const host = hosts.find((h) => h.id === id)!
        return { ...host, isVirtualMachine: true, hypervisorId: 'h-node1', tags: [...host.tags, { name: 'backup', value: 'nightly' }] }
      }),
    )

    await user.click(await screen.findByRole('button', { name: /^app1/ }))
    await user.keyboard('{Control>}')
    await user.click(row('app2'))
    await user.keyboard('{/Control}')

    const panel = await screen.findByRole('form', { name: '2 hosts selected' })
    expect(row('app1')).toHaveTextContent('(selected)')
    expect(row('app3')).not.toHaveTextContent('(selected)')

    await user.type(within(panel).getByLabelText('Name for tag change 1'), 'backup')
    await user.type(within(panel).getByLabelText('Value for tag change 1'), 'nightly')
    expect(within(panel).getByText('Changes 2 hosts')).toBeInTheDocument()
    await user.click(within(panel).getByRole('button', { name: 'Add a tag change' }))
    await user.selectOptions(within(panel).getByLabelText('Action for tag change 2'), 'remove')
    await user.type(within(panel).getByLabelText('Name for tag change 2'), 'env')
    expect(within(panel).getByText('1 host has it')).toBeInTheDocument()
    await user.selectOptions(within(panel).getByLabelText('Runs on'), 'node1')
    await user.click(within(panel).getByRole('button', { name: 'Apply to 2 hosts' }))

    expect(await within(panel).findByText('Updated 2 hosts.')).toBeInTheDocument()
    expect(bodies[0]).toEqual({
      hostIds: ['h-app1', 'h-app2'],
      addTags: [{ name: 'backup', value: 'nightly' }],
      setTags: [],
      removeTags: [{ name: 'env', value: null }],
      hypervisor: { hypervisorId: 'h-node1' },
    })

    await user.click(within(panel).getByRole('button', { name: 'Clear selection' }))
    expect(screen.queryByRole('form', { name: /hosts selected/ })).not.toBeInTheDocument()
  })

  it('adds services to every selected host, saying which already have them', async () => {
    const { bodies, user } = renderAt('/', (body) => (body.hostIds as string[]).map((id) => hosts.find((h) => h.id === id)!))

    await user.click(await screen.findByRole('button', { name: /^app1/ }))
    await user.keyboard('{Control>}')
    await user.click(row('app3'))
    await user.keyboard('{/Control}')
    const panel = await screen.findByRole('form', { name: '2 hosts selected' })

    await user.click(within(panel).getByRole('button', { name: 'Add service' }))
    expect(within(panel).getByLabelText('Type for service 1')).toHaveValue('Ssh')
    expect(within(panel).getByText('Adds it to 1 host')).toBeInTheDocument()
    // Each iDRAC has its own address, so it isn't offered here.
    expect(within(within(panel).getByLabelText('Type for service 1')).queryByRole('option', { name: /iDRAC/ })).not.toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Move service 1 up' })).not.toBeInTheDocument()

    await user.click(within(panel).getByRole('button', { name: 'Add service' }))
    await user.selectOptions(within(panel).getByLabelText('Type for service 2'), 'Rdp')
    await user.type(within(panel).getByLabelText('Port for service 2'), '3390')
    await user.selectOptions(within(panel).getByLabelText('Security for service 2'), 'Tls')
    expect(within(panel).getByText('Adds it to 2 hosts')).toBeInTheDocument()
    await user.click(within(panel).getByRole('button', { name: 'Apply to 2 hosts' }))

    expect(await within(panel).findByText('Updated 2 hosts.')).toBeInTheDocument()
    expect(bodies[0]).toEqual({
      hostIds: ['h-app1', 'h-app3'],
      addTags: [],
      setTags: [],
      removeTags: [],
      addServices: [
        { protocol: 'Ssh', port: null, label: null },
        { protocol: 'Rdp', port: 3390, label: null, rdpSecurity: 'Tls' },
      ],
    })
    expect(within(panel).getByText('No services to add.')).toBeInTheDocument()
  })

  it('removes services, and adds, changes and removes credentials on every selected host', async () => {
    const { bodies, user } = renderAt('/', (body) => (body.hostIds as string[]).map((id) => hosts.find((h) => h.id === id)!))

    await user.click(await screen.findByRole('button', { name: /^app1/ }))
    await user.keyboard('{Control>}')
    await user.click(row('app3'))
    await user.keyboard('{/Control}')
    const panel = await screen.findByRole('form', { name: '2 hosts selected' })

    await user.click(within(panel).getByRole('button', { name: 'Remove a service' }))
    expect(within(panel).getByLabelText('Type for service removal 1')).toHaveValue('Ssh')
    expect(within(panel).getByText('1 host has it')).toBeInTheDocument()

    await user.click(within(panel).getByRole('button', { name: 'Add a credential change' }))
    await user.selectOptions(within(panel).getByLabelText('Action for credential change 1'), 'change')
    await user.type(within(panel).getByLabelText('Username for credential change 1'), 'ADMIN')
    await user.type(within(panel).getByLabelText('New password'), 'n3w-secret')
    await user.click(within(panel).getByRole('button', { name: 'Add a credential change' }))
    await user.type(within(panel).getByLabelText('Username for credential change 2'), 'deploy')
    await user.type(within(panel).getByLabelText('Password'), 'd3ploy')
    expect(within(panel).getByText('Adds it to 2 hosts')).toBeInTheDocument()
    await user.click(within(panel).getByRole('button', { name: 'Add a credential change' }))
    await user.selectOptions(within(panel).getByLabelText('Action for credential change 3'), 'remove')
    await user.type(within(panel).getByLabelText('Username for credential change 3'), 'legacy')
    expect(within(panel).getByText('No selected host has it')).toBeInTheDocument()
    await user.click(within(panel).getByRole('button', { name: 'Apply to 2 hosts' }))

    expect(await within(panel).findByText('Updated 2 hosts.')).toBeInTheDocument()
    expect(bodies[0]).toEqual({
      hostIds: ['h-app1', 'h-app3'],
      addTags: [],
      setTags: [],
      removeTags: [],
      removeServices: [{ protocol: 'Ssh', port: null }],
      addCredentials: [{ label: null, username: 'deploy', domain: null, notes: null, password: 'd3ploy' }],
      updateCredentials: [{ username: 'ADMIN', domain: null, password: 'n3w-secret' }],
      removeCredentials: [{ username: 'legacy', domain: null }],
    })
    expect(within(panel).getByText('No credential changes.')).toBeInTheDocument()
  })

  it('Shift+click selects a range, in the order the tree shows', async () => {
    const { user } = renderAt('/')

    await user.click(await screen.findByRole('button', { name: /^app1/ }))
    await user.keyboard('{Shift>}')
    await user.click(row('app3'))
    await user.keyboard('{/Shift}')

    expect(await screen.findByRole('form', { name: '3 hosts selected' })).toBeInTheDocument()
    expect(row('node1')).not.toHaveTextContent('(selected)')
  })

  it('a group’s menu selects all its hosts, and hosts the user can’t change are left out', async () => {
    const { bodies, user } = renderAt('/?group=site', (body) => (body.hostIds as string[]).map((id) => hosts.find((h) => h.id === id)!))

    fireEvent.contextMenu(await screen.findByRole('button', { name: /DC-A/, expanded: true }))
    await user.click(screen.getByRole('menuitem', { name: /^Select 3 hosts/ }))

    const panel = await screen.findByRole('form', { name: '3 hosts selected' })
    expect(within(panel).getByText(/You can’t change 1 of them/)).toBeInTheDocument()
    await user.selectOptions(within(panel).getByLabelText('Action for tag change 1'), 'set')
    await user.type(within(panel).getByLabelText('Name for tag change 1'), 'site')
    await user.type(within(panel).getByLabelText('Value for tag change 1'), 'DC-C')
    await user.click(within(panel).getByRole('button', { name: 'Apply to 2 hosts' }))

    await waitFor(() => expect(bodies[0]).toMatchObject({ hostIds: ['h-app1', 'h-app2'], setTags: [{ name: 'site', value: 'DC-C' }] }))
  })
})
