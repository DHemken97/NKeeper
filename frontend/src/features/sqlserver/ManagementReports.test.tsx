import { act, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import type { ToolReport } from './sqlServerApi'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const db01: Host = {
  id: 'h-db01',
  name: 'db01',
  hostname: null,
  ipAddress: '10.0.0.40',
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
    { protocol: 'Oracle', port: null, label: null, instance: 'ORCLPDB1' },
    { protocol: 'Ase', port: null, label: null },
  ],
  credentials: [{ id: 'c-dba', label: null, username: 'dba', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const report = (title: string): ToolReport => ({
  warnings: [`${title} warning`],
  suspended: [],
  sections: [
    {
      title,
      command: 'v$something',
      results: [{ kind: 'results', columns: [{ name: 'Name', type: 'VARCHAR2', numeric: false }], rows: [['USERS']], rowCount: 1 }],
      error: null,
      scripts: [
        { label: 'USERS: add a datafile', text: 'ALTER TABLESPACE "USERS" ADD DATAFILE SIZE 1G;', kind: 'sql' },
        { label: 'Full backup', text: 'BACKUP DATABASE PLUS ARCHIVELOG;', kind: 'copy' },
      ],
    },
  ],
  atUtc: '2026-11-04T12:00:00Z',
})

function renderWorkspace(engine: 'oracle' | 'ase', tools: { label: string; name: string }[]) {
  const keptAlive: string[] = []
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [db01]),
    [`POST /api/${engine}/sessions`]: () =>
      jsonResponse(200, {
        status: 'ready',
        sessionId: 'main',
        target: 'dba@10.0.0.40',
        server: { serverName: 'db01', productVersion: '19.0', productLevel: null, edition: null, login: 'dba', sessionId: 7 },
        database: 'DBA',
        certificate: null,
      }),
    [`GET /api/${engine}/sessions/main/databases`]: () => jsonResponse(200, ['DBA']),
    [`POST /api/${engine}/sessions/main/explore`]: (_url, init) => {
      const node = JSON.parse(String(init.body)).node
      return jsonResponse(
        200,
        node.kind === 'server'
          ? [{ kind: 'management', label: 'Management', expandable: true }]
          : node.kind === 'management'
            ? tools.map((t) => ({ kind: 'tool', label: t.label, expandable: false, name: t.name }))
            : [],
      )
    },
    [`GET /api/${engine}/sessions/main/reports/storage`]: () => jsonResponse(200, report('Tablespaces')),
    [`GET /api/${engine}/sessions/main/reports/backups`]: () => jsonResponse(200, report('RMAN jobs')),
    [`GET /api/${engine}/sessions/main/reports/replication`]: () => jsonResponse(200, report('Transaction logs')),
    [`GET /api/${engine}/sessions/main/reports/license`]: () => jsonResponse(200, report('Licenses')),
    [`POST /api/${engine}/sessions/main/keepalive`]: () => (keptAlive.push(engine), new Response(null, { status: 204 })),
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
    </AuthProvider>,
  )
  return { user: userEvent.setup(vi.isFakeTimers() ? { advanceTimers: vi.advanceTimersByTime } : {}), keptAlive }
}

async function connect(user: ReturnType<typeof userEvent.setup>, path: RegExp[]) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /db01/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
  for (const name of path) {
    await user.hover(screen.getByRole('menuitem', { name }))
  }
  await user.click(screen.getByRole('menuitem', { name: /^dba/ }))
}

describe('Management reports', () => {
  it('keeps an Oracle tab open: its keep-alive goes to the Oracle API', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      const { user, keptAlive } = renderWorkspace('oracle', [])
      await connect(user, [/^Oracle/, /^ORCLPDB1/])
      await screen.findByRole('tree', { name: 'Objects' })

      await act(async () => {
        await vi.advanceTimersByTimeAsync(61_000)
      })
      expect(keptAlive).toContain('oracle')
      expect(screen.queryByText(/session has ended/)).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })

  it('opens Oracle Storage and Backups as tabs; SQL scripts open a query window, RMAN ones a dialog to copy', async () => {
    const { user } = renderWorkspace('oracle', [
      { label: 'Storage', name: 'storage' },
      { label: 'Backups (RMAN)', name: 'backups' },
    ])
    await connect(user, [/^Oracle/, /^ORCLPDB1/])

    const tree = await screen.findByRole('tree', { name: 'Objects' })
    await user.dblClick(await within(tree).findByText('Management'))
    await user.dblClick(await within(tree).findByText('Storage'))
    expect(await screen.findByRole('tab', { name: 'Storage' })).toHaveAttribute('aria-selected', 'true')
    const storage = screen.getByRole('tabpanel', { name: 'Storage' })
    expect(await within(storage).findByText('Tablespaces warning')).toBeInTheDocument()

    await user.click(within(storage).getByRole('button', { name: 'USERS: add a datafile' }))
    expect(await screen.findByRole('tab', { name: 'SQLQuery2' })).toHaveAttribute('aria-selected', 'true')

    fireEvent.contextMenu(within(tree).getByText('Backups (RMAN)'))
    await user.click(screen.getByRole('menuitem', { name: 'Open' }))
    const backups = await screen.findByRole('tabpanel', { name: 'Backups (RMAN)' })
    await user.click(await within(backups).findByRole('button', { name: 'Full backup…' }))
    expect(within(await screen.findByRole('dialog', { name: 'Full backup' })).getByText('BACKUP DATABASE PLUS ARCHIVELOG;')).toBeInTheDocument()
  })

  it('opens License on an ASE tab', async () => {
    const { user } = renderWorkspace('ase', [{ label: 'License', name: 'license' }])
    await connect(user, [/^SAP ASE/])

    const tree = await screen.findByRole('tree', { name: 'Objects' })
    await user.dblClick(await within(tree).findByText('Management'))
    await user.dblClick(await within(tree).findByText('License'))
    const panel = await screen.findByRole('tabpanel', { name: 'License' })
    expect(await within(panel).findByText('Licenses warning')).toBeInTheDocument()
  })

  it('opens Replication Health on an ASE tab', async () => {
    const { user } = renderWorkspace('ase', [
      { label: 'Blocking Manager', name: 'blocking' },
      { label: 'Replication Health', name: 'replication' },
    ])
    await connect(user, [/^SAP ASE/])

    const tree = await screen.findByRole('tree', { name: 'Objects' })
    await user.dblClick(await within(tree).findByText('Management'))
    await user.dblClick(await within(tree).findByText('Replication Health'))
    const panel = await screen.findByRole('tabpanel', { name: 'Replication Health' })
    expect(await within(panel).findByText('Transaction logs warning')).toBeInTheDocument()
  })
})
