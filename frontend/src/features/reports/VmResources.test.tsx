import { render, screen, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import { buildVmResources, vmResourcesCsv } from './vmResources'

const user = { id: 'u1', userName: 'bob', email: 'bob@example.com', roles: [], mustChangePassword: false }

function host(id: string, name: string, fields: Partial<Host> = {}): Host {
  return {
    id,
    name,
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
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
    version: 1,
    canManage: false,
    ...fields,
  }
}

const vm = (id: string, name: string, hypervisorId: string | null, cpuCores: number | null, memoryMiB: number | null) =>
  host(id, name, { isVirtualMachine: true, hypervisorId, cpuCores, memoryMiB })

const hosts = [
  host('n1', 'node1', { cpuCores: 32, memoryMiB: 131072, serialNumber: 'ABC1234' }),
  vm('v2', 'web02', 'n1', 4, 8192),
  vm('v1', 'web01', 'n1', 4, 8192),
  vm('v3', 'db01', 'n1', 16, 65536),
  vm('v4', 'mystery', 'n1', null, null),
  host('n2', 'small', { cpuCores: 4, memoryMiB: 8192, serialNumber: 'XYZ9876' }),
  vm('v5', 'greedy', 'n2', 8, 4096),
  host('p1', 'nas', { cpuCores: 8, memoryMiB: 32768, serialNumber: 'NAS0001' }),
  vm('v6', 'orphan', null, 2, 2048),
]

describe('VM resources', () => {
  it('puts each VM under its host, with what they’re given in total', () => {
    const report = buildVmResources(hosts)

    expect(report.hypervisors.map((g) => [g.host.name, g.guests.map((v) => v.name), g.allocatedCpus, g.allocatedMemoryMiB, g.incomplete])).toEqual([
      ['node1', ['db01', 'mystery', 'web01', 'web02'], 24, 81920, 1],
      ['small', ['greedy'], 8, 4096, 0],
    ])
    expect(report.physical.map((h) => h.name)).toEqual(['nas'])
    expect(report.unplaced.map((h) => h.name)).toEqual(['orphan'])
  })

  it('exports one row per host, VMs after their host', () => {
    const rows = vmResourcesCsv(buildVmResources(hosts)).trimEnd().split('\r\n')

    expect(rows[0]).toBe(
      'Host,Kind,Runs on,Serial number,CPU cores / vCPUs,vCPUs allocated,% of host CPU,Memory (GiB),Memory allocated (GiB),% of host memory',
    )
    expect(rows[1]).toBe('node1,Hypervisor,,ABC1234,32,24,75,128,80,63')
    expect(rows[2]).toBe('db01,VM,node1,,16,,50,64,,50')
    expect(rows[3]).toBe('mystery,VM,node1,,,,,,,')
    expect(rows.at(-2)).toBe('nas,Physical,,NAS0001,8,,,32,,')
    expect(rows.at(-1)).toBe('orphan,VM (host unknown),,,2,,,2,,')
  })

  it('shows the grid: each VM host and its serial, its VMs beneath with their share, overcommitment flagged', async () => {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, user),
      'GET /api/hosts': () => jsonResponse(200, hosts),
    })
    render(
      <AuthProvider>
        <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/reports/vm-resources'] })} />
      </AuthProvider>,
    )

    const table = (await screen.findByRole('table')) as HTMLTableElement
    const text = (row: HTMLTableRowElement) => [...row.cells].map((c) => c.textContent)
    const node1 = table.tBodies[0].rows
    expect(text(node1[0])).toEqual(['node14 VMs', 'ABC1234', '32 cores', '24 (75%)+1 unknown', '128 GiB', '80 GiB (63%)'])
    expect(text(node1[1])).toEqual(['db01', '', '16 vCPUs', '50% of host', '64 GiB', '50% of host'])

    // 8 vCPUs on a 4-core host: twice what it has.
    const greedy = within(table.tBodies[1]).getByText('200% of host')
    expect(greedy).toHaveAttribute('title', 'More than the host has (overcommitted)')

    expect(within(table.tBodies[2]).getByRole('columnheader', { name: 'Physical servers without VMs' })).toBeInTheDocument()
    expect(text(table.tBodies[2].rows[1])).toEqual(['nas', 'NAS0001', '8 cores', '', '32 GiB', ''])
    expect(within(table.tBodies[3]).getByRole('link', { name: 'orphan' })).toHaveAttribute('href', '/?host=v6')
    expect(screen.getByText('2 VM hosts · 6 VMs · 1 other physical server')).toBeInTheDocument()
  })
})
