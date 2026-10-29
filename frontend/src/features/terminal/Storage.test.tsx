import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const app01: Host = {
  id: 'h-app01',
  name: 'app01',
  hostname: null,
  ipAddress: '10.0.0.20',
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
  services: [{ protocol: 'Ssh', port: null, label: null }],
  credentials: [{ id: 'c-root', label: null, username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const status = {
  iscsi: {
    initiatorName: 'iqn.1993-08.org.debian:01:app01',
    sessions: [{ target: 'iqn.2003-01.org.example.san:disk1', portal: '10.0.0.5:3260', state: 'LOGGED_IN', disks: ['sdb'] }],
    nodes: [
      { target: 'iqn.2003-01.org.example.san:disk1', portal: '10.0.0.5:3260', loggedIn: true },
      { target: 'iqn.2003-01.org.example.san:disk2', portal: '10.0.0.5:3260', loggedIn: false },
    ],
  },
  multipath: [
    {
      name: 'mpatha',
      wwid: '36001405aaaa',
      dmDevice: 'dm-0',
      product: 'LIO-ORG,disk01',
      size: '10G',
      paths: [
        { address: '3:0:0:0', device: 'sdb', dmState: 'active', pathState: 'ready', onlineState: 'running' },
        { address: '4:0:0:0', device: 'sdc', dmState: 'failed', pathState: 'faulty', onlineState: 'offline' },
      ],
    },
  ],
  drbd: [
    {
      name: 'r0',
      up: true,
      role: 'Secondary',
      volumes: [{ volume: 0, minor: 0, diskState: 'UpToDate' }],
      peers: [
        { name: 'node2', connectionState: 'Connected', role: 'Primary', volumes: [{ volume: 0, replicationState: 'SyncTarget', diskState: 'UpToDate', percentInSync: 42.5 }] },
      ],
    },
    { name: 'r1', up: false, role: null, volumes: [], peers: [] },
  ],
}

const ready = (storage: unknown, message: string | null = null) => jsonResponse(200, { status: 'ready', storage, hostKey: null, message })

function renderWorkspace(routesOverride: Parameters<typeof mockFetch>[0]) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [app01]),
    ...routesOverride,
  })
  const router = createMemoryRouter(routes, { initialEntries: ['/'] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { fetchMock, user: userEvent.setup() }
}

async function openStorage(user: ReturnType<typeof userEvent.setup>) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /app01/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: /^Storage/ }))
  await user.click(screen.getByRole('menuitem', { name: /^root/ }))
  return screen.findByRole('tabpanel', { name: 'app01 storage' })
}

describe('storage (iSCSI, multipath, DRBD)', () => {
  it('asks to trust the host key, then shows iSCSI targets, multipath paths and DRBD resources', async () => {
    const bodies: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/storage/status': (_url, init) => {
        bodies.push(JSON.parse(String(init.body)))
        return bodies.length === 1
          ? jsonResponse(200, { status: 'trustRequired', storage: null, hostKey: { algorithm: 'ssh-ed25519', fingerprint: 'SHA256:abc' } })
          : ready(status)
      },
    })

    const panel = await openStorage(user)
    await user.click(await within(panel).findByRole('button', { name: 'Trust and connect' }))

    const iscsi = await within(panel).findByRole('region', { name: 'iSCSI' })
    expect(within(iscsi).getByText('Initiator iqn.1993-08.org.debian:01:app01')).toBeInTheDocument()
    const disk1 = within(iscsi).getByRole('row', { name: /disk1/ })
    expect(disk1).toHaveTextContent('logged in')
    expect(disk1).toHaveTextContent('sdb')
    expect(within(disk1).getByRole('button', { name: 'Log out' })).toBeInTheDocument()
    expect(within(within(iscsi).getByRole('row', { name: /disk2/ })).getByRole('button', { name: 'Log in' })).toBeInTheDocument()

    const multipath = within(panel).getByRole('region', { name: 'Multipath' })
    expect(within(multipath).getByRole('row', { name: /mpatha/ })).toHaveTextContent('1 of 2 ready')
    expect(within(multipath).getByText(/sdc \(4:0:0:0\) failed · faulty · offline/)).toBeInTheDocument()
    // The map's paths come from the session that attached sdb.
    const target = within(within(multipath).getByRole('row', { name: /mpatha/ })).getAllByRole('cell')[2]
    expect(target).toHaveTextContent('iqn.2003-01.org.example.san:disk1via 10.0.0.5:3260')

    const drbd = within(panel).getByRole('region', { name: 'DRBD' })
    const r0 = within(drbd).getByRole('row', { name: /r0/ })
    expect(r0).toHaveTextContent('node2: Connected · Primary')
    expect(r0).toHaveTextContent('SyncTarget · UpToDate · 42.5% in sync')
    expect(within(within(drbd).getByRole('row', { name: /r1/ })).getByRole('button', { name: 'Up' })).toBeInTheDocument()

    expect(bodies[0]).toMatchObject({ hostId: 'h-app01', serviceIndex: 0, credentialId: 'c-root' })
    expect(bodies[1]).toMatchObject({ trustFingerprint: 'SHA256:abc' })
  })

  it('discovers targets at a portal and says what it found', async () => {
    const actions: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/storage/status': () => ready(status),
      'POST /api/storage/action': (_url, init) => {
        actions.push(JSON.parse(String(init.body)))
        return ready(status, 'Found 1 target, 1 new.')
      },
    })

    const panel = await openStorage(user)
    await user.type(await within(panel).findByLabelText('Discover targets at portal'), '10.0.0.9')
    await user.click(within(panel).getByRole('button', { name: 'Discover' }))

    expect(await within(panel).findByText('Found 1 target, 1 new.')).toBeInTheDocument()
    expect(actions).toEqual([expect.objectContaining({ action: 'iscsi.discover', portal: '10.0.0.9', hostId: 'h-app01', credentialId: 'c-root' })])
  })

  it('logs out of a target only after confirming, and shows why the host refused', async () => {
    const actions: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/storage/status': () => ready(status),
      'POST /api/storage/action': (_url, init) => {
        actions.push(JSON.parse(String(init.body)))
        return jsonResponse(502, { title: 'Bad Gateway', status: 502, code: 'connection_failed', detail: 'iSCSI logout · disk1 failed: device is busy.' })
      },
    })

    const panel = await openStorage(user)
    const row = within(await within(panel).findByRole('region', { name: 'iSCSI' })).getByRole('row', { name: /disk1/ })
    await user.click(within(row).getByRole('button', { name: 'Log out' }))

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Its disks disappear from this host.')
    expect(actions).toEqual([])
    await user.click(within(dialog).getByRole('button', { name: 'Log out' }))

    expect(await within(panel).findByRole('alert')).toHaveTextContent('device is busy')
    expect(actions).toEqual([expect.objectContaining({ action: 'iscsi.logout', target: 'iqn.2003-01.org.example.san:disk1', portal: '10.0.0.5:3260' })])
  })

  it('on a Proxmox node, mounts a blank multipath map as new LVM storage, then guards what it’s on', async () => {
    const lvm = { map: 'mpatha', volumeGroup: null, signature: null, storage: null, mounted: false }
    const proxmox = (maps: unknown[]) => ({ node: 'app01', clusterNodes: ['app01'], allStorage: ['local'], storages: [], maps })
    const actions: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/storage/status': () => ready({ ...status, proxmox: proxmox([lvm]) }),
      'POST /api/storage/action': (_url, init) => {
        actions.push(JSON.parse(String(init.body)))
        const mounted = { ...lvm, volumeGroup: 'san-vms', storage: 'san-vms', mounted: true }
        return ready({ ...status, proxmox: proxmox([mounted]) }, 'Mounted mpatha as Proxmox LVM storage san-vms (volume group san-vms) on app01.')
      },
    })

    const panel = await openStorage(user)
    const multipath = await within(panel).findByRole('region', { name: 'Multipath' })
    const row = within(multipath).getByRole('row', { name: /mpatha/ })
    expect(row).toHaveTextContent('Blank')
    expect(within(row).getByRole('button', { name: 'Flush' })).toBeInTheDocument()
    await user.click(within(row).getByRole('button', { name: 'Mount LVM' }))

    // The storage ID is suggested from the map's iSCSI target.
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('looks blank')
    const id = within(dialog).getByLabelText('Storage ID')
    expect(id).toHaveValue('disk1')
    await user.clear(id)
    await user.type(id, 'san-vms')
    await user.click(within(dialog).getByRole('button', { name: 'Initialize and mount' }))

    expect(await within(panel).findByText(/Mounted mpatha/)).toBeInTheDocument()
    expect(actions).toEqual([expect.objectContaining({ action: 'lvm.mount', name: 'mpatha', storage: 'san-vms' })])
    const mountedRow = within(multipath).getByRole('row', { name: /mpatha/ })
    expect(mountedRow).toHaveTextContent('Storage san-vms (VG san-vms)')
    expect(within(mountedRow).getByRole('button', { name: 'Unmount LVM' })).toBeInTheDocument()
    expect(within(mountedRow).queryByRole('button', { name: 'Flush' })).not.toBeInTheDocument()

    // Its only path can't log out while it's mounted.
    const disk1 = within(within(panel).getByRole('region', { name: 'iSCSI' })).getByRole('row', { name: /disk1/ })
    expect(within(disk1).getByRole('button', { name: 'Log out' })).toBeDisabled()
    expect(disk1).toHaveTextContent('mpatha is mounted: unmount it first')
  })

  it('logs in to a target and mounts its map on the storage its volume group already has', async () => {
    const disk2 = 'iqn.2003-01.org.example.san:disk2'
    const before = {
      ...status,
      proxmox: { node: 'app01', clusterNodes: ['app01', 'app02'], allStorage: ['san'], storages: [], maps: [] },
    }
    const after = {
      ...before,
      iscsi: { ...status.iscsi, sessions: [...status.iscsi.sessions, { target: disk2, portal: '10.0.0.5:3260', state: 'LOGGED_IN', disks: ['sdd'] }] },
      multipath: [
        ...status.multipath,
        { name: 'mpathb', wwid: '36001405bbbb', dmDevice: 'dm-1', product: null, size: '2T', paths: [{ address: '5:0:0:0', device: 'sdd', dmState: 'active', pathState: 'ready', onlineState: 'running' }] },
      ],
      proxmox: { ...before.proxmox, maps: [{ map: 'mpathb', volumeGroup: 'vgsan', signature: null, storage: 'san', mounted: false }] },
    }
    const actions: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/storage/status': () => ready(before),
      'POST /api/storage/action': (_url, init) => {
        actions.push(JSON.parse(String(init.body)))
        return ready(after, actions.length === 1 ? 'Logged in.' : 'Mounted mpathb.')
      },
    })

    const panel = await openStorage(user)
    const row = within(await within(panel).findByRole('region', { name: 'iSCSI' })).getByRole('row', { name: /disk2/ })
    await user.click(within(row).getByRole('button', { name: 'Log in & mount LVM' }))

    expect(await within(panel).findByText('Mounted mpathb.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(actions).toEqual([
      expect.objectContaining({ action: 'iscsi.login', target: disk2, portal: '10.0.0.5:3260' }),
      expect.objectContaining({ action: 'lvm.mount', name: 'mpathb' }),
    ])
  })

  it('brings a DRBD resource up straight away, and says when no storage tools are installed', async () => {
    const actions: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/storage/status': () => ready(status),
      'POST /api/storage/action': (_url, init) => {
        actions.push(JSON.parse(String(init.body)))
        return ready({ iscsi: null, multipath: null, drbd: null })
      },
    })

    const panel = await openStorage(user)
    const r1 = within(await within(panel).findByRole('region', { name: 'DRBD' })).getByRole('row', { name: /r1/ })
    await user.click(within(r1).getByRole('button', { name: 'Up' }))

    expect(await within(panel).findByText(/has none of iSCSI/)).toBeInTheDocument()
    expect(actions).toEqual([expect.objectContaining({ action: 'drbd.up', name: 'r1' })])
  })
})
