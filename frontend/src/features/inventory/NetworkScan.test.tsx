import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { DiscoveredDevice, Host, NetworkScan } from './types'

const adminUser = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

const web01: Host = {
  id: 'h-web01',
  name: 'web01',
  hostname: null,
  ipAddress: '10.0.0.30',
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

const known: DiscoveredDevice = {
  ipAddress: '10.0.0.30',
  macAddress: null,
  vendor: null,
  isVirtual: false,
  hostname: null,
  names: [],
  operatingSystem: null,
  ttl: 64,
  respondedTo: ['ping'],
  ports: [{ port: 22, name: 'SSH', protocol: 'Ssh' }],
  details: [],
  hostId: 'h-web01',
  matchedHostName: 'web01',
  matchedBy: 'ip',
  newHost: null,
}

const pve: DiscoveredDevice = {
  ipAddress: '10.0.0.41',
  macAddress: 'BC:24:11:AA:BB:CC',
  vendor: 'Proxmox VE',
  isVirtual: true,
  hostname: 'app01.lab.example',
  names: ['app01.lab.example (reverse DNS)'],
  operatingSystem: 'Debian',
  ttl: 64,
  respondedTo: ['ping', 'arp'],
  ports: [
    { port: 22, name: 'SSH', protocol: 'Ssh' },
    { port: 3306, name: 'MySQL / MariaDB', protocol: null },
    { port: 8443, name: 'HTTPS (alternate)', protocol: 'Https' },
  ],
  details: ['SSH: SSH-2.0-OpenSSH_9.2p1 Debian-2', 'Web on port 8443: 200 · “App console”'],
  hostId: null,
  matchedHostName: null,
  matchedBy: null,
  newHost: { name: 'app01', hostname: 'app01.lab.example', ipAddress: '10.0.0.41', macAddress: 'BC:24:11:AA:BB:CC', description: 'Found by a network scan of 10.0.0.0/24 on 2026-11-09.' },
}

function scanOf(state: NetworkScan['state'], devices: DiscoveredDevice[], done: number): NetworkScan {
  return {
    id: 'scan1',
    subnet: '10.0.0.0/24',
    state,
    total: 254,
    done,
    devices,
    notes: state === 'finished' ? ['NKeeper runs in a container: unless it uses the host’s network, other machines’ MAC addresses aren’t visible to it.'] : [],
    startedAtUtc: '2026-11-09T10:00:00Z',
    finishedAtUtc: state === 'finished' ? '2026-11-09T10:01:00Z' : null,
    message: null,
  }
}

describe('Scan network', () => {
  it('warns that it is loud, needs the warning acknowledged, and adds the machines chosen as hosts', async () => {
    let started: unknown = null
    let created: Record<string, unknown> | null = null
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, adminUser),
      'GET /api/hosts': () => jsonResponse(200, [web01]),
      'GET /api/network-scan/latest': () => jsonResponse(204),
      'GET /api/network-scan/networks': () => jsonResponse(200, [{ subnet: '10.0.0.0/24', interface: 'eth0', address: '10.0.0.9' }]),
      'POST /api/network-scan': (_url, init) => {
        started = JSON.parse(String(init.body))
        return jsonResponse(200, scanOf('running', [known], 40))
      },
      'GET /api/network-scan/scan1': () => jsonResponse(200, scanOf('finished', [known, pve], 254)),
      'POST /api/hosts': (_url, init) => {
        created = JSON.parse(String(init.body)) as Record<string, unknown>
        return jsonResponse(201, { ...web01, id: 'h-app01', name: 'app01' })
      },
    })
    render(
      <AuthProvider>
        <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
      </AuthProvider>,
    )
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Scan network' }))
    const dialog = await screen.findByRole('dialog', { name: 'Scan network' })
    expect(within(dialog).getByText(/This scan is loud/)).toBeInTheDocument()
    expect(within(dialog).getByText(/honeypots and other security tools/)).toBeInTheDocument()
    expect(await within(dialog).findByLabelText('Subnet')).toHaveValue('10.0.0.0/24')

    const start = within(dialog).getByRole('button', { name: 'Start scan' })
    expect(start).toBeDisabled()
    await user.click(within(dialog).getByLabelText(/I understand this scan is loud/))
    await user.click(start)
    expect(started).toEqual({ subnet: '10.0.0.0/24', acknowledged: true })

    const results = await screen.findByRole('dialog', { name: 'Network scan of 10.0.0.0/24' })
    expect(within(results).getByText(/Scanned 40 of 254 addresses/)).toBeInTheDocument()
    // The progress is polled until the scan finishes.
    expect(await within(results).findByText('app01.lab.example', {}, { timeout: 4000 })).toBeInTheDocument()
    expect(within(results).getByText('Proxmox VE')).toBeInTheDocument()
    expect(within(results).getByText(/1 already in NKeeper/)).toBeInTheDocument()
    expect(within(results).getByText(/runs in a container/)).toBeInTheDocument()
    // A machine NKeeper already has can't be added again.
    expect(within(results).queryByLabelText('Add 10.0.0.30')).toBeNull()

    await user.click(within(results).getByRole('button', { name: /10\.0\.0\.41/ }))
    expect(within(results).getByText('Web on port 8443: 200 · “App console”')).toBeInTheDocument()

    await user.click(within(results).getByLabelText('Add 10.0.0.41'))
    await user.click(within(results).getByRole('button', { name: 'Add 1 host' }))
    expect(await within(results).findByText('Added app01.')).toBeInTheDocument()
    expect(created).toMatchObject({
      name: 'app01',
      hostname: 'app01.lab.example',
      ipAddress: '10.0.0.41',
      macAddress: 'BC:24:11:AA:BB:CC',
      isVirtualMachine: true,
      tags: [{ name: 'os', value: 'Debian' }],
      services: [
        { protocol: 'Ssh', port: null, label: null },
        { protocol: 'Https', port: 8443, label: null },
      ],
    })
  }, 15000)
})
