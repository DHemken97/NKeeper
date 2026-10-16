import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import { buildIpGrid, ipGridCsv } from './ipGrid'

const user = { id: 'u1', userName: 'bob', email: 'bob@example.com', roles: [], mustChangePassword: false }

function host(id: string, name: string, ipAddress: string | null, hostname: string | null = null): Host {
  return {
    id,
    name,
    hostname: hostname ?? (ipAddress ? null : `${name}.example.com`),
    ipAddress,
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
    services: [],
    credentials: [],
    trustedKeys: [],
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
    version: 1,
    canManage: false,
  }
}

const hosts = [
  host('h1', 'router', '192.168.1.1'),
  host('h2', 'nas', '192.168.1.20', 'nas.home.lan'),
  host('h3', 'esx01', '10.0.1.10'),
  host('h4', 'lab-gw', '192.168.42.1'),
  host('h5', 'old-printer', '192.168.1.20'),
  host('h6', 'dns-only', null),
  host('h7', 'v6', '2001:db8::1'),
  host('h8', 'deep', '192.168.100.1'),
]

describe('IP address grid', () => {
  it('has one column per /24 subnet in numeric order, with each address’s hosts', () => {
    const grid = buildIpGrid(hosts)

    expect(grid.subnets).toEqual([
      { prefix: '10.0.1', used: 1 },
      { prefix: '192.168.1', used: 2 },
      { prefix: '192.168.42', used: 1 },
      { prefix: '192.168.100', used: 1 },
    ])
    expect(grid.at('192.168.1', 1).map((h) => h.name)).toEqual(['router'])
    expect(grid.at('192.168.1', 20).map((h) => h.name)).toEqual(['nas', 'old-printer'])
    expect(grid.at('192.168.1', 2)).toEqual([])
    expect([...grid.usedOctets].sort((a, b) => a - b)).toEqual([1, 10, 20])
  })

  it('exports a spreadsheet: a row per address .0–.255, quoting where needed', () => {
    const csv = ipGridCsv(buildIpGrid([...hosts, host('h9', 'web, "main"', '10.0.1.80')]))
    const rows = csv.trimEnd().split('\r\n')

    expect(rows).toHaveLength(257)
    expect(rows[0]).toBe('Address,10.0.1.x,192.168.1.x,192.168.42.x,192.168.100.x')
    expect(rows[1]).toBe('.0,,,,')
    expect(rows[2]).toBe('.1,,router,lab-gw,deep')
    expect(rows[21]).toBe('.20,,nas / old-printer,,')
    expect(rows[81]).toBe('.80,"web, ""main""",,,')
  })
})

describe('reports', () => {
  function renderAt(path: string) {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, user),
      'GET /api/hosts': () => jsonResponse(200, hosts),
    })
    const router = createMemoryRouter(routes, { initialEntries: [path] })
    render(
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>,
    )
    return { router, user: userEvent.setup() }
  }

  it('shows the IP address map: subnets across, addresses down, host names in the cells', async () => {
    renderAt('/reports')

    // Plain DOM queries: role queries over a 256-row table are slow enough to time out under load.
    const table = (await screen.findByRole('table')) as HTMLTableElement
    const headers = [...table.tHead!.rows[0].cells].map((h) => h.textContent)
    expect(headers).toEqual(['Address', '10.0.1.x1 used', '192.168.1.x2 used', '192.168.42.x1 used', '192.168.100.x1 used'])
    // .0 to .255
    const rows = table.tBodies[0].rows
    expect(rows).toHaveLength(256)
    expect(rows[1].cells[0].textContent).toBe('.1')
    expect([...rows[1].querySelectorAll('td')].map((c) => c.textContent)).toEqual(['', 'router', 'lab-gw', 'deep'])
    expect(screen.getByText(/2 hosts without an IPv4 address not shown/)).toBeInTheDocument()
  })

  it('flags two hosts on one address, hides unused rows on request, and links to the host', async () => {
    const { router, user: person } = renderAt('/reports/ip-addresses')

    const conflict = await screen.findByTitle('192.168.1.20 is used by 2 hosts')
    expect(conflict).toHaveTextContent('nasold-printer')

    await person.click(screen.getByRole('checkbox', { name: 'Only used addresses' }))
    expect((screen.getByRole('table') as HTMLTableElement).tBodies[0].rows).toHaveLength(3)

    await person.click(screen.getByRole('link', { name: 'esx01' }))
    expect(router.state.location.pathname).toBe('/')
    expect(router.state.location.search).toBe('?host=h3')
  })
})
