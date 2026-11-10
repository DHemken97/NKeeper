import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import { findDuplicates, sameMachine } from './duplicates'

const user = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

function host(id: string, name: string, extra: Partial<Host> = {}): Host {
  return {
    id,
    name,
    hostname: null,
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
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
    version: 1,
    canManage: true,
    ...extra,
  }
}

describe('finding duplicate hosts', () => {
  it('decides by the first field both have: MAC, then IP, then hostname, then name', () => {
    // Same MAC: duplicates whatever their names.
    expect(sameMachine(host('a', 'web1', { macAddress: '00:11:22:33:44:55' }), host('b', 'other', { macAddress: '00-11-22-33-44-55' }))?.label).toBe(
      'MAC address',
    )
    // Different MACs: never duplicates, even with the same IP and name.
    expect(
      sameMachine(host('a', 'web1', { macAddress: '00:11:22:33:44:55', ipAddress: '10.0.0.1' }), host('b', 'web1', { macAddress: 'aa:bb:cc:dd:ee:ff', ipAddress: '10.0.0.1' })),
    ).toBeNull()
    // One has no MAC: the IP decides.
    expect(sameMachine(host('a', 'web1', { macAddress: '00:11:22:33:44:55', ipAddress: '10.0.0.1' }), host('b', 'x', { ipAddress: '10.0.0.1' }))?.label).toBe('IP address')
    // The hostname without its domain.
    expect(sameMachine(host('a', 'one', { hostname: 'sql01.corp.lan' }), host('b', 'two', { hostname: 'SQL01' }))?.label).toBe('hostname')
    // Different IPs: not duplicates, even with the same name.
    expect(sameMachine(host('a', 'web1', { ipAddress: '10.0.0.1' }), host('b', 'web1', { ipAddress: '10.0.0.2' }))).toBeNull()
    // Behind different jump hosts, the same private address is a different machine.
    expect(sameMachine(host('a', 'x', { ipAddress: '192.168.1.10', jumpHostId: 'j1' }), host('b', 'y', { ipAddress: '192.168.1.10' }))).toBeNull()
    expect(sameMachine(host('a', 'Web1'), host('b', 'web1'))?.label).toBe('name')
  })

  it('groups them, the host holding the most first', () => {
    const groups = findDuplicates([
      host('a', 'app01', { ipAddress: '10.0.0.5' }),
      host('b', 'APP01 old', { ipAddress: '10.0.0.5', macAddress: '00:11:22:33:44:55', services: [{ protocol: 'Ssh', port: 22, label: null }] }),
      host('c', 'app01-x', { macAddress: '00:11:22:33:44:55' }),
      host('d', 'unique', { ipAddress: '10.0.0.9' }),
    ])
    expect(groups).toHaveLength(1)
    expect(groups[0].hosts.map((h) => h.id)).toEqual(['b', 'a', 'c'])
    expect(groups[0].reasons.sort()).toEqual(['Same IP address 10.0.0.5', 'Same MAC address 00:11:22:33:44:55'])
  })
})

describe('duplicate hosts report', () => {
  it('combines the ticked hosts into the one kept, after asking', async () => {
    let merged: unknown = null
    const hosts = [host('a', 'app01', { ipAddress: '10.0.0.5' }), host('b', 'app01-old', { ipAddress: '10.0.0.5', services: [{ protocol: 'Ssh', port: 22, label: null }] })]
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, user),
      'GET /api/hosts': () => jsonResponse(200, merged ? [hosts[0]] : hosts),
      'POST /api/hosts/a/merge': (_url, init) => {
        merged = JSON.parse(init.body as string)
        return jsonResponse(200, hosts[0])
      },
    })
    const router = createMemoryRouter(routes, { initialEntries: ['/reports/duplicates'] })
    render(
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>,
    )
    const person = userEvent.setup()

    const group = await screen.findByRole('region', { name: 'Duplicates of app01-old' })
    expect(within(group).getByText('Same IP address 10.0.0.5')).toBeInTheDocument()
    // The richer host is offered as the one to keep; choose the other.
    await person.click(within(group).getByRole('radio', { name: 'Keep app01' }))
    expect(within(group).getByRole('checkbox', { name: 'Combine app01-old' })).toBeChecked()
    await person.click(within(group).getByRole('button', { name: 'Combine 1 into app01' }))

    const dialog = await screen.findByRole('dialog', { name: 'Combine into app01?' })
    await person.click(within(dialog).getByRole('button', { name: 'Combine and delete 1 host' }))

    expect(await screen.findByText('Combined 1 host into app01.')).toBeInTheDocument()
    expect(merged).toEqual({ sourceIds: ['b'] })
    expect(await screen.findByText('No duplicates found.')).toBeInTheDocument()
  })
})
