import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { decodeGroupKey } from '../inventory/grouping'
import type { Host, HostCredential, HostTag } from '../inventory/types'
import { buildInventory, inventoryCsv } from './inventoryCounts'

const user = { id: 'u1', userName: 'bob', email: 'bob@example.com', roles: [], mustChangePassword: false }

function host(id: string, name: string, tags: HostTag[], fields: Partial<Host> = {}): Host {
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
    tags,
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

const tag = (name: string, value: string | null = null): HostTag => ({ name, value })
const login = (username: string, domain: string | null = null): HostCredential => ({
  id: `c-${username}`,
  label: null,
  username,
  domain,
  notes: null,
  hasPassword: true,
  hasPrivateKey: false,
})

const hosts = [
  host('h1', 'sql01', [tag('site', 'DC-A'), tag('role', 'sql'), tag('os', 'Windows Server 2022')], {
    services: [{ protocol: 'Rdp', port: null, label: null }],
    credentials: [login('admin', 'CORP'), login('Admin', 'corp')],
  }),
  host('h2', 'sql02', [tag('Site', 'dc-a'), tag('role', 'sql'), tag('OS', 'windows server 2022')], {
    isVirtualMachine: true,
    services: [{ protocol: 'Rdp', port: null, label: null }],
  }),
  host('h3', 'web01', [tag('site', 'DC-A'), tag('role', 'web'), tag('role', 'proxy'), tag('os', 'Ubuntu 24.04')], {
    isVirtualMachine: true,
    services: [
      { protocol: 'Ssh', port: null, label: null },
      { protocol: 'Https', port: null, label: null },
    ],
    credentials: [login('root')],
  }),
  host('h4', 'web02', [tag('site', 'DC-B'), tag('role', 'web'), tag('os', 'Ubuntu 24.04')], {
    services: [{ protocol: 'Ssh', port: null, label: null }],
    credentials: [login('root')],
  }),
  host('h5', 'lab', [tag('role', 'web')]),
  host('h6', 'nas', []),
]

const rowsOf = (groups: string[]) =>
  buildInventory(hosts, groups.map(decodeGroupKey)).rows.map((r) => [...r.values.map((v) => v.label), r.hosts.length])

describe('inventory report', () => {
  it('counts hosts per value of one tag, case-insensitively, with the untagged last', () => {
    expect(rowsOf(['site'])).toEqual([
      ['DC-A', 3],
      ['DC-B', 1],
      ['No site', 2],
    ])
    expect(buildInventory(hosts, [{ kind: 'tag', name: 'site' }]).hostCount).toBe(6)
  })

  it('counts each combination of several tags, a host with two values under each', () => {
    expect(rowsOf(['site', 'role'])).toEqual([
      ['DC-A', 'proxy', 1],
      ['DC-A', 'sql', 2],
      ['DC-A', 'web', 1],
      ['DC-B', 'web', 1],
      ['No site', 'web', 1],
      ['No site', 'No role', 1],
    ])
  })

  it('counts by operating system, service, username, and physical or VM', () => {
    expect(rowsOf(['@os'])).toEqual([
      ['Ubuntu 24.04', 2],
      ['Windows Server 2022', 2],
      ['No operating system', 2],
    ])
    expect(rowsOf(['@service'])).toEqual([
      ['HTTPS', 1],
      ['RDP', 2],
      ['SSH', 2],
      ['No services', 2],
    ])
    expect(rowsOf(['@username'])).toEqual([
      ['CORP\\admin', 1],
      ['root', 2],
      ['No username', 3],
    ])
    expect(rowsOf(['@machine', '@os'])).toEqual([
      ['Physical', 'Ubuntu 24.04', 1],
      ['Physical', 'Windows Server 2022', 1],
      ['Physical', 'No operating system', 2],
      ['Virtual machine', 'Ubuntu 24.04', 1],
      ['Virtual machine', 'windows server 2022', 1],
    ])
  })

  it('searches the workspace for a row’s tag and OS values', () => {
    const report = buildInventory(hosts, ['site', '@service', '@os'].map(decodeGroupKey))
    expect(report.rows[0].search).toBe('site:DC-A os:"Ubuntu 24.04"')
  })

  it('is empty until a field is chosen', () => {
    expect(buildInventory(hosts, [])).toEqual({ keys: [], rows: [], hostCount: 0 })
  })

  it('exports one row per combination, with the hosts’ names', () => {
    const rows = inventoryCsv(buildInventory(hosts, ['site', 'role'].map(decodeGroupKey))).trimEnd().split('\r\n')

    expect(rows[0]).toBe('site,role,Hosts,Host names')
    expect(rows[2]).toBe('DC-A,sql,2,sql01; sql02')
    expect(rows.at(-1)).toBe(',,1,nas')
  })

  it('shows the counts for the fields in the URL, and adds a field from the picker', async () => {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, user),
      'GET /api/hosts': () => jsonResponse(200, hosts),
    })
    const router = createMemoryRouter(routes, { initialEntries: ['/reports/inventory?group=site'] })
    render(
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>,
    )

    const table = (await screen.findByRole('table')) as HTMLTableElement
    const text = (row: HTMLTableRowElement) => [...row.cells].map((c) => c.textContent)
    expect(text(table.tBodies[0].rows[0])).toEqual(['DC-A', '3', '50%'])
    expect(text(table.tFoot!.rows[0])).toEqual(['All hosts', '6', ''])
    expect(screen.getByText('3 combinations · 6 hosts')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '3' })).toHaveAttribute('href', '/?group=site&q=site%3ADC-A')

    const picker = screen.getByRole('combobox', { name: 'Add grouping level' })
    // The os tag is offered as a field, not as a tag.
    expect(within(picker).queryByRole('option', { name: 'Tag: os' })).not.toBeInTheDocument()
    await userEvent.selectOptions(picker, 'Operating system')

    expect(router.state.location.search).toBe('?group=site&group=%40os')
    expect(screen.getByRole('columnheader', { name: 'Operating system' })).toBeInTheDocument()
    expect(screen.getByText('4 combinations · 6 hosts')).toBeInTheDocument()
  })
})
