import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import { firstLine, fromLocalInput, journalEntryMatches, rangeProblem, toLocalInput, type JournalEntry } from './journal'
import { declaredModule, denialMatches, suggestedName, type SelinuxDenial, type SelinuxStatus } from './selinux'

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

type Body = Record<string, unknown>

class FakeWebSocket {
  static instances: FakeWebSocket[] = []
  sent: string[] = []
  closed = false
  onopen: (() => void) | null = null
  onmessage: ((event: { data: string }) => void) | null = null
  onclose: (() => void) | null = null
  url: string

  constructor(url: string) {
    this.url = url
    FakeWebSocket.instances.push(this)
  }

  send(data: string) {
    this.sent.push(data)
  }

  close() {
    this.closed = true
  }

  receive(message: unknown) {
    act(() => this.onmessage?.({ data: JSON.stringify(message) }))
  }
}

beforeEach(() => {
  FakeWebSocket.instances = []
  vi.stubGlobal('WebSocket', FakeWebSocket)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

function renderWorkspace(routesOverride: Parameters<typeof mockFetch>[0]) {
  mockFetch({
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
  return userEvent.setup()
}

async function openTool(user: ReturnType<typeof userEvent.setup>, tool: RegExp, panel: string) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /app01/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
  await user.hover(screen.getByRole('menuitem', { name: tool }))
  await user.click(screen.getByRole('menuitem', { name: /^root/ }))
  return within(await screen.findByRole('tabpanel', { name: panel }))
}

const entry = (n: number, overrides: Partial<JournalEntry> = {}): JournalEntry => ({
  cursor: `s=${n}`,
  time: '2026-11-06T18:29:47Z',
  priority: 6,
  unit: 'sshd.service',
  identifier: 'sshd',
  pid: '123',
  hostname: 'app01',
  message: `Entry ${n}\nSecond line ${n}`,
  ...overrides,
})

const page = (entries: JournalEntry[], hasMore = false, limited = false) => jsonResponse(200, { status: 'ready', page: { entries, hasMore, limited }, hostKey: null })

/** Records each request for entries, answering with the next response. */
function journalRoutes(...answers: Response[]) {
  const bodies: Body[] = []
  return {
    bodies,
    routes: {
      'POST /api/journal/entries': (_url: string, init: RequestInit) => {
        bodies.push(JSON.parse(String(init.body)) as Body)
        return answers[Math.min(bodies.length, answers.length) - 1].clone()
      },
    },
  }
}

describe('journal', () => {
  it('asks to trust the host key, then reads the last hour, newest first, and opens an entry', async () => {
    const { bodies, routes: fake } = journalRoutes(
      jsonResponse(200, { status: 'trustRequired', page: null, hostKey: { algorithm: 'ssh-ed25519', fingerprint: 'SHA256:abc' } }),
      page([entry(2, { priority: 3 }), entry(1)]),
    )
    const user = renderWorkspace(fake)

    const panel = await openTool(user, /^Journal/, 'app01 journal')
    await user.click(await panel.findByRole('button', { name: 'Trust and connect' }))

    expect(await panel.findByRole('button', { name: 'Entry 2' })).toBeInTheDocument()
    expect(panel.getByText('Error')).toBeInTheDocument()
    expect(bodies[1]).toMatchObject({ hostId: 'h-app01', credentialId: 'c-root', trustFingerprint: 'SHA256:abc' })
    expect(Math.abs(Date.now() - 3_600_000 - new Date(String(bodies[1].since)).getTime())).toBeLessThan(120_000)
    expect(bodies[1].until).toBeUndefined()

    expect(panel.queryByText(/Second line 2/)).not.toBeInTheDocument()
    await user.click(panel.getByRole('button', { name: 'Entry 2' }))
    expect(panel.getByText(/Second line 2/)).toBeInTheDocument()
    expect(panel.getByText('sshd.service')).toBeInTheDocument()
  })

  it('sends its start, stop and filters on Apply', async () => {
    const { bodies, routes: fake } = journalRoutes(page([entry(1)]))
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^Journal/, 'app01 journal')
    await panel.findByRole('button', { name: 'Entry 1' })

    fireEvent.change(panel.getByLabelText('Start'), { target: { value: '2026-11-06T10:00' } })
    fireEvent.change(panel.getByLabelText('Stop'), { target: { value: '2026-11-06T11:30' } })
    await user.selectOptions(panel.getByLabelText('Priority'), 'Error and worse')
    await user.type(panel.getByLabelText('Unit'), 'nginx.service')
    await user.type(panel.getByLabelText('Program'), 'nginx')
    await user.type(panel.getByLabelText('Search'), 'failed')
    expect(bodies).toHaveLength(1)

    await user.click(panel.getByRole('button', { name: 'Apply' }))

    await waitFor(() => expect(bodies).toHaveLength(2))
    expect(bodies[1]).toMatchObject({
      since: new Date('2026-11-06T10:00').toISOString(),
      until: new Date('2026-11-06T11:30').toISOString(),
      maxPriority: 3,
      unit: 'nginx.service',
      identifier: 'nginx',
      search: 'failed',
    })
  })

  it('refuses a start after the stop', async () => {
    const { bodies, routes: fake } = journalRoutes(page([entry(1)]))
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^Journal/, 'app01 journal')
    await panel.findByRole('button', { name: 'Entry 1' })

    fireEvent.change(panel.getByLabelText('Start'), { target: { value: '2026-11-06T12:00' } })
    fireEvent.change(panel.getByLabelText('Stop'), { target: { value: '2026-11-06T11:00' } })

    expect(panel.getByText('The start is after the stop.')).toBeInTheDocument()
    expect(panel.getByRole('button', { name: 'Apply' })).toBeDisabled()
    expect(bodies).toHaveLength(1)
  })

  it('loads older entries, and finds text among those loaded', async () => {
    const { bodies, routes: fake } = journalRoutes(page([entry(3), entry(2, { unit: 'nginx.service' })], true), page([entry(1)]))
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^Journal/, 'app01 journal')
    await panel.findByRole('button', { name: 'Entry 3' })
    expect(panel.getByText('2 entries, more are older')).toBeInTheDocument()

    await user.click(panel.getByRole('button', { name: 'Load older entries' }))

    expect(await panel.findByRole('button', { name: 'Entry 1' })).toBeInTheDocument()
    expect(bodies[1]).toMatchObject({ after: 's=2' })
    expect(panel.queryByRole('button', { name: 'Load older entries' })).not.toBeInTheDocument()

    await user.type(panel.getByLabelText('Find in the entries below'), 'nginx')
    expect(panel.getByText('1 of 3 entries')).toBeInTheDocument()
    expect(panel.queryByRole('button', { name: 'Entry 3' })).not.toBeInTheDocument()
    expect(bodies).toHaveLength(2)
  })

  it('follows new entries from the newest shown, until told to stop', async () => {
    const { routes: fake } = journalRoutes(page([entry(1)]))
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^Journal/, 'app01 journal')
    await panel.findByRole('button', { name: 'Entry 1' })
    await user.type(panel.getByLabelText('Unit'), 'x')
    await user.click(panel.getByRole('button', { name: 'Apply' }))
    await waitFor(() => expect(panel.getByRole('button', { name: 'Follow' })).toBeEnabled())

    await user.click(panel.getByRole('button', { name: 'Follow' }))

    const socket = FakeWebSocket.instances[0]
    expect(socket.url).toContain('/api/journal/follow')
    act(() => socket.onopen?.())
    expect(JSON.parse(socket.sent[0])).toMatchObject({ type: 'start', hostId: 'h-app01', credentialId: 'c-root', after: 's=1', unit: 'x' })

    socket.receive({ type: 'connected', target: 'root@10.0.0.20:22' })
    socket.receive({ type: 'entry', entry: entry(2) })
    expect(panel.getByRole('button', { name: 'Entry 2' })).toBeInTheDocument()
    expect(panel.getAllByRole('button', { name: /^Entry/ }).map((b) => b.textContent)).toEqual(['Entry 2', 'Entry 1'])

    await user.click(panel.getByRole('button', { name: 'Following…' }))
    expect(socket.closed).toBe(true)
  })

  it('can’t follow with a stop time, and says when following fails', async () => {
    const { routes: fake } = journalRoutes(page([entry(1)]))
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^Journal/, 'app01 journal')
    await panel.findByRole('button', { name: 'Entry 1' })

    await user.click(panel.getByRole('button', { name: 'Follow' }))
    const socket = FakeWebSocket.instances[0]
    socket.receive({ type: 'error', code: 'connection_failed', message: 'The connection to the host was lost.' })
    expect(panel.getByText('The connection to the host was lost.')).toBeInTheDocument()
    expect(panel.getByRole('button', { name: 'Follow' })).toBeInTheDocument()

    fireEvent.change(panel.getByLabelText('Start'), { target: { value: '2026-11-06T10:00' } })
    fireEvent.change(panel.getByLabelText('Stop'), { target: { value: '2026-11-06T11:00' } })
    await user.click(panel.getByRole('button', { name: 'Apply' }))
    await waitFor(() => expect(panel.getByRole('button', { name: 'Follow' })).toBeDisabled())
  })

  it('keeps the tab when one read fails, and says when it reads only part of the journal', async () => {
    const { routes: fake } = journalRoutes(
      page([entry(1)], false, true),
      jsonResponse(502, { detail: 'The search isn’t a valid regular expression.', code: 'connection_failed' }),
    )
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^Journal/, 'app01 journal')
    await panel.findByRole('button', { name: 'Entry 1' })
    expect(panel.getByText(/can only read part of the journal/)).toBeInTheDocument()

    await user.type(panel.getByLabelText('Search'), '(')
    await user.click(panel.getByRole('button', { name: 'Apply' }))

    expect(await panel.findByText('The search isn’t a valid regular expression.')).toBeInTheDocument()
    expect(panel.getByLabelText('Search')).toHaveValue('(')
  })

  it('shows a first failure with a way to try again', async () => {
    const { routes: fake } = journalRoutes(jsonResponse(502, { detail: 'This host has no journalctl (systemd).', code: 'connection_failed' }), page([entry(1)]))
    const user = renderWorkspace(fake)

    const panel = await openTool(user, /^Journal/, 'app01 journal')

    expect(await panel.findByText('This host has no journalctl (systemd).')).toBeInTheDocument()
    await user.click(panel.getByRole('button', { name: 'Try again' }))
    expect(await panel.findByRole('button', { name: 'Entry 1' })).toBeInTheDocument()
  })
})

const denial = (eventId: number, overrides: Partial<SelinuxDenial> = {}): SelinuxDenial => ({
  eventId,
  time: '2026-11-06T18:00:00Z',
  type: 'AVC',
  blocked: true,
  permissions: ['read'],
  process: 'nginx',
  pid: '123',
  target: `index${eventId}.html`,
  targetClass: 'file',
  sourceContext: 'system_u:system_r:httpd_t:s0',
  targetContext: 'unconfined_u:object_r:user_home_t:s0',
  raw: `type=AVC msg=audit(1:${eventId}): avc: denied { read } comm="nginx"`,
  ...overrides,
})

const status = (overrides: Partial<SelinuxStatus> = {}): SelinuxStatus => ({
  mode: 'Enforcing',
  modules: [
    { priority: 100, name: 'base', format: 'pp' },
    { priority: 100, name: 'httpd', format: 'pp' },
    { priority: 400, name: 'my_fix', format: 'pp' },
  ],
  denials: [denial(2, { blocked: false, process: 'cat', target: '/etc/shadow' }), denial(1)],
  denialsTruncated: false,
  auditProblem: null,
  ...overrides,
})

const ready = (selinux: SelinuxStatus) => jsonResponse(200, { status: 'ready', selinux, hostKey: null })

function selinuxRoutes(overrides: Parameters<typeof mockFetch>[0] = {}, current: () => SelinuxStatus = () => status()) {
  const calls: { url: string; body: Body }[] = []
  const record = (url: string, init: RequestInit) => calls.push({ url, body: JSON.parse(String(init.body)) as Body })
  return {
    calls,
    routes: {
      'POST /api/selinux/status': (url: string, init: RequestInit) => {
        record(url, init)
        return ready(current())
      },
      ...overrides,
    },
    record,
  }
}

const generated = 'module nginx_fix 1.0;\n\nrequire {\n\ttype httpd_t;\n}\n\nallow httpd_t user_home_t:file read;\n'

describe('SELinux', () => {
  it('shows the mode, blocks and policies in one place', async () => {
    const { routes: fake, calls } = selinuxRoutes()
    const user = renderWorkspace(fake)

    const panel = await openTool(user, /^SELinux/, 'app01 SELinux')

    expect(await panel.findByRole('heading', { name: 'Enforcing' })).toBeInTheDocument()
    expect(panel.getByText('1 blocked, 1 logged only')).toBeInTheDocument()
    expect(panel.getByRole('button', { name: '/etc/shadow' })).toBeInTheDocument()
    expect(within(panel.getByRole('region', { name: 'Policies' })).getByText('my_fix')).toBeInTheDocument()
    // The distribution's are counted, not listed, and can't be removed.
    expect(panel.getByText('2 from the distribution')).toBeInTheDocument()
    expect(panel.queryByText(/base, httpd/)).not.toBeInTheDocument()
    expect(panel.getAllByRole('button', { name: 'Remove' })).toHaveLength(1)
    await user.click(panel.getByRole('button', { name: 'Show the distribution’s modules' }))
    expect(panel.getByText('base, httpd')).toBeInTheDocument()

    expect(Math.abs(Date.now() - 24 * 3_600_000 - new Date(String(calls[0].body.since)).getTime())).toBeLessThan(120_000)
  })

  it('reads its blocks for a start and stop, and finds text among them', async () => {
    const { routes: fake, calls } = selinuxRoutes()
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^SELinux/, 'app01 SELinux')
    await panel.findByRole('heading', { name: 'Enforcing' })

    fireEvent.change(panel.getByLabelText('Start'), { target: { value: '2026-11-06T10:00' } })
    fireEvent.change(panel.getByLabelText('Stop'), { target: { value: '2026-11-06T11:00' } })
    await user.click(panel.getByRole('button', { name: 'Apply' }))

    await waitFor(() => expect(calls).toHaveLength(2))
    expect(calls[1].body).toMatchObject({ since: new Date('2026-11-06T10:00').toISOString(), until: new Date('2026-11-06T11:00').toISOString() })

    await user.type(panel.getByLabelText('Find'), 'event-not-there')
    expect(panel.getByText('Nothing was denied in this time range.')).toBeInTheDocument()
  })

  it('makes a policy from selected denials, shows it, and installs what was read', async () => {
    const { routes: fake, calls, record } = selinuxRoutes({
      'POST /api/selinux/generate': (url: string, init: RequestInit) => {
        record(url, init)
        return jsonResponse(200, { status: 'ready', selinux: null, hostKey: null, source: generated })
      },
      'POST /api/selinux/install': (url: string, init: RequestInit) => {
        record(url, init)
        return jsonResponse(200, { status: 'ready', selinux: null, hostKey: null, message: 'Installed nginx_fix.' })
      },
    })
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^SELinux/, 'app01 SELinux')
    await panel.findByRole('heading', { name: 'Enforcing' })
    expect(panel.getByRole('button', { name: 'Create policy from selected' })).toBeDisabled()

    await user.click(panel.getByRole('checkbox', { name: /read by nginx/ }))
    await user.click(panel.getByRole('button', { name: 'Create policy from selected (1)' }))

    const naming = within(screen.getByRole('dialog', { name: 'Create a policy from these denials' }))
    expect(naming.getByLabelText('Policy name')).toHaveValue('nkeeper_httpd_t')
    await user.clear(naming.getByLabelText('Policy name'))
    await user.type(naming.getByLabelText('Policy name'), 'nginx_fix')
    await user.click(naming.getByRole('button', { name: 'Generate' }))

    const review = within(await screen.findByRole('dialog', { name: 'Install nginx_fix?' }))
    expect(calls.find((c) => c.url.includes('generate'))?.body).toMatchObject({ name: 'nginx_fix', eventIds: [1] })
    expect(calls.some((c) => c.url.includes('install'))).toBe(false)
    expect(review.getByLabelText('Policy source (.te)')).toHaveValue(generated)

    await user.type(review.getByLabelText('Policy source (.te)'), '# reviewed')
    await user.click(review.getByRole('button', { name: 'Install' }))

    expect(await panel.findByText('Installed nginx_fix.')).toBeInTheDocument()
    expect(calls.find((c) => c.url.includes('install'))?.body.source).toBe(`${generated}# reviewed`)
    await waitFor(() => expect(calls.filter((c) => c.url.includes('status'))).toHaveLength(2))
  })

  it('adds a written policy, needing its name line, and refusing a distribution module’s name', async () => {
    const { routes: fake } = selinuxRoutes({
      'POST /api/selinux/install': () => jsonResponse(502, { detail: 'The policy wasn’t installed: syntax error', code: 'connection_failed' }),
    })
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^SELinux/, 'app01 SELinux')
    await panel.findByRole('heading', { name: 'Enforcing' })

    await user.click(panel.getByRole('button', { name: 'Add policy' }))
    const dialog = within(screen.getByRole('dialog', { name: 'Install my_policy?' }))
    const source = dialog.getByLabelText('Policy source (.te)')

    fireEvent.change(source, { target: { value: 'allow a b:c d;' } })
    expect(dialog.getByText(/must start with its name and version/)).toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Install' })).toBeDisabled()

    fireEvent.change(source, { target: { value: 'module httpd 1.0;' } })
    expect(dialog.getByText('The distribution has a module called httpd. Choose another name.')).toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Install' })).toBeDisabled()

    fireEvent.change(source, { target: { value: 'module my_fix 2.0;' } })
    expect(dialog.getByText(/This replaces the my_fix already installed/)).toBeInTheDocument()

    await user.click(dialog.getByRole('button', { name: 'Install' }))
    // It stays open with the host's reason, to fix the source.
    expect(await dialog.findByText('The policy wasn’t installed: syntax error')).toBeInTheDocument()
    expect(source).toHaveValue('module my_fix 2.0;')
  })

  it('asks before removing a policy', async () => {
    const { routes: fake, calls, record } = selinuxRoutes({
      'POST /api/selinux/remove': (url: string, init: RequestInit) => {
        record(url, init)
        return jsonResponse(200, { status: 'ready', selinux: null, hostKey: null, message: 'Removed my_fix.' })
      },
    })
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^SELinux/, 'app01 SELinux')
    await panel.findByRole('heading', { name: 'Enforcing' })

    await user.click(panel.getByRole('button', { name: 'Remove' }))
    const dialog = within(screen.getByRole('dialog', { name: 'Remove my_fix?' }))
    expect(calls.some((c) => c.url.includes('remove'))).toBe(false)
    await user.click(dialog.getByRole('button', { name: 'Remove' }))

    expect(await panel.findByText('Removed my_fix.')).toBeInTheDocument()
    expect(calls.find((c) => c.url.includes('remove'))?.body).toMatchObject({ name: 'my_fix', priority: 400 })
  })

  it('says when a host has no SELinux, and why denials can’t be read', async () => {
    let current = status({ mode: null, modules: [], denials: [] })
    const { routes: fake } = selinuxRoutes({}, () => current)
    const user = renderWorkspace(fake)
    const panel = await openTool(user, /^SELinux/, 'app01 SELinux')
    expect(await panel.findByText(/has no SELinux tools/)).toBeInTheDocument()

    current = status({ denials: [], auditProblem: 'This host has no audit log, so denials can’t be read.' })
    await user.click(panel.getByRole('button', { name: 'Refresh' }))

    expect(await panel.findByText('This host has no audit log, so denials can’t be read.')).toBeInTheDocument()
    expect(panel.queryByText('Nothing was denied in this time range.')).not.toBeInTheDocument()
  })
})

describe('helpers', () => {
  it('keeps a time through the datetime field', () => {
    const value = toLocalInput(new Date(2026, 10, 6, 9, 5))
    expect(value).toBe('2026-11-06T09:05')
    expect(fromLocalInput(value)).toBe(new Date(2026, 10, 6, 9, 5).toISOString())
    expect(fromLocalInput('')).toBeUndefined()
    expect(fromLocalInput('nonsense')).toBeUndefined()
  })

  it('checks a range, allowing either end to be empty', () => {
    expect(rangeProblem('2026-11-06T10:00', '2026-11-06T11:00')).toBeNull()
    expect(rangeProblem('2026-11-06T10:00', '2026-11-06T10:00')).toBeNull()
    expect(rangeProblem('', '2026-11-06T11:00')).toBeNull()
    expect(rangeProblem('2026-11-06T10:00', '')).toBeNull()
    expect(rangeProblem('2026-11-06T12:00', '2026-11-06T11:00')).not.toBeNull()
  })

  it('finds text in journal entries and denials, ignoring case', () => {
    expect(journalEntryMatches(entry(1), 'SSHD.SERVICE')).toBe(true)
    expect(journalEntryMatches(entry(1), 'second line 1')).toBe(true)
    expect(journalEntryMatches(entry(1), 'nothing')).toBe(false)
    expect(journalEntryMatches(entry(1), '  ')).toBe(true)
    expect(denialMatches(denial(1), 'NGINX')).toBe(true)
    expect(denialMatches(denial(1), 'nothing')).toBe(false)
  })

  it('takes the first line with text', () => {
    expect(firstLine('\n  \nHello\nWorld')).toBe('Hello')
  })

  it('reads a policy’s name, past comments', () => {
    expect(declaredModule('# note\n\nmodule fix 1.0;\n')).toBe('fix')
    expect(declaredModule('allow a b:c d;')).toBeNull()
    expect(declaredModule('')).toBeNull()
  })

  it('suggests a name from the denied domain', () => {
    expect(suggestedName([denial(1)])).toBe('nkeeper_httpd_t')
    expect(suggestedName([denial(1, { sourceContext: null })])).toBe('nkeeper_local')
  })
})
