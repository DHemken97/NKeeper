import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { MenuEntry } from '../../components/ui/ContextMenu'
import { CredentialsEditor } from './components/CredentialsEditor'
import { newServiceDraft, toHostDraft, toHostInput, type CredentialDraft } from './drafts'
import { connectMenuEntries, defaultConnection } from './hostMenu'
import type { Host } from './types'

const sql01: Host = {
  id: 'h-sql01',
  name: 'sql01',
  hostname: null,
  ipAddress: '10.0.0.31',
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
    { protocol: 'Rdp', port: null, label: null, id: 's-rdp' },
    { protocol: 'Ssms', port: null, label: null, instance: null, id: 's-sql' },
  ],
  credentials: [
    { id: 'c-admin', label: null, username: 'administrator', domain: null, notes: null, hasPassword: true, hasPrivateKey: false, serviceIds: ['s-rdp'] },
    { id: 'c-sa', label: null, username: 'sa', domain: null, notes: null, hasPassword: true, hasPrivateKey: false, serviceIds: ['s-sql'] },
    { id: 'c-ops', label: null, username: 'ops', domain: null, notes: null, hasPassword: true, hasPrivateKey: false },
  ],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const actions = { connect: vi.fn(), connectWithPrompt: vi.fn(), connectWithoutLogin: vi.fn(), connectWeb: vi.fn(), openDirect: vi.fn() }

/** The labels of a menu's items, following a path of submenus. */
function labels(entries: MenuEntry[], ...path: string[]): string[] {
  let current = entries
  for (const step of path) {
    const next = current.find((e) => e.kind === 'item' && e.label === step)
    current = next?.kind === 'item' ? (next.submenu ?? []) : []
  }
  return current.flatMap((e) => (e.kind === 'item' ? [e.label] : []))
}

describe('credentials used with some services', () => {
  it('are offered only for those services; others for every service', () => {
    const menu = connectMenuEntries(sql01, actions)

    expect(labels(menu, 'RDP')).toEqual(['administrator', 'ops', 'Enter credentials…'])
    expect(labels(menu, 'Tools', 'MS SQL', 'Default instance')).toEqual(['sa', 'ops', 'Enter credentials…'])
    // Double-click picks the first one meant for the default service.
    expect(defaultConnection(sql01)?.credential?.id).toBe('c-admin')
  })

  it('follow their services when the services are reordered or removed in the editor', () => {
    const draft = toHostDraft(sql01)
    expect(draft.credentials.map((c) => c.serviceKeys?.length ?? 'all')).toEqual([1, 1, 'all'])

    // MS SQL moves first, and a new service is added: sa still means MS SQL, and isn't given the new one.
    draft.services = [draft.services[1], draft.services[0], newServiceDraft('Ssh')]
    const input = toHostInput(draft, 1)
    expect(input.credentials.map((c) => c.serviceIndexes)).toEqual([[1], [0], undefined])

    // Its only service removed: used with none.
    draft.services = [draft.services[1]]
    expect(toHostInput(draft, 1).credentials[1].serviceIndexes).toEqual([])
  })

  it('are chosen with “Use with”: all services by default, or the ones ticked', async () => {
    const user = userEvent.setup()
    const services = [newServiceDraft('Rdp'), newServiceDraft('Ssms', 14330, null, null, null, 'SQLEXPRESS')]
    const changes: CredentialDraft[][] = []
    function Harness() {
      const [credentials, setCredentials] = useState<CredentialDraft[]>(toHostDraft({ ...sql01, credentials: [sql01.credentials[2]] }).credentials)
      return (
        <CredentialsEditor
          value={credentials}
          onChange={(next) => {
            changes.push(next)
            setCredentials(next)
          }}
          errors={{}}
          services={services}
        />
      )
    }
    render(<Harness />)

    const card = screen.getByRole('listitem', { name: 'ops' })
    const all = within(card).getByRole('checkbox', { name: 'All services' })
    expect(all).toBeChecked()
    expect(within(card).queryByRole('checkbox', { name: 'RDP' })).not.toBeInTheDocument()

    // Choosing starts from every service ticked: untick the ones it isn't for.
    await user.click(all)
    expect(within(card).getByRole('checkbox', { name: 'RDP' })).toBeChecked()
    await user.click(within(card).getByRole('checkbox', { name: 'RDP' }))
    expect(within(card).getByRole('checkbox', { name: 'MS SQL · SQLEXPRESS :14330' })).toBeChecked()
    expect(changes.at(-1)![0].serviceKeys).toEqual([services[1].key])

    await user.click(within(card).getByRole('checkbox', { name: 'MS SQL · SQLEXPRESS :14330' }))
    expect(within(card).getByText('Not used with any service: it isn’t offered anywhere.')).toBeInTheDocument()

    await user.click(all)
    expect(changes.at(-1)![0].serviceKeys).toBeNull()
  })
})
