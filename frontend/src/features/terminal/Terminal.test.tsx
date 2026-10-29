import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { connectMenuEntries } from '../inventory/hostMenu'
import type { Host } from '../inventory/types'

// xterm.js needs a real browser; a fake records what the session writes and lets tests type.
const fakes = vi.hoisted(() => {
  class FakeTerminal {
    static instances: FakeTerminal[] = []
    cols = 80
    rows = 24
    written = ''
    dataListener: ((data: string) => void) | null = null
    constructor() {
      FakeTerminal.instances.push(this)
    }
    loadAddon() {}
    open() {}
    focus() {}
    dispose() {}
    write(data: string | Uint8Array) {
      this.written += typeof data === 'string' ? data : new TextDecoder().decode(data)
    }
    onData(listener: (data: string) => void) {
      this.dataListener = listener
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

/** A WebSocket the test plays the server for. */
class FakeWebSocket {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSED = 3
  static instances: FakeWebSocket[] = []

  readyState = FakeWebSocket.CONNECTING
  binaryType = 'blob'
  sent: (string | Uint8Array)[] = []
  onopen: (() => void) | null = null
  onmessage: ((event: { data: string | ArrayBuffer }) => void) | null = null
  onclose: (() => void) | null = null
  closed = false

  readonly url: string

  constructor(url: string) {
    this.url = url
    FakeWebSocket.instances.push(this)
  }

  send(data: string | Uint8Array) {
    this.sent.push(data)
  }

  close() {
    this.closed = true
    this.readyState = FakeWebSocket.CLOSED
  }

  /** Server side. */
  accept() {
    this.readyState = FakeWebSocket.OPEN
    act(() => this.onopen?.())
  }

  receive(message: object) {
    act(() => this.onmessage?.({ data: JSON.stringify(message) }))
  }

  receiveBytes(text: string) {
    act(() => this.onmessage?.({ data: new TextEncoder().encode(text).buffer }))
  }

  jsonSent() {
    return this.sent.filter((m): m is string => typeof m === 'string').map((m) => JSON.parse(m))
  }
}

const adminUser = { id: '6f1c0d4e-0000-4000-8000-000000000001', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

const linux01: Host = {
  id: 'h-linux01',
  name: 'linux01',
  hostname: 'linux01.example.com',
  ipAddress: null,
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
  services: [
    { protocol: 'Ssh', port: null, label: null },
    { protocol: 'Rdp', port: null, label: null },
    { protocol: 'Ssh', port: 2222, label: 'Jump' },
  ],
  credentials: [
    { id: 'c-root', label: 'Root', username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false },
    { id: 'c-empty', label: 'Placeholder', username: null, domain: null, notes: null, hasPassword: false, hasPrivateKey: false },
  ],
  trustedKeys: [],
  canManage: true,
  createdAtUtc: '2026-10-01T12:00:00Z',
  updatedAtUtc: '2026-10-01T12:00:00Z',
  version: 1,
}

beforeEach(() => {
  FakeWebSocket.instances = []
  fakes.FakeTerminal.instances = []
  vi.stubGlobal('WebSocket', FakeWebSocket)
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      disconnect() {}
    },
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
})

function renderWorkspace(path = '/') {
  mockFetch({
    'GET /api/auth/me': () => jsonResponse(200, adminUser),
    'GET /api/hosts': () => jsonResponse(200, [linux01]),
  })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { router, user: userEvent.setup() }
}

/** The n-th socket opened; the terminal is lazy-loaded, so it may take a moment to appear. */
async function socketAt(index: number) {
  await waitFor(() => expect(FakeWebSocket.instances.length).toBeGreaterThan(index))
  return FakeWebSocket.instances[index]
}

async function hostRow() {
  return within(await screen.findByRole('list', { name: 'Hosts' })).getByRole('button', { name: /linux01/ })
}

describe('host context menu', () => {
  it('connects through Connect ▸ SSH ▸ credential, confirming the host key', async () => {
    const { user } = renderWorkspace()

    fireEvent.contextMenu(await hostRow(), { clientX: 40, clientY: 80 })
    const menu = screen.getByRole('menu', { name: 'Actions for linux01' })
    await user.hover(within(menu).getByRole('menuitem', { name: 'Connect' }))
    const services = screen.getByRole('menu', { name: 'Connect' })
    expect(within(services).getByRole('menuitem', { name: /^RDP:3389/ })).not.toHaveAttribute('aria-disabled')
    await user.hover(within(services).getByRole('menuitem', { name: /^SSH:22/ }))
    const credentials = screen.getByRole('menu', { name: 'SSH' })
    expect(within(credentials).getByRole('menuitem', { name: /Placeholder.*No username/ })).toHaveAttribute('aria-disabled', 'true')
    await user.click(within(credentials).getByRole('menuitem', { name: /Root/ }))

    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /linux01.*SSH · root/, selected: true })).toBeInTheDocument()

    const socket = await socketAt(0)
    expect(socket.url).toBe('ws://localhost:3000/api/ssh/session')
    socket.accept()
    expect(socket.jsonSent()[0]).toEqual({ type: 'start', hostId: 'h-linux01', serviceIndex: 0, credentialId: 'c-root', cols: 80, rows: 24 })

    socket.receive({ type: 'status', status: 'connecting', target: 'root@linux01.example.com:22' })
    socket.receive({ type: 'hostKey', status: 'unknown', port: 22, algorithm: 'ssh-ed25519', fingerprint: 'abc123' })
    const prompt = screen.getByRole('alertdialog', { name: 'Trust this host key?' })
    expect(within(prompt).getByText('SHA256:abc123')).toBeInTheDocument()
    await user.click(within(prompt).getByRole('button', { name: 'Trust and connect' }))
    expect(socket.jsonSent().at(-1)).toEqual({ type: 'hostKeyDecision', trust: true })

    socket.receive({ type: 'connected', target: 'root@linux01.example.com:22' })
    expect(screen.getByText('Connected')).toBeInTheDocument()

    socket.receiveBytes('Welcome to linux01\r\n$ ')
    const terminal = fakes.FakeTerminal.instances[0]
    expect(terminal.written).toContain('Welcome to linux01')

    act(() => terminal.dataListener?.('ls\r'))
    expect(new TextDecoder().decode(socket.sent.at(-1) as Uint8Array)).toBe('ls\r')
  })

  it('is fully keyboard operable', async () => {
    const { user } = renderWorkspace()
    const row = await hostRow()
    row.focus()

    await user.keyboard('{Shift>}{F10}{/Shift}')
    expect(screen.getByRole('menuitem', { name: 'Connect' })).toHaveFocus()

    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('menuitem', { name: /^SSH:22/ })).toHaveFocus()
    await user.keyboard('{ArrowDown}{ArrowDown}')
    expect(screen.getByRole('menuitem', { name: /SSH · Jump:2222/ })).toHaveFocus()

    await user.keyboard('{ArrowLeft}')
    expect(screen.queryByRole('menu', { name: 'Connect' })).not.toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: 'Connect' })).toHaveFocus()

    await user.keyboard('{Escape}')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    expect(row).toHaveFocus()
  })

  it('connects with one-off credentials that are never offered for reconnect', async () => {
    const { user } = renderWorkspace()

    fireEvent.contextMenu(await hostRow())
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: /SSH · Jump/ }))
    await user.click(screen.getByRole('menuitem', { name: 'Enter credentials…' }))

    const dialog = screen.getByRole('dialog', { name: 'Enter credentials' })
    await user.type(within(dialog).getByLabelText('Username'), 'admin')
    await user.type(within(dialog).getByLabelText('Password'), 'hunter2')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    const socket = await socketAt(0)
    socket.accept()
    expect(socket.jsonSent()[0]).toEqual({ type: 'start', hostId: 'h-linux01', serviceIndex: 2, username: 'admin', password: 'hunter2', cols: 80, rows: 24 })

    socket.receive({ type: 'error', code: 'authentication_failed', message: 'Authentication failed for admin.' })
    expect(screen.getByRole('alert')).toHaveTextContent('Authentication failed for admin.')
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reconnect' })).not.toBeInTheDocument()
  })

  it('explains a changed host key and links to the host', async () => {
    const { user, router } = renderWorkspace()
    await user.dblClick(await hostRow())

    const socket = await socketAt(0)
    socket.accept()
    expect(socket.jsonSent()[0]).toMatchObject({ serviceIndex: 0, credentialId: 'c-root' })
    socket.receive({ type: 'hostKey', status: 'changed', port: 22, algorithm: 'ssh-ed25519', fingerprint: 'new', trustedFingerprint: 'old' })
    socket.receive({ type: 'error', code: 'host_key_changed', message: 'The ssh-ed25519 host key has changed. Someone may be intercepting the connection.' })

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Server identity changed')
    await user.click(screen.getByRole('button', { name: 'Show host details' }))

    expect(router.state.location.search).toBe('?host=h-linux01')
    expect(screen.getByRole('tab', { name: /Details/, selected: true })).toBeInTheDocument()
  })

  it('closes the tab by itself when the remote shell exits, returning to the previous tab', async () => {
    const { user } = renderWorkspace('/?host=h-linux01')
    await user.dblClick(await hostRow())
    await user.dblClick(await hostRow())
    const first = await socketAt(0)
    const second = await socketAt(1)
    first.accept()
    second.accept()
    first.receive({ type: 'connected', target: 'root@linux01.example.com:22' })
    expect(screen.getByRole('tab', { name: /linux01 \(2\)/, selected: true })).toBeInTheDocument()

    // An exit in the background tab removes it and leaves the active one alone.
    first.receive({ type: 'closed', message: 'The session ended.' })
    expect(screen.getAllByRole('tab', { name: /SSH/ })).toHaveLength(1)
    expect(screen.getByRole('tab', { name: /linux01 \(2\)/, selected: true })).toBeInTheDocument()

    // Exiting the active tab falls back to the host details.
    second.receive({ type: 'closed', message: 'The session ended.' })
    expect(screen.queryByRole('tab')).not.toBeInTheDocument()
    expect(screen.getByRole('article', { name: 'linux01' })).toBeVisible()
  })

  it('keeps the tab open after a lost connection so it can reconnect, and closing a tab closes its socket', async () => {
    const { user } = renderWorkspace()
    await user.dblClick(await hostRow())
    const first = await socketAt(0)
    first.accept()
    first.receive({ type: 'connected', target: 'root@linux01.example.com:22' })
    act(() => first.onclose?.())

    await user.click(screen.getByRole('button', { name: 'Reconnect' }))
    expect(first.closed).toBe(true)
    const second = await socketAt(1)
    second.accept()
    expect(second.jsonSent()[0]).toMatchObject({ credentialId: 'c-root' })

    await user.click(screen.getByRole('button', { name: 'Close linux01' }))
    expect(second.closed).toBe(true)
    expect(screen.queryByRole('tab', { name: /SSH/ })).not.toBeInTheDocument()
  })

  it('offers Connect from the host details too (no right-click on touch screens)', async () => {
    const { user } = renderWorkspace('/?host=h-linux01')

    await user.click(await screen.findByRole('button', { name: 'Connect' }))

    expect(screen.getByRole('menuitem', { name: /^SSH:22/ })).toHaveFocus()
  })

  it('reports a lost connection', async () => {
    renderWorkspace()
    fireEvent.doubleClick(await hostRow())
    const socket = await socketAt(0)
    socket.accept()
    socket.receive({ type: 'connected', target: 'root@linux01.example.com:22' })

    act(() => socket.onclose?.())

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('The connection was lost.'))
  })
})

describe('VNC', () => {
  it('connects with a password-only credential to the VNC endpoint', async () => {
    const vncHost: Host = {
      ...linux01,
      id: 'h-vnc',
      name: 'kiosk',
      services: [{ protocol: 'Vnc', port: null, label: null }],
      credentials: [{ id: 'c-vnc', label: 'VNC password', username: null, domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
    }
    mockFetch({
      'GET /api/auth/me': () => jsonResponse(200, adminUser),
      'GET /api/hosts': () => jsonResponse(200, [vncHost]),
    })
    const router = createMemoryRouter(routes, { initialEntries: ['/'] })
    render(
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>,
    )
    const user = userEvent.setup()

    fireEvent.contextMenu(await screen.findByRole('button', { name: /kiosk/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: /^VNC:5900/ }))
    const credential = screen.getByRole('menuitem', { name: /VNC password/ })
    expect(credential).not.toHaveAttribute('aria-disabled')
    await user.click(credential)

    const socket = await socketAt(0)
    expect(socket.url).toBe('ws://localhost:3000/api/vnc/session')
    socket.accept()
    expect(socket.jsonSent()[0]).toMatchObject({ type: 'start', hostId: 'h-vnc', serviceIndex: 0, credentialId: 'c-vnc' })
    expect(screen.getByRole('tab', { name: /kiosk.*VNC/, selected: true })).toBeInTheDocument()
  })

  it('opens RDP with the user’s own domain sign-in, sending no secret', async () => {
    mockFetch({
      'GET /api/auth/me': () => jsonResponse(200, { ...adminUser, isDirectoryAccount: true, signInAccount: 'CORP\\jdoe' }),
      'GET /api/hosts': () => jsonResponse(200, [linux01]),
    })
    const router = createMemoryRouter(routes, { initialEntries: ['/'] })
    render(
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>,
    )
    const user = userEvent.setup()

    fireEvent.contextMenu(await hostRow())
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: /^SSH:22/ }))
    expect(screen.queryByRole('menuitem', { name: /My sign-in/ })).not.toBeInTheDocument()
    await user.hover(screen.getByRole('menuitem', { name: /^RDP:3389/ }))
    await user.click(screen.getByRole('menuitem', { name: /My sign-in.*CORP\\jdoe/ }))

    const socket = await socketAt(0)
    expect(socket.url).toBe('ws://localhost:3000/api/rdp/session')
    socket.accept()
    const start = socket.jsonSent()[0]
    expect(start).toMatchObject({ type: 'start', hostId: 'h-linux01', serviceIndex: 1, useSignInCredentials: true })
    expect(start.password).toBeUndefined()
    expect(start.credentialId).toBeUndefined()
    expect(screen.getByRole('tab', { name: /linux01.*RDP · CORP\\jdoe/, selected: true })).toBeInTheDocument()
  })
})

describe('Telnet', () => {
  const telnetHost: Host = {
    ...linux01,
    id: 'h-switch',
    name: 'switch01',
    services: [{ protocol: 'Telnet', port: null, label: null }],
    credentials: [{ id: 'c-admin', label: 'Switch admin', username: 'admin', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  }

  function renderTelnet(host: Host) {
    mockFetch({
      'GET /api/auth/me': () => jsonResponse(200, adminUser),
      'GET /api/hosts': () => jsonResponse(200, [host]),
    })
    render(
      <AuthProvider>
        <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
      </AuthProvider>,
    )
    return userEvent.setup()
  }

  it('opens a terminal on the Telnet endpoint, marked as unencrypted', async () => {
    const user = renderTelnet(telnetHost)

    fireEvent.contextMenu(await screen.findByRole('button', { name: /switch01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: /^Telnet:23/ }))
    expect(screen.getByRole('menuitem', { name: /^Without a login/ })).toBeInTheDocument()
    await user.click(screen.getByRole('menuitem', { name: /Switch admin/ }))

    const socket = await socketAt(0)
    expect(socket.url).toBe('ws://localhost:3000/api/telnet/session')
    socket.accept()
    expect(socket.jsonSent()[0]).toEqual({ type: 'start', hostId: 'h-switch', serviceIndex: 0, credentialId: 'c-admin', cols: 80, rows: 24 })
    socket.receive({ type: 'connected', target: 'admin@10.0.0.2:23' })
    expect(screen.getByText('Unencrypted to host')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /switch01.*Telnet · admin/, selected: true })).toBeInTheDocument()
  })

  it('connects without a login on double-click when none is saved: the device asks in the terminal', async () => {
    const user = renderTelnet({ ...telnetHost, credentials: [] })

    await user.dblClick(await screen.findByRole('button', { name: /switch01/ }))

    const socket = await socketAt(0)
    socket.accept()
    expect(socket.jsonSent()[0]).toEqual({ type: 'start', hostId: 'h-switch', serviceIndex: 0, cols: 80, rows: 24 })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('connectMenuEntries', () => {
  it('explains when a host has no services', () => {
    const entries = connectMenuEntries({ ...linux01, services: [] }, { connect: vi.fn(), connectWithPrompt: vi.fn(), connectWithoutLogin: vi.fn(), connectWeb: vi.fn(), openDirect: vi.fn() })
    expect(entries).toEqual([{ kind: 'item', label: 'No services', detail: 'Edit the host to add one', disabled: true }])
  })

  it('offers only services NKeeper can connect with', () => {
    const actions = { connect: vi.fn(), connectWithPrompt: vi.fn(), connectWithoutLogin: vi.fn(), connectWeb: vi.fn(), openDirect: vi.fn() }
    const mixed = connectMenuEntries(
      { ...linux01, services: [{ protocol: 'Ssms', port: null, label: null }, { protocol: 'Ssh', port: null, label: null }, { protocol: 'Ftp', port: null, label: null }] },
      actions,
    )
    expect(mixed.map((entry) => (entry.kind === 'item' ? entry.label : entry.kind))).toEqual(['SSH', 'Files', 'Storage'])

    const none = connectMenuEntries({ ...linux01, services: [{ protocol: 'Ssms', port: null, label: null }] }, actions)
    expect(none).toEqual([{ kind: 'item', label: 'No services to connect with', detail: 'Edit the host to add one', disabled: true }])
  })
})
