import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from './types'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const base: Host = {
  id: 'h-kvm1',
  name: 'kvm01',
  hostname: null,
  ipAddress: '10.0.0.5',
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
  services: [{ protocol: 'Kvm', port: null, label: null }],
  credentials: [{ id: 'c1', label: null, username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const kvm01 = base
const kvm02: Host = { ...base, id: 'h-kvm2', name: 'kvm02', ipAddress: '10.0.0.6', credentials: [{ ...base.credentials[0], id: 'c2' }] }
const web01: Host = {
  ...base,
  id: 'h-web',
  name: 'web01',
  isVirtualMachine: true,
  hypervisorId: 'h-kvm1',
  virtualMachineId: 'libvirt:9f0c6b5e-1111-4a2b-9c3d-000000000001',
  services: [],
  credentials: [],
}

const plan = {
  mode: 'offline',
  guestState: 'running',
  sourceName: 'kvm01',
  targetName: 'kvm02',
  disks: [
    {
      name: 'vda',
      path: '/srv/vms/web.qcow2',
      layout: 'filesystem',
      mount: { device: '/dev/drbd0', mountPoint: '/srv/vms', fsType: 'ext4' },
      layers: [{ kind: 'drbd', name: 'r0', detail: '0' }],
      storage: null,
      note: null,
    },
  ],
  steps: [
    { kind: 'guest-shutdown', side: 'source', title: 'Shut it down on kvm01', critical: true },
    { kind: 'umount', side: 'source', title: 'Unmount /srv/vms on kvm01', critical: true },
    { kind: 'guest-move', side: 'hypervisor', title: 'Move it to kvm02', critical: true },
  ],
  blockers: [],
  notes: ['It will be shut down for the move: Its disks are image files on /srv/vms, a file system that can’t be mounted on two hosts at once.'],
  choices: { liveAvailable: false, liveUnavailableReason: null, logoutOldIscsi: true, iscsiToLogOut: [], iscsiKept: [], hasIscsi: false, hasDrbd: true, hasLocalDisks: false },
}

const job = (state: string, steps: string[], message: string | null = null) => ({
  id: 'j1',
  guest: 'web01',
  source: 'kvm01',
  target: 'kvm02',
  mode: 'offline',
  state,
  steps: plan.steps.map((s, i) => ({ title: s.title, side: s.side, state: steps[i], message: null, critical: true })),
  message,
})

function renderWorkspace(hosts: Host[], routesOverride: Parameters<typeof mockFetch>[0]) {
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, hosts),
    ...routesOverride,
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
    </AuthProvider>,
  )
  return userEvent.setup()
}

async function openMigrate(user: ReturnType<typeof userEvent.setup>, vm: RegExp, via: RegExp, login: RegExp) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: vm }))
  await user.hover(screen.getByRole('menuitem', { name: 'Tasks' }))
  await user.hover(screen.getByRole('menuitem', { name: via }))
  await user.hover(screen.getByRole('menuitem', { name: /^Migrate/ }))
  await user.click(screen.getByRole('menuitem', { name: login }))
  return screen.findByRole('dialog', { name: /Migrate/ })
}

describe('migrating a VM with its storage', () => {
  it('plans a libvirt move after trusting the new host, runs it, and records where the VM now runs', async () => {
    const plans: Record<string, unknown>[] = []
    const links: unknown[] = []
    let polls = 0
    const user = renderWorkspace([kvm01, kvm02, web01], {
      'POST /api/migrations/plan': (_url, init) => {
        plans.push(JSON.parse(String(init.body)))
        return plans.length === 1
          ? jsonResponse(200, { status: 'trustRequired', login: 'target', hostKey: { algorithm: 'ssh-ed25519', fingerprint: 'SHA256:kvm02' }, certificate: null, plan: null, jobId: null })
          : jsonResponse(200, { status: 'ready', login: null, hostKey: null, certificate: null, plan, jobId: null })
      },
      'POST /api/migrations': () => jsonResponse(200, { status: 'started', login: null, hostKey: null, certificate: null, plan, jobId: 'j1' }),
      'GET /api/migrations/j1': () => {
        polls++
        return jsonResponse(200, polls === 1 ? job('running', ['done', 'running', 'pending']) : job('succeeded', ['done', 'done', 'done'], 'Moved to kvm02.'))
      },
      'POST /api/hosts/hypervisor-links': (_url, init) => {
        links.push(JSON.parse(String(init.body)))
        return jsonResponse(200, [])
      },
    })

    const dialog = await openMigrate(user, /web01/, /^KVM.*kvm01/, /^root/)
    await user.selectOptions(within(dialog).getByLabelText('Move to'), 'kvm02')
    await user.click(within(dialog).getByRole('button', { name: 'Check' }))
    expect(await within(dialog).findByText('ssh-ed25519 SHA256:kvm02')).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Trust and continue' }))

    const disks = await within(dialog).findByRole('table', { name: 'Disks' })
    expect(within(disks).getByRole('row', { name: /vda/ })).toHaveTextContent('File system on shared storage/srv/vms (ext4) → DRBD r0')
    expect(within(dialog).getByText(/can’t be mounted on two hosts at once/)).toBeInTheDocument()
    expect(plans[1]).toMatchObject({
      via: 'kvm',
      vmHostId: 'h-web',
      vm: '9f0c6b5e-1111-4a2b-9c3d-000000000001',
      source: { hostId: 'h-kvm1', serviceIndex: 0, credentialId: 'c1' },
      target: { hostId: 'h-kvm2', serviceIndex: 0, credentialId: 'c2', trustFingerprint: 'SHA256:kvm02' },
      options: { live: true, startAfter: true, drbdDownOnOld: false, withLocalDisks: false },
    })
    expect(plans[1].options).not.toHaveProperty('logoutOldIscsi')

    await user.click(within(dialog).getByRole('button', { name: 'Shut down and migrate' }))

    expect(await within(dialog).findByText('Moved to kvm02.', {}, { timeout: 5000 })).toBeInTheDocument()
    const steps = within(dialog).getByRole('list', { name: 'Steps' })
    expect(within(steps).getAllByRole('listitem').map((li) => li.getAttribute('data-state'))).toEqual(['done', 'done', 'done'])
    expect(links).toEqual([{ links: [{ guestId: 'h-web', hypervisorId: 'h-kvm2', virtualMachineId: 'libvirt:9f0c6b5e-1111-4a2b-9c3d-000000000001' }] }])
  })

  it('plans again when an option changes, and can’t start while something blocks it', async () => {
    const plans: Record<string, unknown>[] = []
    const user = renderWorkspace([kvm01, kvm02, web01], {
      'POST /api/migrations/plan': (_url, init) => {
        plans.push(JSON.parse(String(init.body)))
        return jsonResponse(200, { status: 'ready', plan: { ...plan, blockers: ['DRBD resource r0 isn’t configured on kvm02.'] } })
      },
    })

    const dialog = await openMigrate(user, /web01/, /^KVM.*kvm01/, /^root/)
    await user.selectOptions(within(dialog).getByLabelText('Move to'), 'kvm02')
    await user.click(within(dialog).getByRole('button', { name: 'Check' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('isn’t configured on kvm02')
    expect(within(dialog).getByRole('button', { name: 'Shut down and migrate' })).toBeDisabled()

    await user.selectOptions(within(dialog).getByLabelText('iSCSI on the old host afterwards'), 'stay')
    await user.selectOptions(within(dialog).getByLabelText('DRBD on the old host afterwards'), 'down')

    await within(dialog).findByRole('button', { name: 'Shut down and migrate' })
    expect(plans.at(-1)).toMatchObject({ options: { logoutOldIscsi: false, drbdDownOnOld: true } })
  })

  it('uses SSH logins to the Proxmox nodes for their storage, and reports a migration that was put back', async () => {
    const pve: Host = { ...base, id: 'h-pve2', name: 'pve2', services: [{ protocol: 'Proxmox', port: null, label: null }, { protocol: 'Ssh', port: null, label: null }] }
    const pve1: Host = { ...base, id: 'h-pve1', name: 'pve1', services: [{ protocol: 'Ssh', port: null, label: null }], credentials: [{ ...base.credentials[0], id: 'c-pve1' }] }
    const app01: Host = { ...web01, id: 'h-app', name: 'app01', hypervisorId: 'h-pve2', virtualMachineId: 'proxmox:lab/105' }
    const plans: Record<string, unknown>[] = []
    let linked = false
    const user = renderWorkspace([pve, pve1, app01], {
      'POST /api/proxmox/guest': () =>
        jsonResponse(200, {
          status: 'ready',
          target: null,
          certificate: null,
          guest: { vmId: 105, type: 'qemu', node: 'pve2', name: 'app01', status: 'running' },
          nodes: [
            { name: 'pve1', online: true },
            { name: 'pve2', online: true },
          ],
        }),
      'POST /api/migrations/plan': (_url, init) => {
        plans.push(JSON.parse(String(init.body)))
        return jsonResponse(200, { status: 'ready', plan: { ...plan, mode: 'live', sourceName: 'pve2', targetName: 'pve1' } })
      },
      'POST /api/migrations': () => jsonResponse(200, { status: 'started', plan, jobId: 'j1' }),
      'GET /api/migrations/j1': () =>
        jsonResponse(200, job('rolledBack', ['undone', 'undone', 'failed'], 'Moving it to pve1 failed in Proxmox: migration aborted. Everything was put back as it was.')),
      'POST /api/hosts/hypervisor-links': () => {
        linked = true
        return jsonResponse(200, [])
      },
    })

    const dialog = await openMigrate(user, /app01/, /^Proxmox.*pve2/, /^root/)
    expect(await within(dialog).findByLabelText('Move to')).toHaveValue('pve1')
    await user.click(within(dialog).getByRole('button', { name: 'Check' }))
    await user.click(await within(dialog).findByRole('button', { name: 'Migrate live' }))

    expect(await within(dialog).findByText(/migration aborted\. Everything was put back/, {}, { timeout: 5000 })).toBeInTheDocument()
    expect(plans[0]).toMatchObject({
      via: 'proxmox',
      vm: '105',
      targetNode: 'pve1',
      source: { hostId: 'h-pve2', serviceIndex: 0, credentialId: 'c1' },
      target: { hostId: 'h-pve1', serviceIndex: 0, credentialId: 'c-pve1' },
      sourceShell: { hostId: 'h-pve2', serviceIndex: 1, credentialId: 'c1' },
    })
    expect(linked).toBe(false)
  })
})
