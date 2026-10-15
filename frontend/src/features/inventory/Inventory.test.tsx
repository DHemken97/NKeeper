import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { AuthProvider } from '../auth/components/AuthProvider'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import type { Credential, HostSummary, Paged } from './types'

const adminUser = { id: '6f1c0d4e-0000-4000-8000-000000000001', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

function summary(overrides: Partial<HostSummary>): HostSummary {
  return {
    id: crypto.randomUUID(),
    name: 'host',
    hostname: null,
    ipAddress: '10.0.0.1',
    macAddress: null,
    group: null,
    tags: [],
    services: [],
    credentialCount: 0,
    updatedAtUtc: '2026-10-01T12:00:00Z',
    ...overrides,
  }
}

function page(items: HostSummary[]): Paged<HostSummary> {
  return { items, totalCount: items.length, page: 1, pageSize: 50 }
}

const credential: Credential = {
  id: 'c0000000-0000-4000-8000-000000000001',
  name: 'Domain admin',
  username: 'administrator',
  domain: 'CORP',
  notes: null,
  hasPassword: true,
  hasPrivateKey: false,
  hostCount: 2,
  createdAtUtc: '2026-10-01T12:00:00Z',
  updatedAtUtc: '2026-10-01T12:00:00Z',
  version: 7,
}

const lookups = {
  'GET /api/auth/me': () => jsonResponse(200, adminUser),
  'GET /api/groups': () => jsonResponse(200, [{ id: 'g1', name: 'Datacenter A', description: null, hostCount: 1 }]),
  'GET /api/tags': () => jsonResponse(200, [{ name: 'production', hostCount: 1 }]),
  'GET /api/credentials': () => jsonResponse(200, [credential]),
}

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { router, user: userEvent.setup() }
}

/** The query string of every call made to GET /api/hosts. */
function hostQueries(fetchMock: ReturnType<typeof mockFetch>): URLSearchParams[] {
  return fetchMock.mock.calls
    .map(([input, init]) => ({ url: new URL(String(input), 'http://localhost'), method: init?.method ?? 'GET' }))
    .filter(({ url, method }) => method === 'GET' && url.pathname === '/api/hosts')
    .map(({ url }) => url.searchParams)
}

describe('hosts page', () => {
  it('lists hosts with their address, group, services and tags', async () => {
    mockFetch({
      ...lookups,
      'GET /api/hosts': () =>
        jsonResponse(
          200,
          page([
            summary({
              name: 'sql01',
              hostname: 'sql01.corp.example.com',
              ipAddress: '10.0.0.15',
              group: { id: 'g1', name: 'Datacenter A' },
              tags: ['production'],
              services: [
                { protocol: 'Rdp', port: null, label: null },
                { protocol: 'Ssms', port: 14330, label: null },
              ],
              credentialCount: 1,
            }),
          ]),
        ),
    })

    renderAt('/hosts')

    const row = (await screen.findByRole('button', { name: 'sql01' })).closest('tr')!
    expect(within(row).getByText('sql01.corp.example.com')).toBeInTheDocument()
    expect(within(row).getByText('10.0.0.15')).toBeInTheDocument()
    expect(within(row).getByRole('button', { name: 'Datacenter A' })).toBeInTheDocument()
    expect(within(row).getByText('RDP')).toBeInTheDocument()
    expect(within(row).getByText('SSMS:14330')).toBeInTheDocument()
    expect(within(row).getByRole('button', { name: 'Filter by tag production' })).toBeInTheDocument()
    expect(screen.getByText('1 host')).toBeInTheDocument()
  })

  it('searches after typing pauses and sorts by clicking a column header', async () => {
    const fetchMock = mockFetch({ ...lookups, 'GET /api/hosts': () => jsonResponse(200, page([summary({ name: 'web01' })])) })
    const { user, router } = renderAt('/hosts')
    await screen.findByRole('button', { name: 'web01' })

    await user.type(screen.getByRole('searchbox', { name: 'Search hosts' }), 'web prod')

    await waitFor(() => expect(hostQueries(fetchMock).at(-1)?.get('search')).toBe('web prod'))
    // Debounced: typing 8 characters does not send 8 requests.
    expect(hostQueries(fetchMock).length).toBeLessThanOrEqual(3)
    expect(router.state.location.search).toBe('?q=web+prod')

    await user.click(screen.getByRole('button', { name: 'Name' }))
    await waitFor(() => expect(hostQueries(fetchMock).at(-1)?.get('direction')).toBe('desc'))
    expect(screen.getByRole('columnheader', { name: 'Name' })).toHaveAttribute('aria-sort', 'descending')

    await user.click(screen.getByRole('button', { name: 'Updated' }))
    await waitFor(() => expect(hostQueries(fetchMock).at(-1)?.get('sort')).toBe('updated'))
  })

  it('filters by a tag when it is clicked, and can clear filters', async () => {
    const fetchMock = mockFetch({
      ...lookups,
      'GET /api/hosts': () => jsonResponse(200, page([summary({ name: 'web01', tags: ['production'] })])),
    })
    const { user } = renderAt('/hosts')

    await user.click(await screen.findByRole('button', { name: 'Filter by tag production' }))
    await waitFor(() => expect(hostQueries(fetchMock).at(-1)?.get('tag')).toBe('production'))
    expect(screen.getByRole('combobox', { name: 'Tag' })).toHaveValue('production')

    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    await waitFor(() => expect(hostQueries(fetchMock).at(-1)?.has('tag')).toBe(false))
  })

  it('shows an empty state with a call to action when there are no hosts', async () => {
    mockFetch({ ...lookups, 'GET /api/hosts': () => jsonResponse(200, page([])) })

    renderAt('/hosts')

    expect(await screen.findByText('No hosts yet')).toBeInTheDocument()
  })

  it('creates a host with services, tags and credentials', async () => {
    let created: Record<string, unknown> | undefined
    mockFetch({
      ...lookups,
      ...antiforgeryRoute,
      'GET /api/hosts': () => jsonResponse(200, page([])),
      'POST /api/hosts': (_url, init) => {
        created = JSON.parse(String(init.body))
        return jsonResponse(201, { id: 'h1' })
      },
    })
    const { user } = renderAt('/hosts')

    await user.click((await screen.findAllByRole('button', { name: 'Add host' }))[0])
    const dialog = await screen.findByRole('dialog', { name: 'Add host' })
    await user.type(within(dialog).getByLabelText('Name'), 'app01')
    await user.type(within(dialog).getByLabelText('IP address'), '10.1.2.3')
    await user.selectOptions(within(dialog).getByLabelText('Group'), 'Datacenter A')
    await user.type(within(dialog).getByLabelText('Tags'), 'linux,web{Enter}')
    await user.click(within(dialog).getByRole('button', { name: 'Add service' }))
    await user.type(within(dialog).getByLabelText('Port for service 1'), '2222')
    await user.click(within(dialog).getByRole('button', { name: 'Add service' }))
    await user.click(await within(dialog).findByRole('checkbox', { name: /Domain admin/ }))
    await user.click(within(dialog).getByRole('button', { name: 'Add host' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(created).toEqual({
      name: 'app01',
      hostname: null,
      ipAddress: '10.1.2.3',
      macAddress: null,
      description: null,
      groupId: 'g1',
      tags: ['linux', 'web'],
      services: [
        { protocol: 'Ssh', port: 2222, label: null },
        { protocol: 'Rdp', port: null, label: null },
      ],
      credentialIds: [credential.id],
    })
  })

  it('requires a name and an address before saving', async () => {
    mockFetch({ ...lookups, 'GET /api/hosts': () => jsonResponse(200, page([])) })
    const { user } = renderAt('/hosts')

    await user.click((await screen.findAllByRole('button', { name: 'Add host' }))[0])
    const dialog = await screen.findByRole('dialog', { name: 'Add host' })
    await user.click(within(dialog).getByRole('button', { name: 'Add host' }))

    expect(within(dialog).getByLabelText('Name')).toHaveAccessibleDescription('Enter a name.')
    expect(within(dialog).getByLabelText('Hostname')).toHaveAccessibleDescription('Enter a hostname or an IP address.')
  })

  it('shows server validation errors next to the matching fields', async () => {
    mockFetch({
      ...lookups,
      ...antiforgeryRoute,
      'GET /api/hosts': () => jsonResponse(200, page([])),
      'POST /api/hosts': () =>
        jsonResponse(400, {
          status: 400,
          title: 'One or more validation errors occurred.',
          errors: { ipAddress: ['Enter a valid IPv4 or IPv6 address.'], 'services[0].port': ['Port must be between 1 and 65535.'] },
        }),
    })
    const { user } = renderAt('/hosts')

    await user.click((await screen.findAllByRole('button', { name: 'Add host' }))[0])
    const dialog = await screen.findByRole('dialog', { name: 'Add host' })
    await user.type(within(dialog).getByLabelText('Name'), 'bad')
    await user.type(within(dialog).getByLabelText('IP address'), '999.1.1.1')
    await user.click(within(dialog).getByRole('button', { name: 'Add service' }))
    await user.type(within(dialog).getByLabelText('Port for service 1'), '99999')
    await user.click(within(dialog).getByRole('button', { name: 'Add host' }))

    expect(await within(dialog).findByText('Enter a valid IPv4 or IPv6 address.')).toBeInTheDocument()
    expect(within(dialog).getByText('Port must be between 1 and 65535.')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Port for service 1')).toHaveAttribute('aria-invalid', 'true')
  })
})

describe('credentials page', () => {
  it('keeps the stored password unless a new one is entered', async () => {
    let sent: Record<string, unknown> | undefined
    mockFetch({
      ...lookups,
      ...antiforgeryRoute,
      [`PUT /api/credentials/${credential.id}`]: (_url, init) => {
        sent = JSON.parse(String(init.body))
        return jsonResponse(200, credential)
      },
    })
    const { user } = renderAt('/credentials')

    await user.click(await screen.findByRole('button', { name: 'Domain admin' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit credential' })
    expect(within(dialog).getByText('A password is stored. Leave empty to keep it.')).toBeInTheDocument()
    await user.clear(within(dialog).getByLabelText('Username'))
    await user.type(within(dialog).getByLabelText('Username'), 'admin2')
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(sent).toBeDefined())
    expect(sent).toEqual({ name: 'Domain admin', username: 'admin2', domain: 'CORP', notes: null, version: 7 })
    expect(sent).not.toHaveProperty('password')
  })

  it('filters the list as you type', async () => {
    mockFetch({
      ...lookups,
      'GET /api/credentials': () =>
        jsonResponse(200, [credential, { ...credential, id: 'c2', name: 'Linux root', username: 'root', domain: null }]),
    })
    const { user } = renderAt('/credentials')
    await screen.findByRole('button', { name: 'Linux root' })

    await user.type(screen.getByRole('searchbox', { name: 'Search credentials' }), 'corp')

    expect(screen.getByRole('button', { name: 'Domain admin' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Linux root' })).not.toBeInTheDocument()
  })
})
