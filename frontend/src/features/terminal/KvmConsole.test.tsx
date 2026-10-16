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
    resized: number[][] = []
    options: Options
    constructor(options: Options) {
      this.options = options
      FakeSpiceMainConn.instances.push(this)
    }
    stop() {
      this.stopped = true
    }
    resize_window(...args: number[]) {
      this.resized.push(args)
    }
  }
  return { FakeSpiceMainConn, sendCtrlAltDel: vi.fn() }
})

vi.mock('../../vendor/spice-html5/main.js', () => ({ SpiceMainConn: spice.FakeSpiceMainConn, sendCtrlAltDel: spice.sendCtrlAltDel }))

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const hypervisor: Host = {
  id: 'h-kvm',
  name: 'kvm01',
  hostname: null,
  ipAddress: '10.0.0.5',
  macAddress: null,
  description: null,
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
  { uuid: 'a1', name: 'db-vm', state: 'shut off', display: null },
  { uuid: 'b2', name: 'web vm', state: 'running', display: 'spice' },
  { uuid: 'c3', name: 'legacy', state: 'running', display: 'vnc' },
]

beforeEach(() => {
  spice.FakeSpiceMainConn.instances = []
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

const consoleReady = (id: string) => ({ status: 'ready', sessionId: id, password: 'vm-secret', hostKey: null })

describe('KVM hosts', () => {
  it('trusts the host key, lists the VMs, and offers consoles for running SPICE VMs', async () => {
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
    expect(within(rows[2]).getByText('Only SPICE consoles are supported')).toBeInTheDocument()
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
    await user.click(await screen.findByRole('button', { name: 'Open console' }))

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
    await user.click(await screen.findByRole('button', { name: 'Open console' }))
    await waitFor(() => expect(spice.FakeSpiceMainConn.instances).toHaveLength(1))
    await user.click(screen.getByRole('tab', { name: /^kvm01/ }))
    await user.click(screen.getByRole('button', { name: 'Open console' }))

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
    await user.click(await screen.findByRole('button', { name: 'Open console' }))
    await waitFor(() => expect(spice.FakeSpiceMainConn.instances).toHaveLength(1))
    const connection = spice.FakeSpiceMainConn.instances[0]
    act(() => connection.options.onsuccess?.())
    act(() => connection.options.onerror?.(new Error('Unexpected close while ready')))

    expect(await screen.findByText(/may have shut down or restarted/)).toBeInTheDocument()
    expect(connection.stopped).toBe(true)
  })
})
