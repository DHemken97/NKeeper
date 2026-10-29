import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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
    zabbixHostId: null,
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
  zabbixHostId: null,
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
  namesake: null,
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
  neighbors: [],
}

describe('virtual machines', () => {
  it('inherit the tags of the host they run on, marked as inherited, unless a tag isn’t inheritable', async () => {
    let saved: Record<string, unknown> | undefined
    const tagged = {
      ...node2,
      tags: [
        { name: 'site', value: 'DC-A', inheritable: true },
        { name: 'env', value: 'prod', inheritable: true },
        { name: 'os', value: 'Proxmox VE 8', inheritable: false },
      ],
    }
    const { router, user } = renderAt('/?host=h-app01', {
      'GET /api/hosts': () => jsonResponse(200, [{ ...app01, tags: [{ name: 'env', value: 'test' }] }, node1, tagged, printer]),
      'PUT /api/hosts/h-node2': (_url, init) => {
        saved = JSON.parse(String(init.body)) as Record<string, unknown>
        return jsonResponse(200, { ...tagged, version: 2 })
      },
    })

    const details = await screen.findByRole('article', { name: 'app01' })
    const tags = within(details).getByRole('heading', { name: 'Tags' }).closest('section')!
    // Its own env replaces node2's; os isn't passed down.
    expect(within(tags).getByRole('button', { name: /^env\s*test:/ })).toBeInTheDocument()
    expect(within(tags).getByRole('button', { name: /^site\s*DC-A, inherited from node2/ })).toBeInTheDocument()
    expect(within(tags).queryByRole('button', { name: /prod|Proxmox/ })).not.toBeInTheDocument()
    expect(within(tags).getByText(/Inherited from node2, the host it runs on/)).toBeInTheDocument()

    // Searching by an inherited tag finds the VM.
    await user.click(within(tags).getByRole('button', { name: /^site\s*DC-A, inherited/ }))
    expect(router.state.location.search).toContain('q=site%3ADC-A')
    expect(await screen.findByRole('button', { name: /^app01/ })).toBeInTheDocument()

    // On the hypervisor, each tag says whether its VMs inherit it.
    await act(() => router.navigate('/?host=h-node2&edit=1'))
    const form = await screen.findByRole('form', { name: 'Edit node2' })
    expect(within(form).getByRole('checkbox', { name: 'VMs inherit tag 3' })).not.toBeChecked()
    await user.click(within(form).getByRole('checkbox', { name: 'VMs inherit tag 1' }))
    await user.click(within(form).getByRole('button', { name: 'Save changes' }))
    await waitFor(() =>
      expect(saved?.tags).toEqual([
        { name: 'site', value: 'DC-A', inheritable: false },
        { name: 'env', value: 'prod', inheritable: true },
        { name: 'os', value: 'Proxmox VE 8', inheritable: false },
      ]),
    )
  })

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

  it('on a Proxmox node: the cluster’s other nodes are added or tagged, and the node gets the cluster’s tag', async () => {
    let created: Record<string, unknown> | undefined
    let bulk: unknown
    let saved: Record<string, unknown> | undefined
    const { user } = renderAt('/', {
      'POST /api/hosts/h-node1/scan/internal': () =>
        jsonResponse(200, {
          status: 'ready',
          hostKey: null,
          findings: {
            ...nodeScan,
            guests: [],
            proxmoxCluster: 'lab',
            neighbors: [
              { ipAddress: '10.0.0.12', macAddress: 'AA:BB:CC:00:00:12', hostId: null, hostName: null, matchedBy: null, note: null,
                newHost: { name: '10.0.0.12', hostname: null, ipAddress: '10.0.0.12', macAddress: 'AA:BB:CC:00:00:12', description: 'Seen.' } },
            ],
            clusterNodes: [
              { name: 'pve2', address: '10.0.0.12', macAddress: 'AA:BB:CC:00:00:12', hostId: null, hostName: null, matchedBy: null, note: null, tagProposed: false,
                newHost: { name: 'pve2', hostname: 'pve2', ipAddress: '10.0.0.12', macAddress: 'AA:BB:CC:00:00:12', description: 'Added from a scan of node1: a node of its Proxmox cluster.' } },
              { name: 'node2', address: '10.0.0.2', macAddress: null, hostId: 'h-node2', hostName: 'node2', matchedBy: 'ip', newHost: null, note: null, tagProposed: true },
            ],
          },
        }),
      'PUT /api/hosts/h-node1': (_url, init) => {
        saved = JSON.parse(String(init.body)) as Record<string, unknown>
        return jsonResponse(200, { ...node1, version: 2 })
      },
      'POST /api/hosts': (_url, init) => {
        created = JSON.parse(String(init.body)) as Record<string, unknown>
        return jsonResponse(201, makeHost({ id: 'h-pve2', name: 'pve2' }))
      },
      'POST /api/hosts/bulk': (_url, init) => {
        bulk = JSON.parse(String(init.body))
        return jsonResponse(200, [node2])
      },
    })

    fireEvent.contextMenu(await screen.findByRole('button', { name: /node1/ }))
    for (const item of ['Tasks', 'Scan', 'Internal']) {
      await user.hover(screen.getByRole('menuitem', { name: new RegExp(`^${item}`) }))
    }
    await user.click(screen.getByRole('menuitem', { name: /^root/ }))

    const dialog = await screen.findByRole('dialog', { name: 'Internal scan of node1' })
    expect(await within(dialog).findByRole('checkbox', { name: 'Proxmox cluster' })).toBeChecked()
    const nodes = within(dialog).getByRole('group', { name: 'Proxmox cluster “lab”' })
    expect(within(nodes).getByRole('checkbox', { name: 'Add pve2' })).toBeChecked()
    expect(within(nodes).getByRole('checkbox', { name: 'Tag node2 cluster: lab' })).toBeChecked()
    // The node's address isn't offered again as a plain neighbour.
    expect(within(dialog).queryByRole('checkbox', { name: 'Add 10.0.0.12' })).not.toBeInTheDocument()

    await user.click(within(dialog).getByRole('button', { name: /^Apply 3 changes/ }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(saved?.tags).toEqual(expect.arrayContaining([{ name: 'cluster', value: 'lab', inheritable: true }]))
    // A new node gets the scanned node's services and the cluster's tag.
    expect(created).toMatchObject({ name: 'pve2', ipAddress: '10.0.0.12', tags: [{ name: 'cluster', value: 'lab' }], services: node1.services })
    expect(bulk).toEqual({ hostIds: ['h-node2'], addTags: [], setTags: [{ name: 'cluster', value: 'lab' }], removeTags: [] })
  })

  it('linked to a scanned hypervisor that weren’t found there can be unlinked', async () => {
    let bulk: unknown
    const { user } = renderAt('/', {
      'POST /api/hosts/h-node1/scan/internal': () =>
        jsonResponse(200, {
          status: 'ready',
          hostKey: null,
          findings: {
            ...nodeScan,
            guests: [],
            missingGuests: [
              { hostId: 'h-app01', name: 'app01', hypervisorHostId: 'h-node1', hypervisorHostName: 'node1', canManage: true },
              { hostId: 'h-locked', name: 'locked', hypervisorHostId: 'h-node1', hypervisorHostName: 'node1', canManage: false },
            ],
          },
        }),
      'POST /api/hosts/bulk': (_url, init) => {
        bulk = JSON.parse(String(init.body))
        return jsonResponse(200, [{ ...app01, hypervisorId: null }])
      },
    })

    fireEvent.contextMenu(await screen.findByRole('button', { name: /node1/ }))
    for (const item of ['Tasks', 'Scan', 'Internal']) {
      await user.hover(screen.getByRole('menuitem', { name: new RegExp(`^${item}`) }))
    }
    await user.click(screen.getByRole('menuitem', { name: /^root/ }))

    const dialog = await screen.findByRole('dialog', { name: 'Internal scan of node1' })
    const unlink = await within(dialog).findByRole('checkbox', { name: 'Unlink app01 from node1' })
    // The VM may only be hidden from this login, so nothing is unlinked unless asked.
    expect(unlink).not.toBeChecked()
    expect(within(dialog).queryByRole('checkbox', { name: /locked/ })).not.toBeInTheDocument()
    expect(within(dialog).getByText(/not found there; you can’t change it/)).toBeInTheDocument()
    await user.click(unlink)
    await user.click(within(dialog).getByRole('button', { name: 'Apply 1 change' }))

    // It stays a VM, with no host it runs on.
    await waitFor(() => expect(bulk).toEqual({ hostIds: ['h-app01'], addTags: [], setTags: [], removeTags: [], hypervisor: { hypervisorId: null } }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('found by a scan can all be ticked or unticked at once, and say why a host with their name isn’t them', async () => {
    const { user } = renderAt('/', {
      'POST /api/hosts/h-node1/scan/internal': () =>
        jsonResponse(200, {
          status: 'ready',
          hostKey: null,
          findings: {
            ...nodeScan,
            guests: [
              nodeScan.guests[0],
              guest({ virtualMachineId: 'libvirt:4', name: 'printer', guestHostId: 'h-printer', guestHostName: 'printer', matchedBy: 'mac', linkProposed: true }),
              guest({
                ...nodeScan.guests[1],
                name: 'zproxy',
                namesake: '“zproxy” has its name but runs on “node2”, and neither a MAC address nor a VM identity confirms it’s this VM, so it’s left there.',
              }),
            ],
          },
        }),
    })

    fireEvent.contextMenu(await screen.findByRole('button', { name: /node1/ }))
    for (const item of ['Tasks', 'Scan', 'Internal']) {
      await user.hover(screen.getByRole('menuitem', { name: new RegExp(`^${item}`) }))
    }
    await user.click(screen.getByRole('menuitem', { name: /^root/ }))

    const dialog = await screen.findByRole('dialog', { name: 'Internal scan of node1' })
    const vms = await within(dialog).findByRole('group', { name: 'Virtual machines' })
    const links = within(vms).getAllByRole('checkbox')
    links.forEach((c) => expect(c).toBeChecked())
    await user.click(within(vms).getByRole('button', { name: 'Select none' }))
    links.forEach((c) => expect(c).not.toBeChecked())
    await user.click(within(vms).getByRole('button', { name: 'Select all 2' }))
    links.forEach((c) => expect(c).toBeChecked())

    expect(within(dialog).getByText(/runs on “node2”, and neither a MAC address nor a VM identity confirms it/)).toBeInTheDocument()
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
    // So does a physical host without VMs, rather than being "not on a known host".
    expect(screen.getByRole('button', { name: /runs on:\s*printer/, expanded: true })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Not on a known host/ })).not.toBeInTheDocument()
  })

  it('a scan inside a VM proposes marking it as one', () => {
    const proposals = scanProposals(printer, { ...nodeScan, isKvmHost: false, guests: [], virtualization: 'kvm' }, 22)

    expect(proposals.find((p) => p.key === 'vm')).toMatchObject({ label: 'Virtual machine', detail: 'it runs under kvm', checked: true })
    expect(scanProposals(app01, { ...nodeScan, isKvmHost: false, guests: [], virtualization: 'kvm' }, 22).some((p) => p.key === 'vm')).toBe(false)
  })
})
