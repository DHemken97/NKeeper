import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import { eventMatches, firstLine, validEventId, type EventLogEntry } from './eventLog'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const win01: Host = {
  id: 'h-win01',
  name: 'win01',
  hostname: null,
  ipAddress: '10.0.0.30',
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
  services: [{ protocol: 'Rpc', port: null, label: null }],
  credentials: [{ id: 'c-admin', label: null, username: 'administrator', domain: 'CORP', notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const event = (recordNumber: number, overrides: Partial<EventLogEntry> = {}): EventLogEntry => ({
  recordNumber,
  timeGenerated: '2026-11-06T18:29:47+00:00',
  level: 'Error',
  eventId: 7001,
  source: 'Service Control Manager',
  category: 'None',
  user: 'SYSTEM',
  computer: 'WIN01',
  message: `Event ${recordNumber} failed.\r\nSecond line ${recordNumber}.`,
  ...overrides,
})

const logs = { logs: [{ name: 'Application', records: 1200 }, { name: 'System', records: 45439 }] }

type Body = Record<string, unknown>

function renderWorkspace(routesOverride: Parameters<typeof mockFetch>[0], hosts: Host[] = [win01]) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, hosts),
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

async function hoverToEventLog(user: ReturnType<typeof userEvent.setup>) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /win01/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
  return screen.getByRole('menuitem', { name: /^Event Log Viewer/ })
}

async function openEventLog(user: ReturnType<typeof userEvent.setup>) {
  await user.hover(await hoverToEventLog(user))
  await user.click(screen.getByRole('menuitem', { name: /administrator/ }))
  return screen.findByRole('tabpanel', { name: 'win01 event log' })
}

/** Records the bodies of requests for events, answering each with the next page. */
function eventRoutes(pages: { events: EventLogEntry[]; hasMore: boolean }[]) {
  const bodies: Body[] = []
  return {
    bodies,
    routes: {
      'POST /api/eventlog/logs': () => jsonResponse(200, logs),
      'POST /api/eventlog/events': (_url: string, init: RequestInit) => {
        bodies.push(JSON.parse(String(init.body)) as Body)
        return jsonResponse(200, pages[Math.min(bodies.length, pages.length) - 1])
      },
    },
  }
}

describe('event log viewer', () => {
  it('opens under Tools with the saved login, reads System by default, and opens an event to its whole message', async () => {
    const { bodies, routes: fake } = eventRoutes([{ events: [event(12), event(11)], hasMore: false }])
    const { user } = renderWorkspace(fake)

    const panel = within(await openEventLog(user))

    expect(await panel.findByRole('button', { name: 'Event 12 failed.' })).toBeInTheDocument()
    expect(panel.getByLabelText('Log')).toHaveValue('System')
    expect(bodies[0]).toMatchObject({ hostId: 'h-win01', serviceIndex: 0, credentialId: 'c-admin', log: 'System', levels: [] })
    // The last 24 hours by default.
    const since = new Date(String(bodies[0].since)).getTime()
    expect(Math.abs(Date.now() - 24 * 3_600_000 - since)).toBeLessThan(60_000)
    // Passwords stay on the server for saved logins.
    expect(bodies[0].password).toBeUndefined()

    expect(panel.queryByText(/Second line 12/)).not.toBeInTheDocument()
    await user.click(panel.getByRole('button', { name: 'Event 12 failed.' }))
    expect(panel.getByRole('button', { name: 'Event 12 failed.' })).toHaveAttribute('aria-expanded', 'true')
    expect(panel.getByText(/Second line 12/)).toBeInTheDocument()
    expect(panel.getByText('WIN01')).toBeInTheDocument()
  })

  it('reads again when a log, time range or level changes, and on Apply for source and event ID', async () => {
    const { bodies, routes: fake } = eventRoutes([{ events: [event(12)], hasMore: false }])
    const { user } = renderWorkspace(fake)
    const panel = within(await openEventLog(user))
    await panel.findByRole('button', { name: 'Event 12 failed.' })

    await user.selectOptions(panel.getByLabelText('Log'), 'Application')
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ log: 'Application' }))

    await user.selectOptions(panel.getByLabelText('Time'), 'All time')
    await waitFor(() => expect(bodies).toHaveLength(3))
    expect(bodies.at(-1)?.since).toBeUndefined()

    await user.click(panel.getByRole('checkbox', { name: 'Warning' }))
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ levels: ['Warning'] }))

    const before = bodies.length
    await user.type(panel.getByLabelText('Source'), 'Disk')
    await user.type(panel.getByLabelText('Event ID'), '7')
    expect(bodies).toHaveLength(before)
    await user.click(panel.getByRole('button', { name: 'Apply' }))
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ source: 'Disk', eventId: 7 }))
  })

  it('refuses an event ID Windows can’t have', async () => {
    const { bodies, routes: fake } = eventRoutes([{ events: [event(12)], hasMore: false }])
    const { user } = renderWorkspace(fake)
    const panel = within(await openEventLog(user))
    await panel.findByRole('button', { name: 'Event 12 failed.' })
    const before = bodies.length

    await user.type(panel.getByLabelText('Event ID'), '70000')

    expect(panel.getByText('Use 0 to 65535.')).toBeInTheDocument()
    expect(panel.getByRole('button', { name: 'Apply' })).toBeDisabled()
    expect(bodies).toHaveLength(before)
  })

  it('loads older events after the last one, and finds text among those loaded', async () => {
    const { bodies, routes: fake } = eventRoutes([
      { events: [event(12), event(11, { source: 'Disk' })], hasMore: true },
      { events: [event(10)], hasMore: false },
    ])
    const { user } = renderWorkspace(fake)
    const panel = within(await openEventLog(user))
    await panel.findByRole('button', { name: 'Event 12 failed.' })
    expect(panel.getByText('2 events, more are older')).toBeInTheDocument()

    await user.click(panel.getByRole('button', { name: 'Load older events' }))

    expect(await panel.findByRole('button', { name: 'Event 10 failed.' })).toBeInTheDocument()
    expect(bodies.at(-1)).toMatchObject({ beforeRecord: 11, log: 'System' })
    expect(panel.getByRole('button', { name: 'Event 12 failed.' })).toBeInTheDocument()
    expect(panel.queryByRole('button', { name: 'Load older events' })).not.toBeInTheDocument()

    await user.type(panel.getByLabelText('Find in the events below'), 'disk')
    expect(panel.getByText('1 of 3 events')).toBeInTheDocument()
    expect(panel.queryByRole('button', { name: 'Event 12 failed.' })).not.toBeInTheDocument()
    expect(panel.getByRole('button', { name: 'Event 11 failed.' })).toBeInTheDocument()
    expect(bodies).toHaveLength(2)
  })

  it('says why the Security log is missing', async () => {
    const { routes: fake } = eventRoutes([{ events: [], hasMore: false }])
    const { user } = renderWorkspace(fake)
    const panel = within(await openEventLog(user))

    expect(await panel.findByText(/The Security log isn’t listed/)).toBeInTheDocument()
    expect(await panel.findByText('No events match in this time range.')).toBeInTheDocument()
  })

  it('shows a refused login with a way to try again', async () => {
    let tries = 0
    const { user } = renderWorkspace({
      'POST /api/eventlog/logs': () => {
        tries += 1
        return tries === 1
          ? jsonResponse(400, { detail: 'Windows refused the login.', code: 'authentication_failed' })
          : jsonResponse(200, logs)
      },
      'POST /api/eventlog/events': () => jsonResponse(200, { events: [event(12)], hasMore: false }),
    })
    const panel = within(await openEventLog(user))

    expect(await panel.findByText('Windows refused the login.')).toBeInTheDocument()

    await user.click(panel.getByRole('button', { name: 'Try again' }))
    expect(await panel.findByRole('button', { name: 'Event 12 failed.' })).toBeInTheDocument()
  })

  it('keeps the tab open when one read fails', async () => {
    const { user } = renderWorkspace({
      'POST /api/eventlog/logs': () => jsonResponse(200, logs),
      'POST /api/eventlog/events': () => jsonResponse(502, { detail: 'The host didn’t finish reading in time.', code: 'timeout' }),
    })
    const panel = within(await openEventLog(user))

    expect(await panel.findByText('The host didn’t finish reading in time.')).toBeInTheDocument()
    expect(panel.getByLabelText('Log')).toBeInTheDocument()
    expect(panel.queryByText('No events match in this time range.')).not.toBeInTheDocument()
  })

  it('asks for a login, with the domain form, and sends it', async () => {
    const { bodies, routes: fake } = eventRoutes([{ events: [event(12)], hasMore: false }])
    const { user } = renderWorkspace(fake)
    await user.hover(await hoverToEventLog(user))
    await user.click(screen.getByRole('menuitem', { name: 'Enter credentials…' }))

    const dialog = within(screen.getByRole('dialog', { name: 'Enter credentials' }))
    expect(dialog.getByText('For a domain account: DOMAIN\\user')).toBeInTheDocument()
    await user.type(dialog.getByLabelText('Username'), 'LAB\\jane')
    await user.type(dialog.getByLabelText('Password'), 'typed')
    await user.click(dialog.getByRole('button', { name: 'Connect' }))

    await waitFor(() => expect(bodies).toHaveLength(1))
    expect(bodies[0]).toMatchObject({ username: 'LAB\\jane', password: 'typed' })
    expect(bodies[0].credentialId).toBeUndefined()
  })

  it('is greyed out behind a jump host', async () => {
    const { user } = renderWorkspace({}, [{ ...win01, jumpHostId: 'h-jump' }])

    const entry = await hoverToEventLog(user)

    expect(entry).toHaveTextContent('Not through a jump host yet')
    expect(entry).toHaveAttribute('aria-disabled', 'true')
  })

  it('isn’t offered on a host without an RPC service', async () => {
    const { user } = renderWorkspace({}, [{ ...win01, services: [{ protocol: 'Ssh', port: null, label: null }] }])

    fireEvent.contextMenu(await screen.findByRole('button', { name: /win01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))

    expect(screen.getByRole('menuitem', { name: /^Files/ })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /^Event Log Viewer/ })).not.toBeInTheDocument()
  })
})

describe('event log helpers', () => {
  it('takes the first line with text', () => {
    expect(firstLine('\r\n  \r\nThe service failed.\r\nDetail')).toBe('The service failed.')
    expect(firstLine('')).toBe('')
  })

  it('validates event IDs', () => {
    expect(['', ' ', '0', '65535', ' 7 '].every(validEventId)).toBe(true)
    expect(['65536', '-1', '1.5', 'abc', '1e3'].some(validEventId)).toBe(false)
  })

  it('finds text in any shown field, ignoring case', () => {
    expect(eventMatches(event(1), 'SERVICE control')).toBe(true)
    expect(eventMatches(event(1), '7001')).toBe(true)
    expect(eventMatches(event(1), 'second line 1')).toBe(true)
    expect(eventMatches(event(1), 'nothing like it')).toBe(false)
    expect(eventMatches(event(1), '  ')).toBe(true)
  })
})
