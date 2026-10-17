import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { auditQuery, parseCast, type AuditEvent } from './auditApi'

// The player writes into xterm.js, which needs a real browser; a fake records what it was given.
const xterm = vi.hoisted(() => {
  class FakeTerminal {
    static instances: FakeTerminal[] = []
    written = ''
    size: [number, number]
    constructor(options: { cols: number; rows: number }) {
      this.size = [options.cols, options.rows]
      FakeTerminal.instances.push(this)
    }
    open() {}
    dispose() {}
    reset() {
      this.written = ''
    }
    write(data: string) {
      this.written += data
    }
    resize(cols: number, rows: number) {
      this.size = [cols, rows]
    }
  }
  return { FakeTerminal }
})

vi.mock('@xterm/xterm', () => ({ Terminal: xterm.FakeTerminal }))

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }
const bob = { ...admin, id: 'u2', userName: 'bob', roles: [] }

function event(id: number, action: string, fields: Partial<AuditEvent> = {}): AuditEvent {
  return {
    id,
    occurredAtUtc: '2026-10-17T09:00:00Z',
    userId: 'u1',
    userName: 'admin',
    action,
    hostId: null,
    hostName: null,
    detail: null,
    sessionId: null,
    clientAddress: '10.0.0.9',
    recordingId: null,
    ...fields,
  }
}

function renderAt(path: string, routesOverride: Parameters<typeof mockFetch>[0], user = admin) {
  const fetchMock = mockFetch({ 'GET /api/auth/me': () => jsonResponse(200, user), ...routesOverride })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { fetchMock, router, person: userEvent.setup() }
}

describe('audit log', () => {
  it('lists what happened, links sessions to their recordings, and pages back', async () => {
    const queries: string[] = []
    const { person } = renderAt('/reports/audit', {
      'GET /api/audit/events': (url) => {
        const query = new URL(url, 'http://localhost').searchParams
        queries.push(query.toString())
        return query.get('before')
          ? jsonResponse(200, { events: [event(1, 'auth.signin')], next: null })
          : jsonResponse(200, {
              events: [
                event(3, 'session.end', { hostId: 'h1', hostName: 'switch01', detail: 'Telnet · alice@10.0.0.2:23 · after 2m 3s', recordingId: 'r1' }),
                event(2, 'auth.signin_failed', { userId: null, userName: 'mallory' }),
              ],
              next: 2,
            })
      },
    })

    const table = await screen.findByRole('table')
    expect(within(table).getByText('Session ended')).toBeInTheDocument()
    expect(within(table).getByRole('link', { name: 'switch01' })).toHaveAttribute('href', '/?host=h1')
    expect(within(table).getByRole('link', { name: /Play recording/ })).toHaveAttribute('href', '/reports/audit/recordings/r1')
    expect(within(table).getByText('Sign-in failed').closest('tr')).toHaveTextContent('mallory')

    await person.click(screen.getByRole('button', { name: 'Load older' }))
    expect(await within(table).findByText('Signed in')).toBeInTheDocument()
    expect(queries.at(-1)).toContain('before=2')
    expect(screen.queryByRole('button', { name: 'Load older' })).not.toBeInTheDocument()

    await person.selectOptions(screen.getByLabelText('What happened'), 'Sessions')
    await waitFor(() => expect(queries.at(-1)).toContain('category=session'))
    expect(screen.getByRole('link', { name: 'Download CSV' })).toHaveAttribute('href', '/api/audit/events.csv?category=session')
  })

  it('is listed for administrators only', async () => {
    renderAt('/reports', { 'GET /api/hosts': () => jsonResponse(200, []) }, bob)

    expect(await screen.findByRole('link', { name: /IP address map/ })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Audit log/ })).not.toBeInTheDocument()
  })

  it('plays a recording back in a terminal of the session’s size', async () => {
    renderAt('/reports/audit/recordings/r1', {
      'GET /api/audit/recordings/r1': () =>
        jsonResponse(200, {
          id: 'r1',
          protocol: 'Telnet',
          userName: 'admin',
          hostName: 'switch01',
          startedAtUtc: '2026-10-17T09:00:00Z',
          endedAtUtc: '2026-10-17T09:02:03Z',
          size: 300,
          truncated: false,
        }),
      'GET /api/audit/recordings/r1/cast': () =>
        new Response(['{"version":2,"width":100,"height":30}', '[0.01,"o","login: "]', '[0.02,"r","120x40"]', '[0.03,"o","Welcome alice\\r\\n"]'].join('\n')),
    })

    expect(await screen.findByRole('heading', { name: 'Telnet session on switch01' })).toBeInTheDocument()
    await waitFor(() => expect(xterm.FakeTerminal.instances.at(-1)?.written).toBe('login: Welcome alice\r\n'))
    expect(xterm.FakeTerminal.instances.at(-1)?.size).toEqual([120, 40])
    expect(await screen.findByRole('button', { name: 'Replay' })).toBeInTheDocument()
  })

  it('reads asciicast files and builds whole-day date filters', () => {
    expect(parseCast('{"version":2,"width":80,"height":24}\n[0.5,"o","hi"]\n')).toEqual({ width: 80, height: 24, events: [[0.5, 'o', 'hi']] })
    const query = new URLSearchParams(auditQuery({ q: ' web ', from: '2026-10-01', to: '2026-10-01' }))
    expect(query.get('q')).toBe('web')
    expect(new Date(query.get('to')!).getTime() - new Date(query.get('from')!).getTime()).toBe(24 * 3600 * 1000)
  })
})
