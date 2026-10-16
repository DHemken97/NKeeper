import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from './types'

const adminUser = { id: '6f1c0d4e-0000-4000-8000-000000000001', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

function makeHost(overrides: Partial<Host>): Host {
  return {
    id: crypto.randomUUID(),
    name: 'host',
    hostname: null,
    ipAddress: '10.0.0.1',
    macAddress: null,
    description: null,
    isVirtualMachine: false,
    hypervisorId: null,
    virtualMachineId: null,
    hypervisorSeenAtUtc: null,
    cpuCores: null,
    memoryMiB: null,
    serialNumber: null,
    tags: [],
    services: [],
    credentials: [],
    trustedKeys: [],
    canManage: true,
    createdAtUtc: '2026-10-01T12:00:00Z',
    updatedAtUtc: '2026-10-01T12:00:00Z',
    version: 1,
    ...overrides,
  }
}

const sql01 = makeHost({
  id: 'h-sql01',
  name: 'sql01',
  hostname: 'sql01.corp.example.com',
  ipAddress: '10.0.0.15',
  tags: [
    { name: 'env', value: 'production' },
    { name: 'site', value: 'DC-A' },
  ],
  services: [
    { protocol: 'Rdp', port: null, label: null },
    { protocol: 'Ssms', port: 14330, label: 'Named instance' },
  ],
  credentials: [
    { id: 'c-admin', label: 'Local admin', username: 'administrator', domain: null, notes: null, hasPassword: true, hasPrivateKey: false },
  ],
  version: 41,
})
const web01 = makeHost({ id: 'h-web01', name: 'web01', hostname: 'web01', tags: [{ name: 'site', value: 'DC-A' }] })
const fw01 = makeHost({ id: 'h-fw01', name: 'fw01', ipAddress: '10.9.0.1', tags: [{ name: 'site', value: 'Branch' }] })
const laptop = makeHost({ id: 'h-laptop', name: 'laptop', hostname: 'laptop' })

function renderAt(path: string, routesOverride: Parameters<typeof mockFetch>[0] = {}) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, adminUser),
    'GET /api/hosts': () => jsonResponse(200, [fw01, laptop, sql01, web01]),
    ...routesOverride,
  })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { router, fetchMock, user: userEvent.setup() }
}

const tree = () => screen.getByRole('list', { name: 'Hosts' })
const hostNamesInTree = () =>
  within(tree())
    .queryAllByRole('button')
    .filter((b) => !b.hasAttribute('aria-expanded'))
    .map((b) => b.querySelector('span span')?.textContent)

describe('workspace', () => {
  it('lists every host in the tree and shows a selected host’s details', async () => {
    const { user, router } = renderAt('/')

    await user.click(await screen.findByRole('button', { name: /sql01/ }))

    expect(router.state.location.search).toBe('?host=h-sql01')
    const details = screen.getByRole('article', { name: 'sql01' })
    expect(within(details).getByText('sql01.corp.example.com · 10.0.0.15')).toBeInTheDocument()
    expect(within(details).getByText('Named instance')).toBeInTheDocument()
    expect(within(details).getByText('port 14330')).toBeInTheDocument()
    expect(within(details).getByText('port 3389 (default)')).toBeInTheDocument()
    expect(within(details).getByText('administrator')).toBeInTheDocument()
    expect(within(details).getByText('Password')).toBeInTheDocument()
    expect(hostNamesInTree()).toEqual(['fw01', 'laptop', 'sql01', 'web01'])
  })

  it('groups the tree by a tag from the URL, with untagged hosts last, and collapses groups', async () => {
    const { user } = renderAt('/?group=site')

    const dcA = await screen.findByRole('button', { name: /site:\s*DC-A\s*2/ })
    expect(screen.getByRole('button', { name: /site:\s*Branch\s*1/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /No site\s*1/ })).toBeInTheDocument()
    expect(hostNamesInTree()).toEqual(['fw01', 'sql01', 'web01', 'laptop'])

    await user.click(dcA)

    expect(dcA).toHaveAttribute('aria-expanded', 'false')
    expect(hostNamesInTree()).toEqual(['fw01', 'laptop'])
  })

  it('collapses and expands every group at once, nested ones included', async () => {
    const { user } = renderAt('/?group=site&group=role')
    await screen.findByRole('button', { name: /site:\s*DC-A/ })

    await user.click(screen.getByRole('button', { name: 'Collapse all groups' }))
    expect(hostNamesInTree()).toEqual([])
    // Opening a site shows its role groups, still collapsed (these hosts have no role tag).
    await user.click(screen.getByRole('button', { name: /site:\s*DC-A/ }))
    expect(screen.getAllByRole('button', { name: /No role/ })[0]).toHaveAttribute('aria-expanded', 'false')
    expect(hostNamesInTree()).toEqual([])

    await user.click(screen.getByRole('button', { name: 'Collapse all groups' }))
    await user.click(screen.getByRole('button', { name: 'Expand all groups' }))
    expect(hostNamesInTree()).toContain('sql01')
    expect(screen.queryByRole('button', { name: 'Expand all groups' })).not.toBeInTheDocument()
  })

  it('offers collapse all only when the tree is grouped', async () => {
    renderAt('/')
    await screen.findByRole('button', { name: /sql01/ })
    expect(screen.queryByRole('button', { name: 'Collapse all groups' })).not.toBeInTheDocument()
  })

  it('adds grouping levels with the group-by picker', async () => {
    const { user, router } = renderAt('/')
    await screen.findByRole('button', { name: /sql01/ })

    await user.selectOptions(screen.getByRole('combobox', { name: 'Group by' }), 'Tag: site')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Add grouping level' }), 'Service')

    expect(new URLSearchParams(router.state.location.search).getAll('group')).toEqual(['site', '@service'])
    expect(screen.getByRole('button', { name: /RDP\s*1/ })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Stop grouping by site' }))
    expect(new URLSearchParams(router.state.location.search).getAll('group')).toEqual(['@service'])
  })

  it('filters by free text and by name:value tag terms', async () => {
    const { user } = renderAt('/')
    const search = await screen.findByRole('searchbox', { name: 'Search hosts' })

    await user.type(search, 'site:dc')
    expect(hostNamesInTree()).toEqual(['sql01', 'web01'])

    await user.clear(search)
    await user.type(search, '10.9')
    expect(hostNamesInTree()).toEqual(['fw01'])
    expect(screen.getByText('1 of 4')).toBeInTheDocument()
  })

  it('filters the tree to a tag when its chip is clicked', async () => {
    const { user } = renderAt('/?host=h-sql01')

    await user.click(await screen.findByRole('button', { name: /env.*production/ }))

    expect(screen.getByRole('searchbox', { name: 'Search hosts' })).toHaveValue('env:production')
    expect(hostNamesInTree()).toEqual(['sql01'])
  })

  it('creates a host with tags, services and a credential', async () => {
    let body: Record<string, unknown> | undefined
    const created = makeHost({ id: 'h-new', name: 'app01', ipAddress: '10.1.2.3' })
    const { user, router } = renderAt('/', {
      'POST /api/hosts': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(201, created)
      },
    })

    await user.click(await screen.findByRole('button', { name: 'New host' }))
    const form = screen.getByRole('form', { name: 'New host' })
    await user.type(within(form).getByLabelText('Name'), 'app01')
    await user.type(within(form).getByLabelText('IP address'), '10.1.2.3')
    await user.click(within(form).getByRole('button', { name: 'Add tag' }))
    await user.type(within(form).getByLabelText('Name for tag 1'), 'site')
    await user.type(within(form).getByLabelText('Value for tag 1'), 'DC-A')
    await user.click(within(form).getByRole('button', { name: 'Add service' }))
    await user.click(within(form).getByRole('button', { name: 'Add credential' }))
    await user.type(within(form).getByLabelText('Username'), 'root')
    await user.type(within(form).getByLabelText('Password'), 's3cret!')
    await user.click(within(form).getByRole('button', { name: 'Create host' }))

    await waitFor(() => expect(router.state.location.search).toBe('?host=h-new'))
    expect(body).toEqual({
      name: 'app01',
      hostname: null,
      ipAddress: '10.1.2.3',
      macAddress: null,
      description: null,
      isVirtualMachine: false,
      hypervisorId: null,
      cpuCores: null,
      memoryMiB: null,
      serialNumber: null,
      tags: [{ name: 'site', value: 'DC-A' }],
      services: [{ protocol: 'Ssh', port: null, label: null }],
      credentials: [{ label: null, username: 'root', domain: null, notes: null, password: 's3cret!' }],
    })
    expect(await screen.findByRole('article', { name: 'app01' })).toBeInTheDocument()
    expect(hostNamesInTree()).toContain('app01')
  })

  it('edits a host, keeping stored secrets unless changed and dropping removed credentials', async () => {
    let body: Record<string, unknown> | undefined
    const { user } = renderAt('/?host=h-sql01&edit=1', {
      'PUT /api/hosts/h-sql01': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, { ...sql01, name: 'sql01-renamed', version: 42 })
      },
    })

    const form = await screen.findByRole('form', { name: 'Edit sql01' })
    expect(within(form).getByText('Stored. Leave empty to keep it.')).toBeInTheDocument()
    await user.click(within(form).getByRole('button', { name: 'Add credential' }))
    await user.type(within(form).getAllByLabelText('Username')[1], 'sa')
    await user.click(within(form).getAllByRole('button', { name: 'Remove' })[0])
    await user.click(within(form).getByRole('button', { name: 'Remove tag 1' }))
    await user.click(within(form).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(body).toBeDefined())
    expect(body).toMatchObject({
      version: 41,
      tags: [{ name: 'site', value: 'DC-A' }],
      credentials: [{ username: 'sa' }],
    })
    expect(await screen.findByRole('article', { name: 'sql01-renamed' })).toBeInTheDocument()
  })

  it('sends only the existing credential id when its secret is untouched', async () => {
    let body: { credentials: Record<string, unknown>[] } | undefined
    const { user } = renderAt('/?host=h-sql01&edit=1', {
      'PUT /api/hosts/h-sql01': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, sql01)
      },
    })

    await user.click(await screen.findByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(body).toBeDefined())
    expect(body!.credentials).toEqual([{ id: 'c-admin', label: 'Local admin', username: 'administrator', domain: null, notes: null }])
  })

  it('clones a host: a prefilled new host whose credentials are copied on the server', async () => {
    let body: Record<string, unknown> | undefined
    const created = makeHost({ id: 'h-sql02', name: 'sql02', ipAddress: '10.0.0.16' })
    const { user, router } = renderAt('/?host=h-sql01', {
      'POST /api/hosts': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(201, created)
      },
    })

    await user.click(await screen.findByRole('button', { name: 'Clone' }))
    const form = await screen.findByRole('form', { name: 'Clone sql01' })
    expect(within(form).getByLabelText('Name')).toHaveValue('sql01 (copy)')
    expect(within(form).getByText('Stored. Leave empty to keep it.')).toBeInTheDocument()
    await user.clear(within(form).getByLabelText('Name'))
    await user.type(within(form).getByLabelText('Name'), 'sql02')
    await user.clear(within(form).getByLabelText('Hostname'))
    await user.type(within(form).getByLabelText('Hostname'), 'sql02.corp.example.com')
    await user.clear(within(form).getByLabelText('IP address'))
    await user.type(within(form).getByLabelText('IP address'), '10.0.0.16')
    await user.click(within(form).getByRole('button', { name: 'Create host' }))

    await waitFor(() => expect(router.state.location.search).toBe('?host=h-sql02'))
    expect(body).toEqual({
      name: 'sql02',
      hostname: 'sql02.corp.example.com',
      ipAddress: '10.0.0.16',
      macAddress: null,
      description: null,
      isVirtualMachine: false,
      hypervisorId: null,
      cpuCores: null,
      memoryMiB: null,
      serialNumber: null,
      tags: [
        { name: 'env', value: 'production' },
        { name: 'site', value: 'DC-A' },
      ],
      services: [
        { protocol: 'Rdp', port: null, label: null, rdpSecurity: 'Auto' },
        { protocol: 'Ssms', port: 14330, label: 'Named instance' },
      ],
      credentials: [{ copyFromCredentialId: 'c-admin', label: 'Local admin', username: 'administrator', domain: null, notes: null }],
    })
  })

  it('offers Clone in the host menu, and Cancel returns to the original', async () => {
    const { user, router } = renderAt('/')

    fireEvent.contextMenu(await screen.findByRole('button', { name: /web01/ }))
    await user.click(screen.getByRole('menuitem', { name: /^Clone/ }))
    expect(await screen.findByRole('form', { name: 'Clone web01' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    await waitFor(() => expect(router.state.location.search).toBe('?host=h-web01'))
  })

  it('shows server validation errors on the matching rows', async () => {
    const { user } = renderAt('/?host=h-sql01&edit=1', {
      'PUT /api/hosts/h-sql01': () =>
        jsonResponse(400, {
          status: 400,
          errors: { 'tags[1].name': ['Tag names cannot contain a colon.'], ipAddress: ['Enter a valid IPv4 or IPv6 address.'] },
        }),
    })

    await user.click(await screen.findByRole('button', { name: 'Save changes' }))

    expect(await screen.findByText('Tag names cannot contain a colon.')).toBeInTheDocument()
    expect(screen.getByLabelText('Name for tag 2')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText('IP address')).toHaveAccessibleDescription('Enter a valid IPv4 or IPv6 address.')
  })

  it('deletes a host after confirmation', async () => {
    const { user, router } = renderAt('/?host=h-web01', { 'DELETE /api/hosts/h-web01': () => jsonResponse(204) })

    await user.click(await screen.findByRole('button', { name: 'Delete' }))
    await user.click(screen.getByRole('button', { name: 'Yes, delete' }))

    await waitFor(() => expect(router.state.location.search).toBe(''))
    expect(hostNamesInTree()).toEqual(['fw01', 'laptop', 'sql01'])
  })
})
