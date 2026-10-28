import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { antiforgeryRoute, jsonResponse, mockFetch } from '@nkeeper/plugin-sdk/testing'
import type { Host } from '@nkeeper/plugin-sdk'
import { ZabbixHostField } from './components/ZabbixHostField'
import { ZabbixSection } from './components/ZabbixSection'
import { ZabbixReport } from './pages/ZabbixReport'
import type { ZabbixComparison, ZabbixHostOverview } from './zabbixApi'

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
  plugins: { zabbix: { hostId: '10084' } },
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

const statusRoute = { 'GET /api/plugins/zabbix/status': () => jsonResponse(200, { enabled: true, frontendUrl: 'https://zabbix.example.com/' }) }

describe('Zabbix in host details', () => {
  it('shows what Zabbix knows about the linked host', async () => {
    mockFetch({ ...statusRoute, 'GET /api/plugins/zabbix/hosts/h1': () => jsonResponse(200, overview) })
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
      'GET /api/plugins/zabbix/hosts/h1': () => jsonResponse(200, overview),
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
    expect(body).toMatchObject({ serialNumber: 'SN-PVE1', macAddress: 'AA:BB:CC:00:00:01', memoryMiB: 16384, cpuCores: 12, version: 7 })
  })

  it('says when Zabbix isn’t set up', async () => {
    mockFetch({
      ...statusRoute,
      'GET /api/plugins/zabbix/hosts/h1': () => jsonResponse(409, { status: 409, code: 'zabbix_disabled', detail: 'Zabbix isn’t set up.' }),
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
      'GET /api/plugins/zabbix/hosts': (url) =>
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

const compared = (id: string, name: string, ipAddress: string | null) => ({ id, name, hostname: null, ipAddress, macAddress: null, canManage: true })
const zabbixHost = (hostId: string, name: string, ip: string) => ({ ...overview.host, hostId, host: name, name, ipAddresses: [ip], macAddresses: [], tags: [] })

const comparison: ZabbixComparison = {
  linked: [{ host: compared('h1', 'pve1', '192.168.42.2'), zabbix: overview.host, matchedBy: null }],
  probableMatches: [{ host: compared('h2', 'web server', '10.0.0.20'), zabbix: zabbixHost('10085', 'Web 01', '10.0.0.20'), matchedBy: 'ip' }],
  zabbixOnly: [zabbixHost('10086', 'Core switch', '10.0.0.250'), zabbixHost('10087', 'PBX1', '10.0.0.13')],
  nKeeperOnly: [compared('h3', 'lonely', '10.9.9.9')],
  brokenLinks: [],
}

function renderReport() {
  render(
    <MemoryRouter>
      <ZabbixReport />
    </MemoryRouter>,
  )
  return userEvent.setup()
}

describe('Zabbix report', () => {
  it('links probable matches and adds Zabbix-only hosts', async () => {
    const posts: Record<string, unknown>[] = []
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/plugins/zabbix/comparison': () => jsonResponse(200, comparison),
      'POST /api/plugins/zabbix/links': (_url, init) => {
        posts.push(JSON.parse(String(init.body)))
        return jsonResponse(200, [])
      },
      'POST /api/plugins/zabbix/import': (_url, init) => {
        posts.push(JSON.parse(String(init.body)))
        return jsonResponse(200, { created: [host], skipped: [{ zabbixHostId: '10087', name: 'PBX1', reason: 'Zabbix has no IP address or DNS name for it.' }] })
      },
    })
    const user = renderReport()

    expect(await screen.findByText(/1 linked · 1 probable match · 2 only in Zabbix · 1 only in NKeeper/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'lonely' })).toHaveAttribute('href', '/?host=h3')

    await user.click(screen.getByRole('button', { name: 'Link 1 host' }))
    expect(await screen.findByText('Linked 1 host to Zabbix.')).toBeInTheDocument()
    expect(posts[0]).toEqual({ links: [{ hostId: 'h2', zabbixHostId: '10085' }] })

    await user.click(screen.getByLabelText('Select every Zabbix host'))
    await user.click(screen.getByRole('button', { name: 'Add 2 hosts to NKeeper' }))
    expect(await screen.findByText(/Added 1 host to NKeeper\./)).toBeInTheDocument()
    expect(screen.getByText('PBX1: Zabbix has no IP address or DNS name for it.')).toBeInTheDocument()
    expect(posts[1]).toEqual({ zabbixHostIds: ['10086', '10087'], groupsAsTags: true })
  })

  it('points to Settings when Zabbix isn’t set up', async () => {
    mockFetch({ 'GET /api/plugins/zabbix/comparison': () => jsonResponse(409, { status: 409, code: 'zabbix_disabled', detail: 'Zabbix isn’t set up.' }) })
    renderReport()

    expect(await screen.findByRole('link', { name: 'Settings' })).toHaveAttribute('href', '/settings')
  })
})
