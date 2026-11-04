import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import type { AseCopyPlan, ToolReport } from './databaseApi'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const ase01: Host = {
  id: 'h-ase01',
  name: 'ase01',
  hostname: null,
  ipAddress: '10.0.0.50',
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
  services: [{ protocol: 'Ase', port: null, label: null }],
  credentials: [{ id: 'c-sa', label: null, username: 'sa', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const report = (title: string, warning: string): ToolReport => ({
  warnings: [warning],
  suspended: [],
  sections: [{ title, command: 'sysusages', results: [{ kind: 'results', columns: [{ name: 'Database', type: 'varchar', numeric: false }], rows: [['sales']], rowCount: 1 }], error: null }],
  atUtc: '2026-11-04T12:00:00Z',
})

const plan: AseCopyPlan = {
  source: { server: 'ASE01', version: '16.0 SP04', pageSize: 4096, charsetId: 1, sortOrderId: 50 },
  target: { server: 'ASE02', version: '16.0 SP04', pageSize: 4096, charsetId: 1, sortOrderId: 50 },
  dataMb: 500,
  logMb: 100,
  targetExists: false,
  blockers: [],
  warnings: ['Users in the copy map to logins by suid.'],
  createSql: "create database [sales] on data1 = '500M' log on log1 = '100M' for load",
  backupServers: ['ASE02_BS'],
  replicated: true,
}

function setup() {
  const steps: Record<string, unknown>[] = []
  const compared: Record<string, unknown>[] = []
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [ase01]),
    'POST /api/ase/sessions': () =>
      jsonResponse(200, {
        status: 'ready',
        sessionId: 'main',
        target: 'sa@10.0.0.50:5000',
        server: { serverName: 'ASE01', productVersion: '16.0 SP04', productLevel: null, edition: null, login: 'sa', sessionId: 12 },
        database: 'master',
        certificate: null,
      }),
    'GET /api/ase/sessions': () => jsonResponse(200, [{ id: 'other', display: 'sa@10.0.0.51:5000', hostId: 'h-ase02', serverName: 'ASE02', version: '16.0 SP04' }]),
    'GET /api/ase/sessions/main/databases': () => jsonResponse(200, ['master', 'sales']),
    'POST /api/ase/sessions/main/explore': (_url, init) =>
      jsonResponse(
        200,
        JSON.parse(String(init.body)).node.kind === 'server'
          ? [{ kind: 'management', label: 'Management', expandable: true }]
          : [
              { kind: 'tool', label: 'Space', expandable: false, name: 'space' },
              { kind: 'tool', label: 'Replication Drift', expandable: false, name: 'drift' },
              { kind: 'tool', label: 'Copy Database', expandable: false, name: 'copy' },
            ],
      ),
    'GET /api/ase/sessions/main/reports/space': () => jsonResponse(200, report('Databases', 'sales: data is 91% full (45.0 MB free).')),
    'GET /api/ase/sessions/main/reports/drift': () => jsonResponse(200, report('Last transaction applied', 'sales: last applied 3 h ago.')),
    'POST /api/ase/sessions/main/drift/compare': (_url, init) => {
      compared.push(JSON.parse(String(init.body)))
      return jsonResponse(200, report('Row counts (estimated)', '1 of 3 tables differ (estimated counts: compare exactly before acting).'))
    },
    'POST /api/ase/sessions/main/copy/check': () => jsonResponse(200, plan),
    'POST /api/ase/sessions/main/copy/run': (_url, init) => {
      const body = JSON.parse(String(init.body))
      steps.push(body)
      return jsonResponse(200, { title: body.step, command: body.step, results: [{ kind: 'message', text: `${body.step} finished` }], error: null })
    },
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
    </AuthProvider>,
  )
  return { user: userEvent.setup(), steps, compared }
}

async function openTool(user: ReturnType<typeof userEvent.setup>, label: string) {
  const tree = await screen.findByRole('tree', { name: 'Objects' })
  if (!within(tree).queryByText(label)) {
    await user.dblClick(await within(tree).findByText('Management'))
  }
  await user.dblClick(await within(tree).findByText(label))
}

describe('SAP ASE Management tools', () => {
  it('shows Space, compares row counts from Replication Drift, and copies a database step by step', async () => {
    const { user, steps, compared } = setup()
    fireEvent.contextMenu(await screen.findByRole('button', { name: /ase01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
    await user.hover(screen.getByRole('menuitem', { name: /^SAP ASE/ }))
    await user.click(screen.getByRole('menuitem', { name: /^sa/ }))

    await openTool(user, 'Space')
    const space = await screen.findByRole('tabpanel', { name: 'Space' })
    expect(await within(space).findByText('sales: data is 91% full (45.0 MB free).')).toBeInTheDocument()

    await openTool(user, 'Replication Drift')
    const drift = await screen.findByRole('tabpanel', { name: 'Replication Drift' })
    const panel = within(drift).getByRole('region', { name: 'Compare with another server' })
    await waitFor(() => expect(within(panel).getByRole('option', { name: 'sales' })).toBeInTheDocument())
    await user.selectOptions(within(panel).getByRole('combobox', { name: 'Database here' }), 'sales')
    await waitFor(() => expect(within(panel).getByRole('combobox', { name: 'Other server' })).toHaveValue('other'))
    await user.click(within(panel).getByRole('button', { name: 'Compare' }))
    expect(await within(panel).findByText(/1 of 3 tables differ/)).toBeInTheDocument()
    expect(compared[0]).toEqual({ otherSessionId: 'other', database: 'sales', exact: false })

    await openTool(user, 'Copy Database')
    const copy = await screen.findByRole('tabpanel', { name: 'Copy Database' })
    await waitFor(() => expect(within(copy).getByRole('option', { name: 'sales' })).toBeInTheDocument())
    await user.selectOptions(within(copy).getByRole('combobox', { name: 'Database to copy' }), 'sales')
    await waitFor(() => expect(within(copy).getByRole('combobox', { name: 'Target server' })).toHaveValue('other'))
    await user.click(within(copy).getByRole('button', { name: 'Check' }))
    expect(await within(copy).findByRole('textbox', { name: 'Create statement' })).toHaveValue(plan.createSql)
    await user.selectOptions(within(copy).getByRole('combobox', { name: 'Dump through' }), 'ASE02_BS')

    await user.click(within(copy).getByRole('button', { name: 'Run all steps' }))
    const dialog = await screen.findByRole('dialog', { name: 'Copy sales to sales on ASE02?' })
    await user.click(within(dialog).getByRole('button', { name: 'Run' }))

    await waitFor(() => expect(steps.map((s) => s.step)).toEqual(['create', 'dump', 'load', 'online', 'settrunc']))
    expect(steps[0]).toMatchObject({ targetSessionId: 'other', database: 'sales', targetDatabase: 'sales', createSql: plan.createSql })
    expect(steps[1]).toMatchObject({ step: 'dump', dumpPath: '/tmp/sales.dmp', loadPath: '/tmp/sales.dmp', remoteBackupServer: 'ASE02_BS', compress: true })
    expect(await within(copy).findByText('settrunc finished')).toBeInTheDocument()
  })
})
