import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { hostHealth, type Host, type ServiceCheck } from './types'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

function host(id: string, name: string, fields: Partial<Host> = {}): Host {
  return {
    id,
    name,
    hostname: null,
    ipAddress: '10.0.0.5',
    macAddress: null,
    description: null,
    isVirtualMachine: false,
    hypervisorId: null,
    virtualMachineId: null,
    hypervisorSeenAtUtc: null,
    cpuCores: null,
    memoryMiB: null,
    serialNumber: null,
    tags: [],
    services: [
      { protocol: 'Ssh', port: null, label: null },
      { protocol: 'Https', port: 8443, label: null },
    ],
    credentials: [],
    trustedKeys: [],
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
    version: 1,
    canManage: true,
    ...fields,
  }
}

const soon = new Date(Date.now() + 10 * 24 * 3600 * 1000 + 3600 * 1000).toISOString()
const later = new Date(Date.now() + 200 * 24 * 3600 * 1000).toISOString()

function check(hostId: string, protocol: ServiceCheck['protocol'], port: number, fields: Partial<ServiceCheck> = {}): ServiceCheck {
  return {
    hostId,
    protocol,
    port,
    address: null,
    isUp: true,
    latencyMs: 4,
    error: null,
    checkedAtUtc: '2026-10-17T09:00:00Z',
    downSinceUtc: null,
    certificateExpiresUtc: null,
    certificateSubject: null,
    ...fields,
  }
}

const web01 = host('h1', 'web01', { macAddress: 'AA:BB:CC:DD:EE:0F' })
const db01 = host('h2', 'db01')
const checks = [
  check('h1', 'Ssh', 22),
  check('h1', 'Https', 8443, { certificateExpiresUtc: soon, certificateSubject: 'CN=web01' }),
  check('h2', 'Ssh', 22, { isUp: false, latencyMs: null, error: 'Couldn’t connect (connection refused)', downSinceUtc: '2026-10-17T08:00:00Z' }),
  check('h2', 'Https', 8443, { certificateExpiresUtc: later, certificateSubject: 'CN=db01' }),
]

function renderAt(path: string, routesOverride: Parameters<typeof mockFetch>[0] = {}) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [web01, db01]),
    'GET /api/health': () => jsonResponse(200, checks),
    ...routesOverride,
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: [path] })} />
    </AuthProvider>,
  )
  return { fetchMock, user: userEvent.setup() }
}

describe('service health', () => {
  it('summarises a host as up, partly down or down', () => {
    expect(hostHealth(undefined)).toBeUndefined()
    expect(hostHealth([check('h', 'Ssh', 22)])).toBe('up')
    expect(hostHealth([check('h', 'Ssh', 22), check('h', 'Rdp', 3389, { isUp: false })])).toBe('partial')
    expect(hostHealth([check('h', 'Ssh', 22, { isUp: false })])).toBe('down')
  })

  it('marks hosts in the tree, and shows each service’s state and certificate in the details', async () => {
    let checked = false
    const { user } = renderAt('/?host=h1', {
      'POST /api/health/hosts/h1/check': () => {
        checked = true
        return jsonResponse(200, checks.slice(0, 2))
      },
    })

    const tree = await screen.findByRole('list', { name: 'Hosts' })
    // db01's SSH is down but its HTTPS answers.
    expect(await within(tree).findByTitle('Some services down')).toBeInTheDocument()
    expect(within(within(tree).getByRole('button', { name: /web01/ })).getByTitle('All services up')).toBeInTheDocument()

    const details = await screen.findByRole('article', { name: 'web01' })
    expect(within(details).getAllByText(/Up · 4 ms/)).toHaveLength(2)
    expect(within(details).getByText(/Certificate expires in 10 days/)).toBeInTheDocument()

    await user.click(within(details).getByRole('button', { name: 'Check now' }))
    await waitFor(() => expect(checked).toBe(true))
  })

  it('wakes a host from its details or its menu', async () => {
    let woken = 0
    const { user } = renderAt('/?host=h1', {
      'POST /api/hosts/h1/wake': () => {
        woken++
        return jsonResponse(200, { sentTo: ['255.255.255.255', '10.0.0.255'] })
      },
    })

    const details = await screen.findByRole('article', { name: 'web01' })
    await user.click(within(details).getByRole('button', { name: 'Wake' }))
    expect(await within(details).findByText(/Wake-on-LAN sent to AA:BB:CC:DD:EE:0F/)).toBeInTheDocument()

    fireEvent.contextMenu(within(await screen.findByRole('list', { name: 'Hosts' })).getByRole('button', { name: /web01/ }))
    await user.click(screen.getByRole('menuitem', { name: /^Wake/ }))
    await waitFor(() => expect(woken).toBe(2))
    // db01 has no MAC address: nothing to wake.
    fireEvent.contextMenu(within(screen.getByRole('list', { name: 'Hosts' })).getByRole('button', { name: /db01/ }))
    expect(screen.queryByRole('menuitem', { name: /^Wake/ })).not.toBeInTheDocument()
  })

  it('reports what’s down and which certificates expire first', async () => {
    renderAt('/reports/health')

    expect(await screen.findByText(/3 of 4 services up · 1 down · 1 certificate expiring within 30 days/)).toBeInTheDocument()
    const down = screen.getAllByRole('table')[0]
    expect(within(down).getByRole('link', { name: 'db01' })).toBeInTheDocument()
    expect(within(down).getByText('Couldn’t connect (connection refused)')).toBeInTheDocument()
    const certificates = screen.getAllByRole('table')[1]
    const rows = within(certificates).getAllByRole('row').slice(1)
    expect(rows.map((r) => within(r).getAllByRole('cell')[0].textContent)).toEqual(['web01', 'db01'])
    expect(rows[0]).toHaveTextContent('in 10 days')
  })
})
