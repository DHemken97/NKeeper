import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import type { Host } from '../inventory/types'
import { ZabbixHostField } from './components/ZabbixHostField'
import { ZabbixSection } from './components/ZabbixSection'
import type { ZabbixHostOverview } from './zabbixApi'

const host: Host = {
  id: 'h1',
  name: 'pve1',
  hostname: null,
  ipAddress: '192.168.42.2',
  macAddress: null,
  description: null,
  isVirtualMachine: false,
  hypervisorId: null,
  virtualMachineId: null,
  hypervisorSeenAtUtc: null,
  cpuCores: 12,
  memoryMiB: null,
  serialNumber: null,
  jumpHostId: null,
  jumpCredentialId: null,
  zabbixHostId: '10084',
  tags: [],
  services: [],
  credentials: [],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 7,
  canManage: true,
}

const overview: ZabbixHostOverview = {
  host: {
    hostId: '10084',
    host: 'pve1',
    name: 'pve1',
    monitored: true,
    ipAddresses: ['192.168.42.2'],
    dnsNames: [],
    macAddresses: ['AA:BB:CC:00:00:01'],
    operatingSystem: 'Debian 12',
    tags: [{ name: 'role', value: 'hypervisor' }],
  },
  description: null,
  inMaintenance: false,
  availability: 'Available',
  interfaces: [{ type: 'Agent', ipAddress: '192.168.42.2', dns: null, port: '10050', useIp: true, main: true, availability: 'Available', error: null }],
  groups: ['Hypervisors'],
  inventory: { serialno_a: 'SN-PVE1', location: 'Rack 4' },
  items: [
    { key: 'system.uptime', name: 'System uptime', value: '3256722', units: 'uptime', checkedAtUtc: '2026-10-28T00:00:00Z' },
    { key: 'system.cpu.num', name: 'Number of CPUs', value: '12', units: '', checkedAtUtc: '2026-10-28T00:00:00Z' },
    { key: 'vm.memory.size[total]', name: 'Total memory', value: '17179869184', units: 'B', checkedAtUtc: '2026-10-28T00:00:00Z' },
  ],
  problems: [{ eventId: '9001', name: 'High CPU utilization', severity: 4, startedAtUtc: '2026-10-27T10:00:00Z', acknowledged: true, suppressed: false }],
}

const statusRoute = { 'GET /api/zabbix/status': () => jsonResponse(200, { enabled: true, frontendUrl: 'https://zabbix.example.com/' }) }

describe('Zabbix in host details', () => {
  it('shows what Zabbix knows about the linked host', async () => {
    mockFetch({ ...statusRoute, 'GET /api/hosts/h1/zabbix': () => jsonResponse(200, overview) })
    render(<ZabbixSection host={host} onChanged={() => {}} />)

    expect(await screen.findByText('High CPU utilization')).toBeInTheDocument()
    expect(screen.getByText('High')).toBeInTheDocument()
    expect(screen.getAllByText('Available').length).toBeGreaterThan(0)
    expect(screen.getByText('Hypervisors')).toBeInTheDocument()
    expect(screen.getByText('37 days, 16:38:42')).toBeInTheDocument()
    expect(screen.getByText('16 GiB')).toBeInTheDocument()
    expect(screen.getByText('Rack 4')).toBeInTheDocument()
    expect(await screen.findByRole('link', { name: 'Open in Zabbix' })).toHaveAttribute('href', expect.stringContaining('https://zabbix.example.com/zabbix.php?action=latest.view'))
  })

  it('copies the serial number, MAC address and memory to the host', async () => {
    let body: Record<string, unknown> | undefined
    const onChanged = vi.fn()
    mockFetch({
      ...antiforgeryRoute,
      ...statusRoute,
      'GET /api/hosts/h1/zabbix': () => jsonResponse(200, overview),
      'PUT /api/hosts/h1': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, { ...host, serialNumber: 'SN-PVE1' })
      },
    })
    const user = userEvent.setup()
    render(<ZabbixSection host={host} onChanged={onChanged} />)

    const group = within(await screen.findByRole('group', { name: 'Copy from Zabbix' }))
    // CPU cores already match, so they aren't offered.
    expect(group.queryByText(/CPU cores/)).not.toBeInTheDocument()
    await user.click(group.getByRole('button', { name: 'Copy to host' }))

    await vi.waitFor(() => expect(onChanged).toHaveBeenCalled())
    expect(body).toMatchObject({ serialNumber: 'SN-PVE1', macAddress: 'AA:BB:CC:00:00:01', memoryMiB: 16384, cpuCores: 12, version: 7, zabbixHostId: '10084' })
  })

  it('says when Zabbix isn’t set up', async () => {
    mockFetch({
      ...statusRoute,
      'GET /api/hosts/h1/zabbix': () => jsonResponse(409, { status: 409, code: 'zabbix_disabled', detail: 'Zabbix isn’t set up.' }),
    })
    render(<ZabbixSection host={host} onChanged={() => {}} />)

    expect(await screen.findByText(/Linked to Zabbix host 10084, but Zabbix isn’t set up/)).toBeInTheDocument()
  })
})

function Field() {
  const [value, setValue] = useState('')
  return <ZabbixHostField value={value} onChange={setValue} suggestion="web" />
}

describe('Zabbix host field', () => {
  it('finds a Zabbix host and links it', async () => {
    mockFetch({
      'GET /api/zabbix/hosts': (url) =>
        jsonResponse(
          200,
          new URL(url, 'http://localhost').searchParams.get('search') === 'web'
            ? [{ ...overview.host, hostId: '10085', host: 'web01', name: 'Web 01', ipAddresses: ['10.0.0.20'], dnsNames: ['web01.example.com'] }]
            : [],
        ),
    })
    const user = userEvent.setup()
    render(<Field />)

    await user.click(screen.getByRole('button', { name: 'Find in Zabbix…' }))
    await user.click(await screen.findByRole('button', { name: /Web 01/ }))

    expect(screen.getByLabelText('Zabbix host ID')).toHaveValue('10085')
    await user.click(screen.getByRole('button', { name: 'Unlink' }))
    expect(screen.getByLabelText('Zabbix host ID')).toHaveValue('')
  })
})
