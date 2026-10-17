import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { scanProposals } from './scanProposals'
import type { Host, ScanFindings, ScannedGuest } from './types'

const adminUser = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

function makeHost(overrides: Partial<Host>): Host {
  return {
    id: crypto.randomUUID(),
    name: 'host',
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
    credentials: [{ id: 'c-root', label: null, username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
    trustedKeys: [],
    canManage: true,
    createdAtUtc: '2026-10-01T12:00:00Z',
    updatedAtUtc: '2026-10-01T12:00:00Z',
    version: 3,
    ...overrides,
  }
}

const node1 = makeHost({ id: 'h-node1', name: 'node1', services: [{ protocol: 'Kvm', port: null, label: null, rdpSecurity: null }, { protocol: 'Ssh', port: null, label: null, rdpSecurity: null }] })
const node2 = makeHost({ id: 'h-node2', name: 'node2', ipAddress: '10.0.0.2' })
const app01 = makeHost({
  id: 'h-app01',
  name: 'app01',
  ipAddress: '10.0.0.50',
  isVirtualMachine: true,
  hypervisorId: 'h-node2',
  hypervisorSeenAtUtc: '2026-10-15T08:00:00Z',
  cpuCores: null,
  memoryMiB: null,
  serialNumber: null,
  jumpHostId: null,
  jumpCredentialId: null,
})
const printer = makeHost({ id: 'h-printer', name: 'printer', ipAddress: '10.0.0.90' })

function renderAt(path: string, routesOverride: Parameters<typeof mockFetch>[0] = {}) {
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, adminUser),
    'GET /api/hosts': () => jsonResponse(200, [app01, node1, node2, printer]),
    ...routesOverride,
  })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { router, user: userEvent.setup() }
}

const guest = (overrides: Partial<ScannedGuest>): ScannedGuest => ({
  platform: 'libvirt',
  virtualMachineId: 'libvirt:1',
  name: 'app01',
  state: 'running',
  node: null,
  onScannedHost: true,
  macAddresses: [],
  addresses: [],
  guestHostId: null,
  guestHostName: null,
  matchedBy: null,
  hypervisorHostId: 'h-node1',
  hypervisorHostName: 'node1',
  linkProposed: false,
  cpuCores: null,
  memoryMiB: null,
  note: null,
  newHost: null,
  ...overrides,
})

const nodeScan: ScanFindings = {
  hostname: null,
  ipAddress: '10.0.0.1',
  macAddress: null,
  operatingSystem: null,
  ports: [],
  isKvmHost: true,
  cpuCores: null,
  memoryMiB: null,
  serialNumber: null,
  users: [],
  notes: [],
  virtualization: null,
  guests: [
    guest({ guestHostId: 'h-app01', guestHostName: 'app01', matchedBy: 'name', linkProposed: true, note: 'Moved from “node2”.', cpuCores: 2, memoryMiB: 4096 }),
    guest({
      virtualMachineId: 'libvirt:2',
      name: 'scratch',
      addresses: ['192.168.122.20'],
      note: 'Not in NKeeper yet.',
      newHost: { name: 'scratch', hostname: 'scratch', ipAddress: '192.168.122.20', macAddress: '52:54:00:00:00:02', description: 'Added from a scan of node1.' },
    }),
    guest({ virtualMachineId: 'libvirt:3', name: 'Windows 11', note: 'Not in NKeeper, and the scan found no address for it.' }),
  ],
}

describe('virtual machines', () => {
  it('are marked in the editor, with the host they run on (hypervisors listed first)', async () => {
    let saved: Record<string, unknown> | undefined
    const { user } = renderAt('/?host=h-printer&edit=1', {
      'PUT /api/hosts/h-printer': (_url, init) => {
        saved = JSON.parse(String(init.body)) as Record<string, unknown>
        return jsonResponse(200, { ...printer, isVirtualMachine: true, hypervisorId: 'h-node1', version: 4 })
      },
    })

    const form = await screen.findByRole('form', { name: 'Edit printer' })
    expect(within(form).queryByLabelText('Runs on')).not.toBeInTheDocument()
    await user.click(within(form).getByRole('checkbox', { name: 'Virtual machine' }))

    const runsOn = within(form).getByLabelText('Runs on')
    const groups = within(runsOn).getAllByRole('group')
    // node1 has a KVM service and node2 already runs a VM: both are hypervisors.
    expect(within(groups[0]).getAllByRole('option').map((o) => o.textContent)).toEqual(['node1', 'node2'])
    expect(within(runsOn).queryByRole('option', { name: 'printer' })).not.toBeInTheDocument()
    await user.selectOptions(runsOn, 'node1')
    await user.click(within(form).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(saved).toMatchObject({ isVirtualMachine: true, hypervisorId: 'h-node1' }))
  })

  it('show where they run, and hypervisors list their VMs', async () => {
    const { router, user } = renderAt('/?host=h-app01')

    const details = await screen.findByRole('article', { name: 'app01' })
    expect(within(details).getByText(/seen there/)).toBeInTheDocument()
    await user.click(within(details).getByRole('button', { name: 'node2' }))

    expect(router.state.location.search).toBe('?host=h-node2')
    const node = await screen.findByRole('article', { name: 'node2' })
    const vms = within(node).getByRole('heading', { name: /Virtual machines/ }).closest('section')!
    expect(within(vms).getByRole('button', { name: 'app01' })).toBeInTheDocument()
  })

  it('are linked to the node a scan found them on, and ones NKeeper lacks can be added', async () => {
    let links: unknown
    let created: unknown
    const { user } = renderAt('/', {
      'POST /api/hosts/h-node1/scan/internal': () => jsonResponse(200, { status: 'ready', findings: nodeScan, hostKey: null }),
      'POST /api/hosts/hypervisor-links': (_url, init) => {
        links = JSON.parse(String(init.body))
        return jsonResponse(200, [{ ...app01, hypervisorId: 'h-node1', virtualMachineId: 'libvirt:1' }])
      },
      'POST /api/hosts/from-scan': (_url, init) => {
        created = JSON.parse(String(init.body))
        return jsonResponse(200, [makeHost({ id: 'h-scratch', name: 'scratch', isVirtualMachine: true, hypervisorId: 'h-node1' })])
      },
    })

    fireEvent.contextMenu(await screen.findByRole('button', { name: /node1/ }))
    for (const item of ['Tasks', 'Scan', 'Internal']) {
      await user.hover(screen.getByRole('menuitem', { name: new RegExp(`^${item}`) }))
    }
    await user.click(screen.getByRole('menuitem', { name: /^root/ }))

    const dialog = await screen.findByRole('dialog', { name: 'Internal scan of node1' })
    const link = await within(dialog).findByRole('checkbox', { name: 'app01 runs on node1' })
    expect(link).toBeChecked()
    expect(within(dialog).getByText(/libvirt VM “app01” \(running\) · matched by its name · 2 vCPUs, 4 GiB · Moved from “node2”/)).toBeInTheDocument()
    // Adding hosts starts unticked; a VM without an address is only listed.
    const add = within(dialog).getByRole('checkbox', { name: 'Add scratch' })
    expect(add).not.toBeChecked()
    expect(within(dialog).getByText('libvirt VM on node1 · 192.168.122.20 · 52:54:00:00:00:02')).toBeInTheDocument()
    expect(within(dialog).getByText('Windows 11')).toBeInTheDocument()
    await user.click(add)

    await user.click(within(dialog).getByRole('button', { name: 'Apply 2 changes' }))

    // What the hypervisor gives each VM is recorded with the link.
    await waitFor(() =>
      expect(links).toEqual({ links: [{ guestId: 'h-app01', hypervisorId: 'h-node1', virtualMachineId: 'libvirt:1', cpuCores: 2, memoryMiB: 4096 }] }),
    )
    await waitFor(() =>
      expect(created).toEqual({
        hosts: [
          {
            name: 'scratch',
            hostname: 'scratch',
            ipAddress: '192.168.122.20',
            macAddress: '52:54:00:00:00:02',
            description: 'Added from a scan of node1.',
            hypervisorId: 'h-node1',
            virtualMachineId: 'libvirt:2',
            cpuCores: null,
            memoryMiB: null,
          },
        ],
      }),
    )
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    // The new host joins the tree.
    expect(await screen.findByRole('button', { name: /^scratch/ })).toBeInTheDocument()
  })

  it('can group the tree by the host they run on', async () => {
    const { router, user } = renderAt('/')

    await user.selectOptions(await screen.findByRole('combobox', { name: 'Group by' }), 'Runs on (hypervisor)')

    expect(router.state.location.search).toBe('?group=%40runs-on')
    const node = screen.getByRole('button', { name: /runs on:\s*node2/, expanded: true })
    expect(within(node.closest('li')!).getByRole('button', { name: /^app01/ })).toBeInTheDocument()
    // The node itself heads its group: it runs on its own hardware.
    const group = within(node.closest('li')!).getAllByRole('button').filter((b) => b !== node)
    expect(group[0]).toHaveAccessibleName(/^node2/)
    expect(screen.getByRole('button', { name: /Not on a known host/ })).toBeInTheDocument()
  })

  it('a scan inside a VM proposes marking it as one', () => {
    const proposals = scanProposals(printer, { ...nodeScan, isKvmHost: false, guests: [], virtualization: 'kvm' }, 22)

    expect(proposals.find((p) => p.key === 'vm')).toMatchObject({ label: 'Virtual machine', detail: 'it runs under kvm', checked: true })
    expect(scanProposals(app01, { ...nodeScan, isKvmHost: false, guests: [], virtualization: 'kvm' }, 22).some((p) => p.key === 'vm')).toBe(false)
  })
})
