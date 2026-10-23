import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host, ScanFindings, ScannedGuest } from './types'

const adminUser = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

function makeHost(name: string, overrides: Partial<Host> = {}): Host {
  return {
    id: `h-${name}`,
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
    services: [{ protocol: 'Ssh', port: null, label: null, rdpSecurity: null }],
    credentials: [{ id: `c-${name}`, label: null, username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
    trustedKeys: [],
    canManage: true,
    createdAtUtc: '2026-10-01T12:00:00Z',
    updatedAtUtc: '2026-10-01T12:00:00Z',
    version: 2,
    ...overrides,
  }
}

const web1 = makeHost('web1')
const web2 = makeHost('web2')
const noLogin = makeHost('nologin', { credentials: [{ id: 'c-x', label: null, username: 'ops', domain: null, notes: null, hasPassword: false, hasPrivateKey: false }] })

const findings = (mac: string): ScanFindings => ({
  hostname: null,
  ipAddress: null,
  macAddress: mac,
  operatingSystem: 'Debian GNU/Linux 12 (bookworm)',
  ports: [],
  isKvmHost: false,
  cpuCores: null,
  memoryMiB: null,
  serialNumber: null,
  users: [],
  notes: [],
  virtualization: null,
  guests: [],
})

function renderWorkspace(scan: (hostId: string, body: Record<string, unknown>) => Response) {
  const scans: { hostId: string; body: Record<string, unknown> }[] = []
  const saved: Record<string, Record<string, unknown>> = {}
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, adminUser),
    'GET /api/hosts': () => jsonResponse(200, [noLogin, web1, web2]),
    'POST /api/hosts/h-web1/scan/internal': (_url, init) => record('h-web1', init),
    'POST /api/hosts/h-web2/scan/internal': (_url, init) => record('h-web2', init),
    'POST /api/hosts/h-nologin/scan/internal': (_url, init) => record('h-nologin', init),
    'PUT /api/hosts/h-web1': (_url, init) => save('h-web1', init),
    'PUT /api/hosts/h-web2': (_url, init) => save('h-web2', init),
  })
  function record(hostId: string, init: RequestInit) {
    const body = JSON.parse(String(init.body)) as Record<string, unknown>
    scans.push({ hostId, body })
    return scan(hostId, body)
  }
  function save(hostId: string, init: RequestInit) {
    saved[hostId] = JSON.parse(String(init.body)) as Record<string, unknown>
    return jsonResponse(200, { ...[web1, web2].find((h) => h.id === hostId), version: 3 })
  }
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
    </AuthProvider>,
  )
  return { scans, saved, user: userEvent.setup() }
}

async function scanAll(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: /^nologin/ }))
  await user.keyboard('{Shift>}')
  await user.click(screen.getByRole('button', { name: /^web2/ }))
  await user.keyboard('{/Shift}')
  const panel = await screen.findByRole('form', { name: '3 hosts selected' })
  await user.click(within(panel).getByRole('button', { name: 'Internal scan…' }))
  return screen.findByRole('dialog', { name: 'Internal scan of 3 hosts' })
}

describe('bulk internal scans', () => {
  it('scan each host with its saved login, skip unknown host keys, and apply the chosen findings together', async () => {
    const { scans, saved, user } = renderWorkspace((hostId) =>
      hostId === 'h-web2'
        ? jsonResponse(200, { status: 'trustRequired', findings: null, hostKey: { algorithm: 'ssh-ed25519', fingerprint: 'new-key' } })
        : jsonResponse(200, { status: 'ready', findings: findings('AA:BB:CC:00:00:01'), hostKey: null }),
    )

    const setup = await scanAll(user)
    // The host whose only login has no password can't be scanned with saved logins.
    await user.click(within(setup).getByRole('button', { name: 'Scan 2 hosts' }))

    const review = await screen.findByRole('dialog', { name: 'Scanned 1 of 3 hosts' })
    const notScanned = within(review).getByRole('region', { name: 'Not scanned' })
    expect(within(notScanned).getByText(/No saved SSH login/)).toBeInTheDocument()
    expect(within(notScanned).getByText(/SSH host key isn’t trusted yet/)).toBeInTheDocument()
    expect(scans.map((s) => s.hostId).sort()).toEqual(['h-web1', 'h-web2'])
    expect(scans.find((s) => s.hostId === 'h-web1')!.body).toEqual({ serviceIndex: 0, credentialId: 'c-web1' })

    const web1Findings = within(review).getByRole('group', { name: 'web1' })
    expect(within(web1Findings).getByRole('checkbox', { name: 'MAC address' })).toBeChecked()
    await user.click(within(review).getByRole('button', { name: 'Apply 2 changes' }))

    await waitFor(() => expect(saved['h-web1']).toMatchObject({ macAddress: 'AA:BB:CC:00:00:01', tags: [{ name: 'os', value: 'Debian GNU/Linux 12 (bookworm)' }] }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('can trust new host keys, and log in to every host with one login', async () => {
    const { scans, user } = renderWorkspace((_hostId, body) =>
      body.trustFingerprint === undefined
        ? jsonResponse(200, { status: 'trustRequired', findings: null, hostKey: { algorithm: 'ssh-ed25519', fingerprint: 'fp' } })
        : jsonResponse(200, { status: 'ready', findings: findings('AA:BB:CC:00:00:02'), hostKey: null }),
    )

    const setup = await scanAll(user)
    await user.click(within(setup).getByRole('radio', { name: /One login for all/ }))
    await user.type(within(setup).getByLabelText('Username'), 'admin')
    await user.type(within(setup).getByLabelText('Password'), 's3cret')
    await user.click(within(setup).getByRole('checkbox', { name: /Trust new SSH host keys/ }))
    await user.click(within(setup).getByRole('button', { name: 'Scan 3 hosts' }))

    expect(await screen.findByRole('dialog', { name: 'Scanned 3 of 3 hosts' })).toBeInTheDocument()
    expect(scans).toHaveLength(6)
    expect(scans.filter((s) => s.body.trustFingerprint === 'fp').map((s) => s.body)).toContainEqual({
      serviceIndex: 0,
      username: 'admin',
      password: 's3cret',
      trustFingerprint: 'fp',
    })
  })
  it('leave a host that VMs on two hypervisors both match unticked, and can select every VM at once', async () => {
    const vm = (hostId: string, overrides: Partial<ScannedGuest>): ScannedGuest => ({
      platform: 'libvirt',
      virtualMachineId: `libvirt:${hostId}`,
      name: 'zproxy',
      state: 'running',
      node: null,
      onScannedHost: true,
      macAddresses: [],
      addresses: [],
      guestHostId: null,
      guestHostName: null,
      matchedBy: null,
      hypervisorHostId: hostId,
      hypervisorHostName: hostId.slice(2),
      linkProposed: false,
      cpuCores: null,
      memoryMiB: null,
      note: null,
      newHost: null,
      namesake: null,
      ...overrides,
    })
    const { user } = renderWorkspace((hostId) =>
      jsonResponse(200, {
        status: 'ready',
        findings: {
          ...findings('AA:BB:CC:00:00:01'),
          guests: [
            vm(hostId, { guestHostId: 'h-zproxy', guestHostName: 'zproxy', matchedBy: 'name', linkProposed: true, note: 'Not linked yet.' }),
            vm(hostId, {
              virtualMachineId: `libvirt:${hostId}-db`,
              name: `db-${hostId}`,
              namesake: '“db” has its name but a different MAC address, so it’s taken to be another VM.',
              newHost: { name: `db-${hostId}`, hostname: `db-${hostId}`, ipAddress: null, macAddress: null, description: '' },
            }),
          ],
        },
        hostKey: null,
      }),
    )

    const setup = await scanAll(user)
    await user.click(within(setup).getByRole('button', { name: 'Scan 2 hosts' }))
    const review = await screen.findByRole('dialog', { name: 'Scanned 2 of 3 hosts' })

    const vms = within(review).getByRole('group', { name: 'Virtual machines' })
    const links = within(vms).getAllByRole('checkbox')
    links.forEach((c) => expect(c).not.toBeChecked())
    expect(within(vms).getAllByText(/more than one hypervisor match it/)).toHaveLength(2)
    await user.click(within(vms).getByRole('button', { name: 'Select all 2' }))
    links.forEach((c) => expect(c).toBeChecked())
    expect(within(vms).getByRole('button', { name: 'Select none' })).toBeInTheDocument()

    const add = within(review).getByRole('group', { name: 'Add to NKeeper' })
    expect(within(add).getAllByText(/different MAC address/)).toHaveLength(2)
    await user.click(within(add).getByRole('button', { name: 'Select all 2' }))
    within(add).getAllByRole('checkbox').forEach((c) => expect(c).toBeChecked())
  })
})
