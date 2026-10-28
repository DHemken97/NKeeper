import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { antiforgeryRoute, AuthProvider, jsonResponse, mockFetch, routes } from '@nkeeper/plugin-sdk/testing'
import { directWebUrl, type Host } from '@nkeeper/plugin-sdk'
import type { IdracOverview } from './idrac'
// Registers the plugin, as loading its script does.
import '.'

// RACADM opens an SSH terminal: xterm.js needs a real browser, so a fake stands in.
const fakes = vi.hoisted(() => {
  class FakeTerminal {
    cols = 80
    rows = 24
    loadAddon() {}
    open() {}
    focus() {}
    dispose() {}
    write() {}
    onData() {
      return { dispose() {} }
    }
    onBinary() {
      return { dispose() {} }
    }
    onResize() {
      return { dispose() {} }
    }
  }
  class FakeFitAddon {
    fit() {}
  }
  return { FakeTerminal, FakeFitAddon }
})

vi.mock('@xterm/xterm', () => ({ Terminal: fakes.FakeTerminal }))
vi.mock('@xterm/addon-fit', () => ({ FitAddon: fakes.FakeFitAddon }))

/** Records the WebSockets a session opens and what it sends first. */
class FakeWebSocket {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSED = 3
  static instances: FakeWebSocket[] = []
  readyState = FakeWebSocket.CONNECTING
  binaryType = 'blob'
  sent: string[] = []
  onopen: (() => void) | null = null
  onmessage: ((event: { data: string }) => void) | null = null
  onclose: (() => void) | null = null
  readonly url: string
  constructor(url: string) {
    this.url = url
    FakeWebSocket.instances.push(this)
  }
  send(data: string) {
    this.sent.push(data)
  }
  close() {
    this.readyState = FakeWebSocket.CLOSED
  }
}

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const r750: Host = {
  id: 'h-r750',
  name: 'r750',
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
  plugins: {},
  tags: [],
  services: [
    { protocol: 'Ssh', port: null, label: null },
    { protocol: 'Idrac', port: null, label: null, address: '10.0.10.20' },
  ],
  credentials: [{ id: 'c-root', label: 'iDRAC root', username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const overview: IdracOverview = {
  manufacturer: 'Dell Inc.',
  model: 'PowerEdge R750',
  serviceTag: 'ABC1234',
  serialNumber: null,
  hostName: 'esx01',
  powerState: 'On',
  health: 'OK',
  biosVersion: '1.13.2',
  processorCount: 2,
  processorModel: 'Xeon Gold 6338',
  logicalProcessorCount: 128,
  memoryGiB: 512,
  managerModel: '15G Monolithic',
  firmwareVersion: '7.00.00.171',
  powerActions: ['On', 'ForceOff', 'ForceRestart', 'GracefulShutdown'],
  console: { available: true, pluginType: 'eHTML5', problem: null },
}

const ready = (target: string) => ({ status: 'ready', overview, target, certificate: null })

let openSpy: ReturnType<typeof vi.fn>

beforeEach(() => {
  FakeWebSocket.instances = []
  openSpy = vi.fn(() => ({}) as Window)
  vi.stubGlobal('open', openSpy)
  vi.stubGlobal('WebSocket', FakeWebSocket)
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
    'GET /api/hosts': () => jsonResponse(200, [r750]),
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

/** Connect ▸ iDRAC ▸ `mode` ▸ the saved credential. */
async function connect(user: ReturnType<typeof userEvent.setup>, mode: string) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /r750/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: /^iDRAC/ }))
  await user.hover(screen.getByRole('menuitem', { name: new RegExp(`^${mode}`) }))
  await user.click(screen.getByRole('menuitem', { name: /^iDRAC root/ }))
}

describe('iDRAC services', () => {
  it('shows the iDRAC at its own address, with what it can open', async () => {
    const { user } = renderWorkspace({})

    fireEvent.contextMenu(await screen.findByRole('button', { name: /r750/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: /^iDRAC.*10\.0\.10\.20/ }))

    expect(screen.getByRole('menuitem', { name: /^Virtual console/ })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: /^Status & power/ })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: /^RACADM/ })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: /^Web interface/ })).toBeInTheDocument()
    expect(directWebUrl(r750, r750.services[1])).toBe('https://10.0.10.20/')
  })

  it('opens the HTML5 virtual console in a tab, through NKeeper, like an RDP or VNC session', async () => {
    let body: unknown
    const { user } = renderWorkspace({
      'POST /api/plugins/idrac/console': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, {
          status: 'ready',
          sessionId: '0123456789abcdef0123456789abcdef',
          launchPath: '/nkeeper/launch?t=token',
          baseDomain: null,
          target: 'https://10.0.10.20/',
          certificate: null,
        })
      },
    })

    await connect(user, 'Virtual console')

    const frame = await screen.findByTitle(/r750 — https:\/\/10\.0\.10\.20/)
    expect(body).toEqual({ hostId: 'h-r750', serviceIndex: 1, credentialId: 'c-root' })
    expect(frame).toHaveAttribute('src', 'http://nkw-0123456789abcdef0123456789abcdef.web.localhost:3000/nkeeper/launch?t=token')
    expect(screen.getByRole('tab', { name: /r750.*iDRAC · Virtual console · root/, selected: true })).toBeInTheDocument()
  })

  it('explains why the console can’t open', async () => {
    const { user } = renderWorkspace({
      'POST /api/plugins/idrac/console': () =>
        jsonResponse(502, { status: 502, code: 'connection_failed', detail: 'This iDRAC’s virtual console type is Java. Set the plug-in type to HTML5.' }),
    })

    await connect(user, 'Virtual console')

    expect(await screen.findByText(/virtual console type is Java/)).toBeInTheDocument()
  })

  it('trusts the iDRAC’s certificate, then shows the server with its power buttons', async () => {
    const bodies: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/plugins/idrac/overview': (_url, init) => {
        bodies.push(JSON.parse(String(init.body)))
        return bodies.length === 1
          ? jsonResponse(200, {
              status: 'trustRequired',
              overview: null,
              target: 'https://10.0.10.20/',
              certificate: { fingerprint: 'AA:BB', subject: 'CN=idrac-ABC1234', issuer: 'CN=idrac-ABC1234', expires: '2030-01-01T00:00:00Z' },
            })
          : jsonResponse(200, ready('https://10.0.10.20/'))
      },
    })

    await connect(user, 'Status & power')
    const prompt = await screen.findByRole('alertdialog', { name: 'Trust this certificate?' })
    await user.click(within(prompt).getByRole('button', { name: 'Trust and connect' }))

    expect(await screen.findByRole('heading', { name: 'Dell Inc. PowerEdge R750' })).toBeInTheDocument()
    expect(bodies[1]).toMatchObject({ hostId: 'h-r750', serviceIndex: 1, credentialId: 'c-root', trustFingerprint: 'AA:BB' })
    expect(screen.getByText('ABC1234')).toBeInTheDocument()
    expect(screen.getByText('2 × Xeon Gold 6338 · 128 threads')).toBeInTheDocument()
    // Only the actions the server accepts, and only those that fit its power state.
    expect(screen.getByRole('button', { name: 'Power on' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Reset' })).toBeEnabled()
    expect(screen.queryByRole('button', { name: 'Restart' })).not.toBeInTheDocument()
  })

  it('confirms a power action before pressing it', async () => {
    let power: unknown
    const { user } = renderWorkspace({
      'POST /api/plugins/idrac/overview': () => jsonResponse(200, ready('https://10.0.10.20/')),
      'POST /api/plugins/idrac/power': (_url, init) => {
        power = JSON.parse(String(init.body))
        return jsonResponse(200, { status: 'ready', overview: null, target: 'https://10.0.10.20/', certificate: null })
      },
    })

    await connect(user, 'Status & power')
    await user.click(await screen.findByRole('button', { name: 'Power off' }))
    const dialog = await screen.findByRole('dialog', { name: 'Power off r750?' })
    expect(power).toBeUndefined()
    await user.click(within(dialog).getByRole('button', { name: 'Power off' }))

    await waitFor(() => expect(power).toEqual({ hostId: 'h-r750', serviceIndex: 1, credentialId: 'c-root', resetType: 'ForceOff' }))
    expect(await screen.findByText('Power off: sent to the server.')).toBeInTheDocument()
  })

  it('saves the service tag, threads and memory the iDRAC reports to the host', async () => {
    let body: Record<string, unknown> | undefined
    const { user } = renderWorkspace({
      'POST /api/plugins/idrac/overview': () => jsonResponse(200, ready('https://10.0.10.20/')),
      'PUT /api/hosts/h-r750': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, { ...r750, serialNumber: 'ABC1234', cpuCores: 128, memoryMiB: 524288, version: 2 })
      },
    })

    await connect(user, 'Status & power')
    expect(await screen.findByText('Records the serial number ABC1234, 128 CPU cores, 512 GiB memory on r750.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Save to host' }))

    await waitFor(() => expect(body).toBeDefined())
    expect(body).toMatchObject({ name: 'r750', serialNumber: 'ABC1234', cpuCores: 128, memoryMiB: 524288, version: 1 })
    expect(await screen.findByText('Saved to the host.')).toBeInTheDocument()
    // Nothing left to record.
    expect(screen.queryByRole('button', { name: 'Save to host' })).not.toBeInTheDocument()
  })

  it('opens RACADM as an SSH session to the iDRAC service, from the menu or the status tab', async () => {
    const { user } = renderWorkspace({
      'POST /api/plugins/idrac/overview': () => jsonResponse(200, ready('https://10.0.10.20/')),
    })

    await connect(user, 'Status & power')
    await user.click(await screen.findByRole('button', { name: 'RACADM' }))

    await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1))
    expect(FakeWebSocket.instances[0].url).toMatch(/\/api\/ssh\/session$/)
    expect(screen.getByRole('tab', { name: /r750.*iDRAC · RACADM · root/, selected: true })).toBeInTheDocument()
  })
})
