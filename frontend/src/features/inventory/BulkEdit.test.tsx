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
  makeHost('app3', { tags: [{ name: 'site', value: 'DC-B' }] }),
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
