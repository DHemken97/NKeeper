import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'

// spice-html5 needs real WebSockets and canvases; a fake records how NKeeper drives it.
const spice = vi.hoisted(() => {
  interface Options {
    uri: string
    screen_id: string
    password?: string
    onsuccess?: () => void
    onerror?: (error: Error) => void
    onagent?: () => void
  }
  class FakeSpiceMainConn {
    static instances: FakeSpiceMainConn[] = []
    stopped = false
    /** spice-html5 removes its canvases from the screen element (looked up by ID) when stopped. */
    screenPresentWhenStopped: boolean | null = null
    resized: number[][] = []
    options: Options
    constructor(options: Options) {
      this.options = options
      FakeSpiceMainConn.instances.push(this)
    }
    stop() {
      this.stopped = true
      this.screenPresentWhenStopped = document.getElementById(this.options.screen_id) !== null
    }
    resize_window(...args: number[]) {
      this.resized.push(args)
    }
  }
  return { FakeSpiceMainConn, sendCtrlAltDel: vi.fn() }
})

vi.mock('../../vendor/spice-html5/main.js', () => ({ SpiceMainConn: spice.FakeSpiceMainConn, sendCtrlAltDel: spice.sendCtrlAltDel }))

// noVNC likewise: a fake RFB that records its target, URL and options, and lets tests fire its events.
const vnc = vi.hoisted(() => {
  class FakeRfb extends EventTarget {
    static instances: FakeRfb[] = []
    scaleViewport = false
    background = ''
    disconnected = false
    sendCtrlAltDel = vi.fn()
    focus = vi.fn()
    target: HTMLElement
    url: string
    options: { credentials?: { password?: string } }
    constructor(target: HTMLElement, url: string, options: { credentials?: { password?: string } }) {
      super()
      this.target = target
      this.url = url
      this.options = options
      FakeRfb.instances.push(this)
    }
    disconnect() {
      this.disconnected = true
    }
    emit(type: string, detail: unknown = {}) {
      this.dispatchEvent(new CustomEvent(type, { detail }))
    }
  }
  return { FakeRfb }
})

vi.mock('@novnc/novnc', () => ({ default: vnc.FakeRfb }))

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const hypervisor: Host = {
  id: 'h-kvm',
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
  credentials: [{ id: 'c1', label: null, username: 'virt', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const vms = [
  { id: 'a1', name: 'db-vm', state: 'shut off', display: null },
  { id: 'b2', name: 'web vm', state: 'running', display: 'spice' },
  { id: 'c3', name: 'legacy', state: 'running', display: 'vnc' },
]

beforeEach(() => {
  spice.FakeSpiceMainConn.instances = []
  vnc.FakeRfb.instances = []
  spice.sendCtrlAltDel.mockClear()
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      disconnect() {}
    },
  )
})

afterEach(() => vi.unstubAllGlobals())

function renderWorkspace(routesOverride: Parameters<typeof mockFetch>[0]) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [hypervisor]),
    'DELETE /api/spice/sessions/s1': () => new Response(null, { status: 204 }),
    'DELETE /api/spice/sessions/s2': () => new Response(null, { status: 204 }),
    'DELETE /api/vnc/consoles/v1': () => new Response(null, { status: 204 }),
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

async function openHost(user: ReturnType<typeof userEvent.setup>) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /kvm01/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: /^KVM/ }))
  await user.click(screen.getByRole('menuitem', { name: /^virt/ }))
}

/** Clicks "Open console" in the VM list row of the named VM. */
async function openConsoleOf(user: ReturnType<typeof userEvent.setup>, vm: string) {
  const row = (await screen.findAllByRole('row')).find((r) => within(r).queryByText(vm))!
  await user.click(within(row).getByRole('button', { name: 'Open console' }))
}

const consoleReady = (id: string) => ({ status: 'ready', sessionId: id, password: 'vm-secret', hostKey: null })

describe('KVM hosts', () => {
  it('trusts the host key, lists the VMs, and offers consoles for running SPICE and VNC VMs', async () => {
    const bodies: unknown[] = []
    const { user } = renderWorkspace({
      'POST /api/kvm/vms': (_url, init) => {
        bodies.push(JSON.parse(String(init.body)))
        return bodies.length === 1
          ? jsonResponse(200, { status: 'trustRequired', vms: null, hostKey: { algorithm: 'ssh-ed25519', fingerprint: 'abc123' } })
          : jsonResponse(200, { status: 'ready', vms, hostKey: null })
      },
    })

    await openHost(user)
    const prompt = await screen.findByRole('alertdialog', { name: 'Trust this host key?' })
    expect(within(prompt).getByText('SHA256:abc123')).toBeInTheDocument()
    await user.click(within(prompt).getByRole('button', { name: 'Trust and connect' }))

    const table = await screen.findByRole('table')
    expect(bodies[0]).toEqual({ hostId: 'h-kvm', serviceIndex: 0, credentialId: 'c1' })
    expect(bodies[1]).toEqual({ hostId: 'h-kvm', serviceIndex: 0, credentialId: 'c1', trustFingerprint: 'abc123' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows.map((row) => within(row).getAllByRole('cell')[0].textContent)).toEqual(['db-vm', 'web vm', 'legacy'])
    expect(within(rows[0]).queryByRole('button')).not.toBeInTheDocument()
    expect(within(rows[1]).getByRole('button', { name: 'Open console' })).toBeInTheDocument()
    expect(within(rows[2]).getByRole('button', { name: 'Open console' })).toBeInTheDocument()
  })

  it('shows which node each VM of a Proxmox cluster is on, and opens its VNC console by VM ID', async () => {
    let consoleBody: unknown
    const { user } = renderWorkspace({
      'POST /api/kvm/vms': () =>
        jsonResponse(200, {
          status: 'ready',
          vms: [
            { id: '200', name: 'remote', state: 'running', display: 'vnc', node: 'pve2' },
            { id: '102', name: 'serial-vm', state: 'running', display: null, node: 'pve1' },
          ],
          hostKey: null,
        }),
      'POST /api/vnc/consoles': (_url, init) => {
        consoleBody = JSON.parse(String(init.body))
        return jsonResponse(200, consoleReady('v1'))
      },
    })

    await openHost(user)
    const table = await screen.findByRole('table')
    expect(within(table).getByRole('columnheader', { name: 'Node' })).toBeInTheDocument()
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows.map((row) => within(row).getAllByRole('cell')[1].textContent)).toEqual(['pve2', 'pve1'])
    expect(within(rows[1]).queryByRole('button')).not.toBeInTheDocument()

    await openConsoleOf(user, 'remote')
    await waitFor(() => expect(vnc.FakeRfb.instances).toHaveLength(1))
    expect(consoleBody).toEqual({ hostId: 'h-kvm', serviceIndex: 0, credentialId: 'c1', vm: '200' })
  })

  it('opens a Proxmox VM host’s console through its node’s Proxmox API, trusting the node’s certificate first', async () => {
    const node: Host = {
      ...hypervisor,
      services: [
        { protocol: 'Kvm', port: null, label: null },
        { protocol: 'Proxmox', port: null, label: null },
      ],
      credentials: [{ id: 'c1', label: null, username: 'root', domain: 'pam', notes: null, hasPassword: true, hasPrivateKey: false }],
    }
    const app01: Host = { ...hypervisor, id: 'h-app', name: 'app01', isVirtualMachine: true, hypervisorId: 'h-kvm', virtualMachineId: 'proxmox:lab/105', services: [], credentials: [] }
    const bodies: unknown[] = []
    const { user, fetchMock } = renderWorkspace({
      'GET /api/hosts': () => jsonResponse(200, [node, app01]),
      'POST /api/proxmox/consoles': (_url, init) => {
        bodies.push(JSON.parse(String(init.body)))
        return bodies.length === 1
          ? jsonResponse(200, {
              status: 'trustRequired',
              sessionId: null,
              password: null,
              target: 'https://10.0.0.5:8006/',
              certificate: { fingerprint: 'AA:BB', subject: 'CN=pve1.lab', issuer: 'CN=Proxmox CA', expires: '2027-01-01T00:00:00Z' },
            })
          : jsonResponse(200, { status: 'ready', sessionId: 'v1', password: 'PVEVNC:ticket', target: 'https://10.0.0.5:8006/', certificate: null })
      },
    })

    fireEvent.contextMenu(await screen.findByRole('button', { name: /app01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    expect(screen.queryByRole('menuitem', { name: /No services/ })).not.toBeInTheDocument()
    await user.hover(screen.getByRole('menuitem', { name: /^Proxmox console.*kvm01/ }))
    await user.click(screen.getByRole('menuitem', { name: /root/ }))

    const prompt = await screen.findByRole('alertdialog', { name: 'Trust this certificate?' })
    expect(within(prompt).getByText('AA:BB')).toBeInTheDocument()
    await user.click(within(prompt).getByRole('button', { name: 'Trust and connect' }))

    await waitFor(() => expect(vnc.FakeRfb.instances).toHaveLength(1))
    expect(bodies).toEqual([
      { hostId: 'h-kvm', serviceIndex: 1, credentialId: 'c1', vm: 105 },
      { hostId: 'h-kvm', serviceIndex: 1, credentialId: 'c1', trustFingerprint: 'AA:BB', vm: 105 },
    ])
    const rfb = vnc.FakeRfb.instances[0]
    expect(rfb.url).toMatch(/^ws:\/\/.*\/api\/vnc\/channel\/v1$/)
    expect(rfb.options.credentials?.password).toBe('PVEVNC:ticket')
    expect(screen.getByRole('tab', { name: /app01.*Proxmox console · kvm01/, selected: true })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Close app01' }))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/api/vnc/consoles/v1') && init?.method === 'DELETE')).toBe(true),
    )
  })

  it('offers a Proxmox console only for Proxmox VMs, and only through a Proxmox service', async () => {
    const proxmoxVm: Host = { ...hypervisor, id: 'h-app', name: 'app01', isVirtualMachine: true, hypervisorId: 'h-kvm', virtualMachineId: 'proxmox:lab/105', services: [] }
    const libvirtVm: Host = { ...proxmoxVm, id: 'h-web', name: 'web01', virtualMachineId: 'libvirt:9f0c6b5e-1111-4a2b-9c3d-000000000001' }
    // The node has a KVM service only: SSH isn't how Proxmox consoles open.
    const { user } = renderWorkspace({ 'GET /api/hosts': () => jsonResponse(200, [hypervisor, proxmoxVm, libvirtVm]) })

    fireEvent.contextMenu(await screen.findByRole('button', { name: /app01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    expect(screen.getByRole('menuitem', { name: /^Proxmox console.*Add a Proxmox service to kvm01/ })).toHaveAttribute('aria-disabled', 'true')
    await user.keyboard('{Escape}')

    fireEvent.contextMenu(await screen.findByRole('button', { name: /web01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    expect(screen.queryByRole('menuitem', { name: /Proxmox console/ })).not.toBeInTheDocument()
  })

  it('opens a VM console over NKeeper’s tunnel and closes it with the tab', async () => {
    let consoleBody: unknown
    const { user, fetchMock } = renderWorkspace({
      'POST /api/kvm/vms': () => jsonResponse(200, { status: 'ready', vms, hostKey: null }),
      'POST /api/spice/sessions': (_url, init) => {
        consoleBody = JSON.parse(String(init.body))
        return jsonResponse(200, consoleReady('s1'))
      },
    })

    await openHost(user)
    await openConsoleOf(user, 'web vm')

    await waitFor(() => expect(spice.FakeSpiceMainConn.instances).toHaveLength(1))
    const connection = spice.FakeSpiceMainConn.instances[0]
    expect(consoleBody).toEqual({ hostId: 'h-kvm', serviceIndex: 0, credentialId: 'c1', vm: 'b2' })
    expect(connection.options.uri).toMatch(/^ws:\/\/.*\/api\/spice\/channel\/s1$/)
    expect(connection.options.password).toBe('vm-secret')
    expect(document.getElementById(connection.options.screen_id)).not.toBeNull()
    expect(screen.getByRole('tab', { name: /web vm.*Console · kvm01/, selected: true })).toBeInTheDocument()

    act(() => connection.options.onsuccess?.())
    await user.click(await screen.findByRole('button', { name: 'Ctrl+Alt+Del' }))
    expect(spice.sendCtrlAltDel).toHaveBeenCalledWith(connection)

    await user.click(screen.getByRole('button', { name: 'Close web vm' }))
    expect(connection.stopped).toBe(true)
    // Stopped before React removed the tab, so spice-html5 could still find its screen.
    expect(connection.screenPresentWhenStopped).toBe(true)
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/api/spice/sessions/s1') && init?.method === 'DELETE')).toBe(true),
    )
  })

  it('keeps one console open at a time', async () => {
    let next = 0
    const { user } = renderWorkspace({
      'POST /api/kvm/vms': () => jsonResponse(200, { status: 'ready', vms, hostKey: null }),
      'POST /api/spice/sessions': () => jsonResponse(200, consoleReady(`s${++next}`)),
    })

    await openHost(user)
    await openConsoleOf(user, 'web vm')
    await waitFor(() => expect(spice.FakeSpiceMainConn.instances).toHaveLength(1))
    await user.click(screen.getByRole('tab', { name: /^kvm01/ }))
    await openConsoleOf(user, 'web vm')

    await waitFor(() => expect(spice.FakeSpiceMainConn.instances).toHaveLength(2))
    expect(spice.FakeSpiceMainConn.instances[0].stopped).toBe(true)
    expect(screen.getAllByRole('tab', { name: /web vm/ })).toHaveLength(1)
  })

  it('explains a console that closes', async () => {
    const { user } = renderWorkspace({
      'POST /api/kvm/vms': () => jsonResponse(200, { status: 'ready', vms, hostKey: null }),
      'POST /api/spice/sessions': () => jsonResponse(200, consoleReady('s1')),
    })

    await openHost(user)
    await openConsoleOf(user, 'web vm')
    await waitFor(() => expect(spice.FakeSpiceMainConn.instances).toHaveLength(1))
    const connection = spice.FakeSpiceMainConn.instances[0]
    act(() => connection.options.onsuccess?.())
    act(() => connection.options.onerror?.(new Error('Unexpected close while ready')))

    expect(await screen.findByText(/may have shut down or restarted/)).toBeInTheDocument()
    expect(connection.stopped).toBe(true)
  })

  it('opens a VNC console with noVNC over NKeeper’s tunnel and closes it with the tab', async () => {
    let consoleBody: unknown
    const { user, fetchMock } = renderWorkspace({
      'POST /api/kvm/vms': () => jsonResponse(200, { status: 'ready', vms, hostKey: null }),
      'POST /api/vnc/consoles': (_url, init) => {
        consoleBody = JSON.parse(String(init.body))
        return jsonResponse(200, consoleReady('v1'))
      },
    })

    await openHost(user)
    await openConsoleOf(user, 'legacy')

    await waitFor(() => expect(vnc.FakeRfb.instances).toHaveLength(1))
    const rfb = vnc.FakeRfb.instances[0]
    expect(consoleBody).toEqual({ hostId: 'h-kvm', serviceIndex: 0, credentialId: 'c1', vm: 'c3' })
    expect(rfb.url).toMatch(/^ws:\/\/.*\/api\/vnc\/channel\/v1$/)
    expect(rfb.options.credentials?.password).toBe('vm-secret')
    expect(rfb.scaleViewport).toBe(true)
    expect(screen.getByRole('tab', { name: /legacy.*Console · kvm01/, selected: true })).toBeInTheDocument()

    act(() => rfb.emit('connect'))
    await user.click(await screen.findByRole('button', { name: 'Ctrl+Alt+Del' }))
    expect(rfb.sendCtrlAltDel).toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: 'Close legacy' }))
    expect(rfb.disconnected).toBe(true)
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/api/vnc/consoles/v1') && init?.method === 'DELETE')).toBe(true),
    )
  })

  it('keeps a VNC console open alongside a SPICE one', async () => {
    const { user } = renderWorkspace({
      'POST /api/kvm/vms': () => jsonResponse(200, { status: 'ready', vms, hostKey: null }),
      'POST /api/spice/sessions': () => jsonResponse(200, consoleReady('s1')),
      'POST /api/vnc/consoles': () => jsonResponse(200, consoleReady('v1')),
    })

    await openHost(user)
    await openConsoleOf(user, 'web vm')
    await waitFor(() => expect(spice.FakeSpiceMainConn.instances).toHaveLength(1))
    await user.click(screen.getByRole('tab', { name: /^kvm01/ }))
    await openConsoleOf(user, 'legacy')

    await waitFor(() => expect(vnc.FakeRfb.instances).toHaveLength(1))
    expect(spice.FakeSpiceMainConn.instances[0].stopped).toBe(false)
    expect(screen.getByRole('tab', { name: /web vm/ })).toBeInTheDocument()
  })

  it('explains a VNC console that closes, and one whose password libvirt didn’t reveal', async () => {
    const { user } = renderWorkspace({
      'POST /api/kvm/vms': () => jsonResponse(200, { status: 'ready', vms, hostKey: null }),
      'POST /api/vnc/consoles': () => jsonResponse(200, { ...consoleReady('v1'), password: null }),
    })

    await openHost(user)
    await openConsoleOf(user, 'legacy')
    await waitFor(() => expect(vnc.FakeRfb.instances).toHaveLength(1))
    act(() => vnc.FakeRfb.instances[0].emit('credentialsrequired', { types: ['password'] }))
    expect(await screen.findByText(/needs a password, and libvirt didn’t give it to NKeeper/)).toBeInTheDocument()
    expect(vnc.FakeRfb.instances[0].disconnected).toBe(true)

    await user.click(screen.getAllByRole('button', { name: 'Reconnect' })[0])
    await waitFor(() => expect(vnc.FakeRfb.instances).toHaveLength(2))
    const second = vnc.FakeRfb.instances[1]
    act(() => second.emit('connect'))
    act(() => second.emit('disconnect', { clean: false }))
    expect(await screen.findByText(/may have shut down or restarted/)).toBeInTheDocument()
  })
})
