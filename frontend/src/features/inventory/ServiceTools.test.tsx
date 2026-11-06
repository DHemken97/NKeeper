import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { ServicesEditor } from './components/ServicesEditor'
import { newServiceDraft, toCloneDraft, toHostDraft, toHostInput, type ServiceDraft } from './drafts'
import { connectMenuEntries } from './hostMenu'
import type { Host, HostService } from './types'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const makeHost = (services: HostService[]): Host => ({
  id: 'h-win',
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
  services,
  credentials: [{ id: 'c-admin', label: null, username: 'admin', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
})

const actions = { connect: vi.fn(), connectWithPrompt: vi.fn(), connectWithoutLogin: vi.fn(), connectWeb: vi.fn(), openDirect: vi.fn() }

/** What Connect ▸ Tools offers, and for each what's in its submenu. */
function toolsOf(host: Host) {
  const tools = connectMenuEntries(host, actions).find((e) => e.kind === 'item' && e.label === 'Tools')
  return tools?.kind === 'item' ? (tools.submenu ?? []).map((e) => (e.kind === 'item' ? e : null)).filter((e) => e !== null) : []
}

const labels = (host: Host) => toolsOf(host).map((e) => e.label)

const ssh = (tools?: string[] | null, port: number | null = null): HostService => ({ protocol: 'Ssh', port, label: null, tools })

describe('tools in the menu', () => {
  it('offers all of a service’s tools unless told otherwise, as for hosts from before there was a choice', () => {
    expect(labels(makeHost([ssh()]))).toEqual(['Files', 'Storage', 'Journal', 'SELinux'])
    expect(labels(makeHost([ssh(null)]))).toEqual(['Files', 'Storage', 'Journal', 'SELinux'])
    expect(labels(makeHost([{ protocol: 'Rpc', port: null, label: null }]))).toEqual(['Event Log Viewer'])
  })

  it('leaves out the Linux tools on SSH to a Windows host', () => {
    expect(labels(makeHost([ssh(['files'])]))).toEqual(['Files'])
  })

  it('has no Tools at all when every one is off', () => {
    const entries = connectMenuEntries(makeHost([ssh([])]), actions)

    expect(entries.map((e) => (e.kind === 'item' ? e.label : e.kind))).toEqual(['SSH'])
  })

  it('turns off the Event Log Viewer on its RPC service', () => {
    expect(labels(makeHost([{ protocol: 'Rpc', port: null, label: null, tools: [] }]))).toEqual([])
  })

  it('offers a tool only on the services that have it, going straight to the logins when that’s one', () => {
    const host = makeHost([ssh(['files'], 22), ssh(null, 2222)])
    const [files, , journal] = toolsOf(host)

    // Both have Files: pick the service.
    expect(files.submenu?.map((e) => (e.kind === 'item' ? e.detail : null))).toEqual([':22', ':2222'])
    // Only the second has the Journal: straight to its logins, and connecting uses that service.
    expect(journal.label).toBe('Journal')
    const login = journal.submenu?.[0]
    expect(login?.kind === 'item' && login.label).toBe('admin')
    if (login?.kind === 'item') {
      login.onSelect?.()
    }
    expect(actions.connect).toHaveBeenCalledWith(host, 1, host.credentials[0], 'journal')
  })
})

describe('tools in the editor', () => {
  function Editor({ initial, onChange }: { initial: ServiceDraft[]; onChange: (services: ServiceDraft[]) => void }) {
    const [value, setValue] = useState(initial)
    return (
      <ServicesEditor
        value={value}
        errors={{}}
        onChange={(next) => {
          setValue(next)
          onChange(next)
        }}
      />
    )
  }

  function setup(initial: ServiceDraft[]) {
    mockFetch({ ...antiforgeryRoute, 'GET /api/settings/inventory-limits': () => jsonResponse(200, { maxServicesPerHost: 20 }) })
    const onChange = vi.fn<(services: ServiceDraft[]) => void>()
    render(<Editor initial={initial} onChange={onChange} />)
    return { onChange, user: userEvent.setup() }
  }

  it('shows checkboxes, all ticked to begin with, and stores what’s unticked', async () => {
    const { onChange, user } = setup([newServiceDraft('Ssh')])
    const tools = within(screen.getByRole('group', { name: 'Tools for service 1' }))

    expect(tools.getAllByRole('checkbox').map((c) => [c.parentElement?.textContent, (c as HTMLInputElement).checked])).toEqual([
      ['Files', true],
      ['Storage', true],
      ['Journal', true],
      ['SELinux', true],
    ])

    await user.click(tools.getByRole('checkbox', { name: 'Journal' }))
    await user.click(tools.getByRole('checkbox', { name: 'SELinux' }))

    expect(onChange.mock.lastCall?.[0][0].tools).toEqual(['files', 'storage'])
    expect(tools.getByRole('checkbox', { name: 'Journal' })).not.toBeChecked()
  })

  it('goes back to “all” when every one is ticked again, so tools added later start on', async () => {
    const { onChange, user } = setup([newServiceDraft('Ssh', null, null, null, null, null, ['files', 'storage', 'journal'])])

    await user.click(screen.getByRole('checkbox', { name: 'SELinux' }))

    expect(onChange.mock.lastCall?.[0][0].tools).toBeNull()
  })

  it('offers them only for services that have tools, and starts afresh when the type changes', async () => {
    const { onChange, user } = setup([newServiceDraft('Ssh', null, null, null, null, null, ['files'])])

    await user.selectOptions(screen.getByLabelText('Type for service 1'), 'Windows RPC')

    expect(onChange.mock.lastCall?.[0][0].tools).toBeNull()
    expect(within(screen.getByRole('group', { name: 'Tools for service 1' })).getAllByRole('checkbox').map((c) => c.parentElement?.textContent)).toEqual(['Event Log Viewer'])

    await user.selectOptions(screen.getByLabelText('Type for service 1'), 'Remote Desktop (RDP)')
    expect(screen.queryByRole('group', { name: 'Tools for service 1' })).not.toBeInTheDocument()
  })
})

describe('tools through a save', () => {
  it('sends them only when some are off, and keeps them through editing and cloning', () => {
    const host = makeHost([ssh(['files']), ssh(null, 2222), { protocol: 'Rdp', port: null, label: null }])

    const input = toHostInput(toHostDraft(host))

    expect(input.services[0]).toMatchObject({ tools: ['files'] })
    expect(input.services[1]).not.toHaveProperty('tools')
    expect(input.services[2]).not.toHaveProperty('tools')
    expect(toHostInput(toCloneDraft(host)).services[0]).toMatchObject({ tools: ['files'] })
  })

  it('can turn off every tool', () => {
    expect(toHostInput(toHostDraft(makeHost([ssh([])]))).services[0]).toMatchObject({ tools: [] })
  })

  it('saves the checkboxes from the host editor, and the menu follows', async () => {
    const saved: Record<string, unknown>[] = []
    let current = makeHost([ssh()])
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, admin),
      'GET /api/hosts': () => jsonResponse(200, [current]),
      'PUT /api/hosts/h-win': (_url: string, init: RequestInit) => {
        const body = JSON.parse(String(init.body)) as { services: HostService[] }
        saved.push(body)
        current = { ...current, services: body.services, version: 2 }
        return jsonResponse(200, current)
      },
    })
    const router = createMemoryRouter(routes, { initialEntries: ['/'] })
    render(
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>,
    )
    const user = userEvent.setup()

    const row = await screen.findByRole('button', { name: /win01/ })
    fireEvent.contextMenu(row)
    await user.click(screen.getByRole('menuitem', { name: /^Edit/ }))
    const tools = within(await screen.findByRole('group', { name: 'Tools for service 1' }))
    await user.click(tools.getByRole('checkbox', { name: 'Journal' }))
    await user.click(tools.getByRole('checkbox', { name: 'SELinux' }))
    await user.click(screen.getByRole('button', { name: /^Save/ }))

    await waitFor(() => expect(saved).toHaveLength(1))
    expect(saved[0]).toMatchObject({ services: [{ protocol: 'Ssh', tools: ['files', 'storage'] }] })

    fireEvent.contextMenu(await screen.findByRole('button', { name: /win01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
    expect(screen.getByRole('menuitem', { name: /^Files/ })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /^Journal/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /^SELinux/ })).not.toBeInTheDocument()
  })
})
