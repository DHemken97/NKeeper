import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import { sessionOrigin } from './webSessionOrigin'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const router1: Host = {
  id: 'h-router',
  name: 'router',
  hostname: null,
  ipAddress: '192.168.1.1',
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
  services: [
    { protocol: 'Https', port: null, label: 'Admin UI' },
    { protocol: 'Http', port: 8080, label: null },
  ],
  credentials: [],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const ready = (id: string) => ({
  status: 'ready',
  sessionId: id,
  launchPath: '/.nkeeper/launch?t=token',
  baseDomain: null,
  target: 'https://192.168.1.1/',
  certificate: null,
})

let openSpy: ReturnType<typeof vi.fn>

beforeEach(() => {
  openSpy = vi.fn(() => ({}) as Window)
  vi.stubGlobal('open', openSpy)
})

afterEach(() => vi.unstubAllGlobals())

function renderWorkspace(routesOverride: Parameters<typeof mockFetch>[0]) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [router1]),
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

/** The session's frame, once it appears. */
function sessionFrame() {
  return waitFor(() => {
    const frame = document.querySelector('iframe')
    expect(frame).not.toBeNull()
    return frame!
  })
}

async function connect(user: ReturnType<typeof userEvent.setup>, service: RegExp, mode = 'Through NKeeper') {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /router/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: service }))
  await user.click(screen.getByRole('menuitem', { name: new RegExp(`^${mode}`) }))
}

describe('web sessions', () => {
  it('shows the host’s web interface inside the tab, served by NKeeper from its own origin', async () => {
    let body: unknown
    const { user } = renderWorkspace({
      'POST /api/web/sessions': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, ready('0123456789abcdef0123456789abcdef'))
      },
    })

    await connect(user, /^HTTPS · Admin UI/)

    const frame = await screen.findByTitle(/router — https:\/\/192\.168\.1\.1/)
    expect(body).toEqual({ hostId: 'h-router', serviceIndex: 0 })
    expect(frame).toHaveAttribute('src', 'http://nkw-0123456789abcdef0123456789abcdef.web.localhost:3000/.nkeeper/launch?t=token')
    expect(frame.getAttribute('sandbox')).not.toContain('allow-top-navigation')
    expect(openSpy).not.toHaveBeenCalled()
    expect(screen.getByRole('tab', { name: /router.*HTTPS/, selected: true })).toBeInTheDocument()
  })

  it('opens the host directly in a new browser tab when asked, without NKeeper in between', async () => {
    const { user, fetchMock } = renderWorkspace({})

    await connect(user, /^HTTP:8080/, 'Directly in a new tab')

    expect(openSpy).toHaveBeenCalledWith('http://192.168.1.1:8080/', '_blank', 'noopener,noreferrer')
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/api/web/sessions'))).toBe(false)
    expect(screen.queryByRole('tab', { name: /router/ })).not.toBeInTheDocument()
  })

  it('asks to trust a self-signed certificate, then retries with that fingerprint', async () => {
    const bodies: unknown[] = []
    const { user } = renderWorkspace({
      'POST /api/web/sessions': (_url, init) => {
        bodies.push(JSON.parse(String(init.body)))
        return bodies.length === 1
          ? jsonResponse(200, {
              status: 'trustRequired',
              sessionId: null,
              launchPath: null,
              baseDomain: null,
              target: 'https://192.168.1.1/',
              certificate: { fingerprint: 'AA:BB', subject: 'CN=router', issuer: 'CN=router', expires: '2030-01-01T00:00:00Z' },
            })
          : jsonResponse(200, ready('ffffffffffffffffffffffffffffffff'))
      },
    })

    await connect(user, /^HTTPS · Admin UI/)
    const prompt = await screen.findByRole('alertdialog', { name: 'Trust this certificate?' })
    expect(within(prompt).getByText('AA:BB')).toBeInTheDocument()
    expect(within(prompt).getByText('CN=router')).toBeInTheDocument()
    await user.click(within(prompt).getByRole('button', { name: 'Trust and connect' }))

    expect(await sessionFrame()).toHaveAttribute('src', expect.stringContaining('nkw-ffffffffffffffffffffffffffffffff'))
    expect(bodies[1]).toEqual({ hostId: 'h-router', serviceIndex: 0, trustFingerprint: 'AA:BB' })
  })

  it('“End session” ends it on the server and closes the tab, without opening anything', async () => {
    const closed = vi.fn(() => jsonResponse(204))
    const { user } = renderWorkspace({
      'POST /api/web/sessions': () => jsonResponse(200, ready('0123456789abcdef0123456789abcdef')),
      'DELETE /api/web/sessions/0123456789abcdef0123456789abcdef': closed,
    })

    await connect(user, /^HTTP:8080/)
    await sessionFrame()
    await user.click(screen.getByRole('button', { name: 'End session' }))

    await waitFor(() => expect(closed).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('tab', { name: /router/ })).not.toBeInTheDocument()
    expect(openSpy).not.toHaveBeenCalled()
  })

  it('explains a changed certificate', async () => {
    const { user } = renderWorkspace({
      'POST /api/web/sessions': () =>
        jsonResponse(409, { status: 409, code: 'host_key_changed', detail: 'The TLS certificate of 192.168.1.1:443 has changed since it was trusted.' }),
    })

    await connect(user, /^HTTPS · Admin UI/)

    expect(await screen.findByRole('alert')).toHaveTextContent('has changed since it was trusted')
    expect(screen.getByRole('button', { name: 'Show host details' })).toBeInTheDocument()
  })
})

describe('sessionOrigin', () => {
  it('uses web.<host> by default, or the configured base domain', () => {
    const location = { protocol: 'https:', hostname: 'nkeeper.example.com', port: '' }
    expect(sessionOrigin('abc', null, location)).toBe('https://nkw-abc.web.nkeeper.example.com')
    expect(sessionOrigin('abc', 'nkeeper-web.example.net', location)).toBe('https://nkw-abc.nkeeper-web.example.net')
  })

  it('needs a host name or a base domain when NKeeper is opened by IP address', () => {
    expect(sessionOrigin('abc', null, { protocol: 'http:', hostname: '10.0.0.5', port: '8080' })).toBeNull()
    expect(sessionOrigin('abc', null, { protocol: 'http:', hostname: '[::1]', port: '8080' })).toBeNull()
    expect(sessionOrigin('abc', null, { protocol: 'http:', hostname: 'cafe', port: '' })).toBe('http://nkw-abc.web.cafe')
    expect(sessionOrigin('abc', 'web.example.net', { protocol: 'http:', hostname: '10.0.0.5', port: '8080' })).toBe('http://nkw-abc.web.example.net:8080')
  })
})
