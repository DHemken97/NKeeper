import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { DatabaseSurvey } from '../databases/databaseApi'
import type { Host } from '../inventory/types'
import { escapeHtml, questionnaireHtml } from './insurance/document'
import { copyAllFound, countedHosts, databaseInstances, resolve, summarizeServers, type QuestionnaireAnswers } from './insurance/model'

const user = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false, isSystemAdministrator: true }

function host(id: string, name: string, extra: Partial<Host> = {}): Host {
  return {
    id,
    name,
    hostname: `${name}.corp.lan`,
    ipAddress: null,
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
    services: [],
    credentials: [],
    trustedKeys: [],
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
    version: 1,
    canManage: true,
    ...extra,
  }
}

const hosts: Host[] = [
  host('h1', 'esx01', { tags: [{ name: 'site', value: 'HQ' }, { name: 'os', value: 'Proxmox VE 8' }], cpuCores: 32, serialNumber: 'ABC123' }),
  host('h2', 'sql01', {
    isVirtualMachine: true,
    hypervisorId: 'h1',
    tags: [{ name: 'os', value: 'Windows Server 2022' }, { name: 'role', value: 'database' }],
    services: [{ id: 's1', protocol: 'Ssms', port: 1433, label: null, instance: null }],
    credentials: [{ id: 'c1', label: 'DBA', username: 'nkeeper_ro', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  }),
  host('h3', 'printer', { tags: [{ name: 'role', value: 'printer' }] }),
]

const survey: DatabaseSurvey = {
  product: 'Microsoft SQL Server',
  version: 'SQL Server 2022 Standard Edition (16.0.4135.4)',
  developer: 'Microsoft (licensed)',
  databases: [
    { name: 'master', sizeMb: 6, created: '2003-04-08', owner: 'sa', state: 'ONLINE', location: 'D:\\Data', system: true },
    { name: 'Payroll', sizeMb: 2048, created: '2019-03-01', owner: 'sa', state: 'ONLINE', location: 'D:\\Data', system: false },
  ],
  facts: [
    { topic: 'software', text: 'Microsoft SQL Server 2022 Standard.' },
    { topic: 'writers', text: 'Server administrators (sysadmin): CORP\\DBAs (Windows group).' },
    { topic: 'writers', text: 'db_datawriter: CORP\\Payroll Clerks (Windows group).', database: 'Payroll' },
    { topic: 'backupTypes', text: 'Full: 30 backups on record.', database: 'Payroll' },
    { topic: 'backupSchedule', text: 'SQL Server Agent job “Nightly”: daily, at 01:00.' },
  ],
  sections: [{ title: 'Databases', command: 'sys.databases', results: [], error: null }],
  atUtc: '2026-11-10T09:00:00Z',
}

describe('insurance questionnaire: model', () => {
  it('counts servers by type, OS and tags, leaving out hosts that match', () => {
    const counted = countedHosts(hosts, 'role:printer, env:lab')
    expect(counted.map((h) => h.name)).toEqual(['esx01', 'sql01'])

    const summary = summarizeServers(counted)
    expect(summary).toMatchObject({ total: 2, physical: 1, virtual: 1, hypervisors: 1 })
    expect(summary.byOs).toEqual([
      { label: 'Proxmox VE 8', count: 1 },
      { label: 'Windows Server 2022', count: 1 },
    ])
    expect(summary.byService).toEqual([{ label: 'Database servers: Microsoft SQL Server', count: 1 }])
    // sql01 inherits esx01's site.
    expect(summary.rows.find((r) => r.name === 'sql01')?.location).toBe('virtual machine on esx01')
    expect(summary.rows.find((r) => r.name === 'esx01')).toMatchObject({ kind: 'Physical', cpu: '32', serial: 'ABC123', location: 'site: HQ' })
  })

  it('finds each database service and resolves answers from the most specific', () => {
    const [instance] = databaseInstances(hosts)
    expect(instance).toMatchObject({ key: 'h2:s1', engine: 'mssql', title: 'Microsoft SQL Server on sql01' })
    expect(instance.location).toContain('server sql01 (sql01.corp.lan, port 1433)')

    expect(resolve('managedBy', { managedBy: ' ' }, { managedBy: 'DBA team' }, { managedBy: 'IT' })).toBe('DBA team')
    expect(resolve('managedBy', undefined, {})).toBe('')
  })
})

describe('insurance questionnaire: copy all', () => {
  it('fills every empty answer with what was found, keeping typed ones and skipping left-out servers', () => {
    const { sections: _sections, ...saved } = survey
    const instances = databaseInstances(hosts)
    const { answers, copied } = copyAllFound({ instances: { 'h2:s1': { survey: saved, server: { writers: 'Typed by me' } } } }, instances)
    const server = answers.instances!['h2:s1']
    expect(server.server?.writers).toBe('Typed by me')
    expect(server.server?.software).toBe('Microsoft SQL Server 2022 Standard.')
    expect(server.databases?.Payroll?.writers).toBe('Payroll: db_datawriter: CORP\\Payroll Clerks (Windows group).')
    expect(server.backup?.types).toBe('Payroll: Full: 30 backups on record.')
    expect(server.backup?.schedule).toBe('SQL Server Agent job “Nightly”: daily, at 01:00.')
    // Questions nothing was found for stay blank.
    expect(server.server?.managedBy).toBeUndefined()
    expect(copied).toBe(4)

    expect(copyAllFound(answers, instances).copied).toBe(0)
    expect(copyAllFound({ instances: { 'h2:s1': { survey: saved, excluded: true } } }, instances).copied).toBe(0)
  })
})

describe('insurance questionnaire: document', () => {
  it('writes the answers, what NKeeper found, and the appendix, escaped', () => {
    const { sections, ...saved } = survey
    const answers: QuestionnaireAnswers = {
      organisation: 'Acme <Ltd>',
      databaseDefaults: { managedBy: 'IT department' },
      backupDefaults: { media: 'Veeam Backup & Replication 12 to a Synology NAS' },
      instances: { 'h2:s1': { survey: saved, databases: { Payroll: { contents: 'Employee pay and tax records' } } } },
      repositories: [{ id: 'r1', name: 'Microsoft 365 e-mail', answers: { remote: 'Backed up by Microsoft and by Veeam for M365' } }],
    }
    const instances = databaseInstances(hosts)
    const html = questionnaireHtml({ answers, servers: summarizeServers(hosts), instances, sections: { 'h2:s1': sections }, generatedAt: new Date('2026-11-10T12:00:00Z') })

    expect(html).toContain('Information Systems Questionnaire – Acme &lt;Ltd&gt;')
    expect(html).toContain('IT department')
    expect(html).toContain('Employee pay and tax records')
    expect(html).toContain('db_datawriter: CORP\\Payroll Clerks (Windows group).')
    expect(html).toContain('Veeam Backup &amp; Replication 12 to a Synology NAS')
    expect(html).toContain('Microsoft 365 e-mail')
    expect(html).toContain('<b>Payroll:</b> Full: 30 backups on record.')
    expect(html).toContain('Appendix: what the database servers reported')
    // The server's own databases aren't listed as the business's.
    expect(html).not.toContain('Database master')
  })

  it('doesn’t repeat facts copied into the answer, and can leave out the appendix', () => {
    const { sections, ...saved } = survey
    const fact = 'SQL Server Agent job “Nightly”: daily, at 01:00.'
    const html = questionnaireHtml({
      answers: { includeAppendix: false, instances: { 'h2:s1': { survey: saved, backup: { schedule: fact } } } },
      servers: summarizeServers(hosts),
      instances: databaseInstances(hosts),
      sections: { 'h2:s1': sections },
      generatedAt: new Date(),
    })
    expect(html.split(escapeHtml(fact)).length - 1).toBe(1)
    expect(html).not.toContain('Appendix')
  })
})

describe('insurance questionnaire page', () => {
  it('is only for the system administrator: other administrators are sent to the workspace', async () => {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, { ...user, userName: 'second', isSystemAdministrator: false }),
      'GET /api/hosts': () => jsonResponse(200, []),
    })
    const router = createMemoryRouter(routes, { initialEntries: ['/reports/insurance'] })
    render(
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>,
    )
    await vi.waitFor(() => expect(router.state.location.pathname).toBe('/'))
    expect(screen.queryByText('Insurance questionnaire')).not.toBeInTheDocument()
  })

  it('reads a database server with its saved login, shows what it found, and saves the answers', async () => {
    let savedBody: unknown = null
    const fetchMock = mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, user),
      'GET /api/hosts': () => jsonResponse(200, hosts),
      'GET /api/reports/answers/insurance': () => jsonResponse(200, { answers: { organisation: 'Acme' }, updatedAtUtc: null, updatedBy: null }),
      'POST /api/mssql/sessions': () =>
        jsonResponse(200, { status: 'ready', sessionId: 'sess1', target: 'nkeeper_ro@sql01:1433', server: null, database: 'master', certificate: null }),
      'GET /api/mssql/sessions/sess1/survey': () => jsonResponse(200, survey),
      'DELETE /api/mssql/sessions/sess1': () => new Response(null, { status: 204 }),
      'PUT /api/reports/answers/insurance': (_url, init) => {
        savedBody = JSON.parse(init.body as string)
        return jsonResponse(200, { answers: {}, updatedAtUtc: '2026-11-10T10:00:00Z', updatedBy: 'bob' })
      },
    })
    const router = createMemoryRouter(routes, { initialEntries: ['/reports/insurance'] })
    render(
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>,
    )
    const person = userEvent.setup()

    expect(await screen.findByRole('textbox', { name: 'Organisation' })).toHaveValue('Acme')
    expect(screen.getByText('3 servers: 2 physical, 1 virtual')).toBeInTheDocument()

    await person.type(screen.getByRole('textbox', { name: 'Don’t count hosts matching' }), 'role:printer')
    expect(screen.getByText('2 servers: 1 physical, 1 virtual')).toBeInTheDocument()

    await person.click(screen.getByRole('button', { name: 'Read every database server' }))
    expect(await screen.findAllByText(/Server administrators \(sysadmin\): CORP\\DBAs/)).not.toHaveLength(0)
    const opened = fetchMock.mock.calls.find(([url, init]) => String(url).endsWith('/api/mssql/sessions') && init?.method === 'POST')
    expect(JSON.parse(opened![1]!.body as string)).toMatchObject({ hostId: 'h2', serviceIndex: 0, credentialId: 'c1' })
    expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/sessions/sess1') && init?.method === 'DELETE')).toBe(true)

    const card = screen.getAllByText('Microsoft SQL Server on sql01')[0].closest('details')!
    expect(within(card).getByText(/Microsoft SQL Server SQL Server 2022 Standard Edition/)).toBeInTheDocument()

    await person.click(screen.getByRole('button', { name: 'Save answers' }))
    expect(await screen.findByText(/Saved .* by bob/)).toBeInTheDocument()
    const body = savedBody as { answers: QuestionnaireAnswers }
    expect(body.answers.excludeHosts).toBe('role:printer')
    expect(body.answers.instances?.['h2:s1']?.survey?.databases.map((d) => d.name)).toEqual(['master', 'Payroll'])
    // The sections stay in the page (for the appendix); they aren't saved.
    expect(body.answers.instances?.['h2:s1']?.survey).not.toHaveProperty('sections')
  })
})
