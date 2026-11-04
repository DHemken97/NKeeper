import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'
import type { BlockingReport, OrphanReport, SqlQueryResult } from './databaseApi'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const sql01: Host = {
  id: 'h-sql01',
  name: 'sql01',
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
  services: [
    { protocol: 'Rdp', port: null, label: null },
    { protocol: 'Ssms', port: 14330, label: null, instance: 'SQLEXPRESS' },
    { protocol: 'Ssms', port: null, label: null, instance: null },
  ],
  credentials: [
    { id: 'c-sa', label: null, username: 'sa', domain: null, notes: null, hasPassword: true, hasPrivateKey: false },
    { id: 'c-dba', label: null, username: 'dba', domain: 'CORP', notes: null, hasPassword: true, hasPrivateKey: false },
  ],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const server = { serverName: 'SQL01\\SQLEXPRESS', productVersion: '16.0.4305.4', productLevel: 'RTM', edition: 'Developer Edition (64-bit)', login: 'sa', sessionId: 51 }
const target = 'sa@10.0.0.30\\SQLEXPRESS:14330'
const opened = (sessionId: string, spid = 51, database = 'master') =>
  jsonResponse(200, { status: 'ready', sessionId, target, server: { ...server, sessionId: spid }, database, certificate: null })

/** Opens sessions in turn: the tab's own first, then one per query window. */
function sessionRoutes(trustFirst = false) {
  const bodies: Record<string, unknown>[] = []
  const closed: string[] = []
  const close = (id: string) => (_url: string, init: RequestInit) => {
    // Sent as the page closes: it must outlive the page (keepalive).
    closed.push(init.keepalive ? `${id} (page closed)` : id)
    return new Response(null, { status: 204 })
  }
  let next = 0
  return {
    bodies,
    closed,
    routes: {
      'POST /api/mssql/sessions': (_url: string, init: RequestInit) => {
        const body = JSON.parse(String(init.body))
        bodies.push(body)
        if (trustFirst && bodies.length === 1) {
          return jsonResponse(200, {
            status: 'trustRequired',
            sessionId: null,
            target,
            server: null,
            database: null,
            certificate: { fingerprint: 'AA:BB:CC', subject: 'CN=SSL_Self_Signed_Fallback', expires: '2046-01-01T00:00:00Z' },
          })
        }
        next++
        return next === 1 ? opened('main') : opened(`win${next - 1}`, 51 + next)
      },
      'GET /api/mssql/sessions/main/databases': () => jsonResponse(200, ['master', 'tempdb', 'Sales']),
      'DELETE /api/mssql/sessions/main': close('main'),
      'DELETE /api/mssql/sessions/win1': close('win1'),
      'DELETE /api/mssql/sessions/win2': close('win2'),
    },
  }
}

function renderWorkspace(routesOverride: Parameters<typeof mockFetch>[0]) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [sql01]),
    'POST /api/mssql/sessions/main/explore': (_url, init) => {
      const node = JSON.parse(String(init.body)).node
      return jsonResponse(
        200,
        node.kind === 'server'
          ? [
              { kind: 'databases', label: 'Databases', expandable: true },
              { kind: 'serverSecurity', label: 'Security', expandable: true },
              { kind: 'management', label: 'Management', expandable: true },
            ]
          : node.kind === 'databases'
            ? [
                { kind: 'systemDatabases', label: 'System Databases', expandable: true },
                { kind: 'database', label: 'Sales', expandable: true, database: 'Sales', name: 'Sales' },
              ]
            : node.kind === 'database'
              ? [{ kind: 'tables', label: 'Tables', expandable: true, database: 'Sales' }]
              : node.kind === 'tables'
                ? [{ kind: 'table', label: 'dbo.Orders', expandable: true, database: 'Sales', schema: 'dbo', name: 'Orders', objectId: 901 }]
                : node.kind === 'management'
                  ? [
                      { kind: 'tool', label: 'Blocking Manager', detail: 'Blocking chains', expandable: false, name: 'blocking' },
                      { kind: 'tool', label: 'Orphaned Users', detail: 'Fix broken logins', expandable: false, name: 'orphans' },
                    ]
                  : [],
      )
    },
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

/** Connect ▸ Tools ▸ MS SQL ▸ SQLEXPRESS ▸ sa. */
async function openSql(user: ReturnType<typeof userEvent.setup>) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /sql01/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
  await user.hover(screen.getByRole('menuitem', { name: /^MS SQL/ }))
  await user.hover(screen.getByRole('menuitem', { name: /^SQLEXPRESS/ }))
  await user.click(screen.getByRole('menuitem', { name: /^sa/ }))
}

function queryResult(overrides: Partial<SqlQueryResult> = {}): SqlQueryResult {
  return {
    output: [
      { kind: 'message', text: 'starting' },
      {
        kind: 'results',
        columns: [
          { name: 'Id', type: 'int', numeric: true },
          { name: 'Name', type: 'nvarchar', numeric: false },
        ],
        rows: [
          ['1', 'alpha'],
          ['2', null],
        ],
        rowCount: 2,
      },
      { kind: 'rows', text: '(2 rows affected)', rowCount: 2 },
      { kind: 'error', text: "Invalid object name 'dbo.Missing'.", number: 208, severity: 16, state: 1, line: 3 },
    ],
    database: 'Sales',
    elapsedMs: 42,
    cancelled: false,
    connectionLost: false,
    hasErrors: true,
    ...overrides,
  }
}

describe('SQL Server', () => {
  it('is under Connect ▸ Tools ▸ MS SQL ▸ instance ▸ login, beside the clients', async () => {
    const { user } = renderWorkspace({})

    fireEvent.contextMenu(await screen.findByRole('button', { name: /sql01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    // Clients stay under Connect; MS SQL is a tool.
    expect(screen.getByRole('menuitem', { name: /^RDP/ })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /^MS SQL/ })).not.toBeInTheDocument()
    await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
    await user.hover(screen.getByRole('menuitem', { name: /^MS SQL/ }))
    expect(screen.getByRole('menuitem', { name: /^SQLEXPRESS.*:14330/ })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: /^Default instance.*:1433/ })).toBeInTheDocument()

    await user.hover(screen.getByRole('menuitem', { name: /^SQLEXPRESS/ }))
    // SQL logins only: a domain credential can't be used.
    expect(screen.getByRole('menuitem', { name: /^CORP\\dba.*Windows login/ })).toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByRole('menuitem', { name: /^sa/ })).not.toHaveAttribute('aria-disabled', 'true')
  })

  it('asks to trust the server’s certificate, then opens the explorer and a query window of its own', async () => {
    const sessions = sessionRoutes(true)
    const { user } = renderWorkspace(sessions.routes)

    await openSql(user)

    const tab = await screen.findByRole('tabpanel', { name: 'sql01 SQL Server' })
    expect(await within(tab).findByText('Trust this certificate?')).toBeInTheDocument()
    expect(within(tab).getByText('AA:BB:CC')).toBeInTheDocument()
    expect(within(tab).getByText(/SSL_Self_Signed_Fallback/, { selector: 'p' })).toBeInTheDocument()
    await user.click(within(tab).getByRole('button', { name: 'Trust and connect' }))

    expect(await within(tab).findByRole('tree', { name: 'Objects' })).toBeInTheDocument()
    expect(await within(tab).findByText('Databases')).toBeInTheDocument()
    expect(within(tab).getByText(/SQL Server 16\.0\.4305\.4 · Developer Edition/)).toBeInTheDocument()
    expect(await within(tab).findByText('sa (53)')).toBeInTheDocument()

    expect(sessions.bodies[0]).toMatchObject({ hostId: 'h-sql01', serviceIndex: 1, credentialId: 'c-sa' })
    expect(sessions.bodies[1]).toMatchObject({ serviceIndex: 1, credentialId: 'c-sa', trustFingerprint: 'AA:BB:CC' })
    // The query window has a session of its own, with the same login.
    expect(sessions.bodies[2]).toMatchObject({ serviceIndex: 1, credentialId: 'c-sa' })
  })

  it('resizes the explorer and the editor against the results, and remembers it; the blocking manager is only under Management', async () => {
    window.localStorage.clear()
    const sessions = sessionRoutes()
    const { user } = renderWorkspace(sessions.routes)
    await openSql(user)

    const tab = await screen.findByRole('tabpanel', { name: 'sql01 SQL Server' })
    await within(tab).findByRole('tree', { name: 'Objects' })
    expect(within(tab).getByRole('button', { name: 'New Query' })).toBeInTheDocument()
    expect(within(tab).queryByRole('button', { name: 'Blocking Manager' })).not.toBeInTheDocument()

    const explorer = within(tab).getByRole('complementary', { name: 'Object Explorer' })
    const explorerBar = within(tab).getByRole('separator', { name: 'Resize Object Explorer' })
    expect(explorer).toHaveStyle({ width: '280px' })
    fireEvent.keyDown(explorerBar, { key: 'ArrowRight' })
    fireEvent.keyDown(explorerBar, { key: 'ArrowRight', shiftKey: true })
    expect(explorer).toHaveStyle({ width: '376px' })
    expect(explorerBar).toHaveAttribute('aria-valuenow', '376')
    fireEvent.doubleClick(explorerBar)
    expect(explorer).toHaveStyle({ width: '280px' })
    fireEvent.keyDown(explorerBar, { key: 'ArrowLeft', shiftKey: true })
    fireEvent.keyDown(explorerBar, { key: 'ArrowLeft', shiftKey: true })
    // Never narrower than its minimum.
    expect(explorer).toHaveStyle({ width: '160px' })

    const editorBar = await within(tab).findByRole('separator', { name: 'Resize editor and results' })
    expect(editorBar).toHaveAttribute('aria-valuenow', '45')
    fireEvent.keyDown(editorBar, { key: 'ArrowDown', shiftKey: true })
    expect(editorBar).toHaveAttribute('aria-valuenow', '60')
    expect(window.localStorage.getItem('nkeeper.sql.editorShare')).toBe('0.6')
    expect(window.localStorage.getItem('nkeeper.sql.explorerWidth')).toBe('160')
  })

  it('closes every connection it opened when its query window or the whole tab is closed', async () => {
    const sessions = sessionRoutes()
    const { user } = renderWorkspace(sessions.routes)
    await openSql(user)

    const tab = await screen.findByRole('tabpanel', { name: 'sql01 SQL Server' })
    await within(tab).findByText('sa (53)')
    await user.click(within(tab).getByRole('button', { name: 'New Query' }))
    await within(tab).findByText('sa (54)')
    expect(sessions.closed).toEqual([])

    // A query window's own connection closes with it.
    await user.click(within(tab).getByRole('button', { name: 'Close SQLQuery2' }))
    await waitFor(() => expect(sessions.closed).toEqual(['win2']))

    // Closing the tab closes the rest: the explorer's and the remaining window's.
    await user.click(screen.getByRole('button', { name: 'Close sql01' }))
    await waitFor(() => expect([...sessions.closed].sort()).toEqual(['main', 'win1', 'win2']))
  })

  it('closes every connection when the browser tab or window closes, not only the NKeeper tab', async () => {
    const sessions = sessionRoutes()
    const { user } = renderWorkspace(sessions.routes)
    await openSql(user)
    const tab = await screen.findByRole('tabpanel', { name: 'sql01 SQL Server' })
    await within(tab).findByText('sa (53)')

    window.dispatchEvent(new Event('pagehide'))

    await waitFor(() => expect([...sessions.closed].sort()).toEqual(['main (page closed)', 'win1 (page closed)']))
  })

  it('runs the query with F5, shows grids, messages with the error’s line, and text', async () => {
    const sessions = sessionRoutes()
    const queries: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      ...sessions.routes,
      'POST /api/mssql/sessions/win1/query': (_url, init) => {
        queries.push(JSON.parse(String(init.body)))
        return jsonResponse(200, queryResult())
      },
    })
    await openSql(user)

    const editor = await screen.findByRole('textbox', { name: 'SQLQuery1 query' })
    await waitFor(() => expect(screen.getByRole('button', { name: /Execute/ })).toBeEnabled())
    fireEvent.change(editor, { target: { value: 'PRINT \'starting\';\nSELECT Id, Name FROM dbo.Orders;\nSELECT * FROM dbo.Missing;' } })
    fireEvent.keyDown(editor, { key: 'F5' })

    const grid = await screen.findByRole('region', { name: 'Result 1' })
    expect(within(grid).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Row', 'Id', 'Name'])
    expect(within(grid).getByText('alpha')).toBeInTheDocument()
    expect(within(grid).getByText('NULL')).toBeInTheDocument()
    expect(queries[0]).toEqual({ sql: "PRINT 'starting';\nSELECT Id, Name FROM dbo.Orders;\nSELECT * FROM dbo.Missing;", lineOffset: 0 })
    expect(screen.getByRole('status', { name: 'SQLQuery1 status' })).toHaveTextContent('Query completed with errors.')
    // USE (or the server) changed the database: the list follows.
    expect(screen.getByRole('combobox', { name: 'Database' })).toHaveValue('Sales')

    await user.click(screen.getByRole('tab', { name: 'Messages' }))
    const messages = screen.getByRole('log', { name: 'Messages' })
    expect(within(messages).getByText('starting')).toBeInTheDocument()
    expect(within(messages).getByText('(2 rows affected)')).toBeInTheDocument()
    expect(within(messages).getByText('Msg 208, Level 16, State 1, Line 3')).toBeInTheDocument()

    // Double-clicking the error selects its line in the editor.
    await user.dblClick(within(messages).getByText("Invalid object name 'dbo.Missing'."))
    const area = editor as HTMLTextAreaElement
    expect(area.value.slice(area.selectionStart, area.selectionEnd)).toBe('SELECT * FROM dbo.Missing;')

    await user.click(screen.getByRole('button', { name: 'Text' }))
    expect(screen.getByLabelText('Results as text').textContent).toContain('Id Name\n-- -----\n 1 alpha\n 2 NULL')

    // A selection runs on its own, with its line in the editor.
    area.setSelectionRange(18, 50)
    fireEvent.keyDown(editor, { key: 'Enter', ctrlKey: true })
    await waitFor(() => expect(queries).toHaveLength(2))
    expect(queries[1]).toEqual({ sql: 'SELECT Id, Name FROM dbo.Orders;', lineOffset: 1 })
  })

  it('scripts Select Top 1000 Rows from the explorer into a new window, which runs it', async () => {
    const sessions = sessionRoutes()
    const scripted: Record<string, unknown>[] = []
    const ran: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      ...sessions.routes,
      'POST /api/mssql/sessions/main/script': (_url, init) => {
        scripted.push(JSON.parse(String(init.body)))
        return jsonResponse(200, { sql: 'SELECT TOP (1000) [Id]\n  FROM [Sales].[dbo].[Orders]' })
      },
      'POST /api/mssql/sessions/win2/query': (_url, init) => {
        ran.push(JSON.parse(String(init.body)))
        return jsonResponse(200, queryResult({ hasErrors: false }))
      },
    })
    await openSql(user)

    const tree = await screen.findByRole('tree', { name: 'Objects' })
    await user.dblClick(await within(tree).findByText('Databases'))
    await user.dblClick(await within(tree).findByText('Sales'))
    await user.dblClick(await within(tree).findByText('Tables'))
    fireEvent.contextMenu(await within(tree).findByText('dbo.Orders'))
    await user.click(screen.getByRole('menuitem', { name: 'Select Top 1000 Rows' }))

    expect(await screen.findByRole('tab', { name: 'Top 1000 · Orders' })).toHaveAttribute('aria-selected', 'true')
    await waitFor(() => expect(ran).toHaveLength(1))
    expect(scripted[0]).toMatchObject({ action: 'selectTop', node: { kind: 'table', database: 'Sales', schema: 'dbo', name: 'Orders', objectId: 901 } })
    expect(ran[0]).toEqual({ sql: 'SELECT TOP (1000) [Id]\n  FROM [Sales].[dbo].[Orders]', lineOffset: 0 })
    // The window starts in the table's database.
    expect(sessions.bodies[2]).toMatchObject({ database: 'Sales' })
  })

  it('opens each Management tool as its own tab, by double-click or right-click ▸ Open', async () => {
    const sessions = sessionRoutes()
    const { user } = renderWorkspace({
      ...sessions.routes,
      'GET /api/mssql/sessions/main/blocking': () => jsonResponse(200, { leads: [], blockedCount: 0, canSeeAllSessions: true, atUtc: '2026-11-03T12:00:00Z' }),
      'GET /api/mssql/sessions/main/orphans': () => jsonResponse(200, { users: [], owners: [], logins: [], skipped: [], databasesChecked: 3 }),
    })
    await openSql(user)

    const tree = await screen.findByRole('tree', { name: 'Objects' })
    await user.dblClick(await within(tree).findByText('Management'))
    await user.dblClick(await within(tree).findByText('Blocking Manager'))
    expect(await screen.findByRole('tab', { name: 'Blocking Manager' })).toHaveAttribute('aria-selected', 'true')

    fireEvent.contextMenu(within(tree).getByText('Orphaned Users'))
    await user.click(screen.getByRole('menuitem', { name: 'Open' }))
    expect(await screen.findByRole('tab', { name: 'Orphaned Users' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tabpanel', { name: 'Orphaned Users' })).toBeVisible()
    const blockingPanel = screen.getAllByRole('tabpanel', { hidden: true }).find((panel) => panel.getAttribute('aria-label') === 'Blocking Manager')
    expect(blockingPanel).not.toBeVisible()

    // Opening one again goes back to its tab rather than opening another.
    await user.dblClick(within(tree).getByText('Blocking Manager'))
    expect(screen.getByRole('tab', { name: 'Blocking Manager' })).toHaveAttribute('aria-selected', 'true')
    await user.dblClick(within(tree).getByText('Orphaned Users'))
    expect(screen.getByRole('tab', { name: 'Orphaned Users' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getAllByRole('tab', { name: /Blocking Manager|Orphaned Users/ })).toHaveLength(2)
  })

  it('finds orphaned users under Management, maps them to their logins, and creates a missing login', async () => {
    const sessions = sessionRoutes()
    const fixes: Record<string, unknown>[] = []
    const report: OrphanReport = {
      users: [
        { database: 'Sales', user: 'app', type: 'SQL user', sid: '0xAB12', defaultSchema: 'dbo', matchingLogin: 'app', ownedSchemas: [] },
        { database: 'Sales', user: 'report', type: 'SQL user', sid: '0xCD34', defaultSchema: 'dbo', matchingLogin: null, ownedSchemas: ['reporting'] },
      ],
      owners: [{ database: 'Archive', ownerSid: '0x0105' }],
      logins: [
        { name: 'app', windows: false, disabled: false },
        { name: 'sa', windows: false, disabled: false },
        { name: 'CORP\\dba', windows: true, disabled: false },
      ],
      skipped: ['Old: offline'],
      databasesChecked: 5,
    }
    let current = report
    const { user } = renderWorkspace({
      ...sessions.routes,
      'GET /api/mssql/sessions/main/orphans': () => jsonResponse(200, current),
      'POST /api/mssql/sessions/main/orphans/fix': (_url, init) => {
        const fix = JSON.parse(String(init.body))
        fixes.push(fix)
        current = { ...current, users: current.users.filter((u) => u.user !== fix.user) }
        return jsonResponse(200, { report: current, message: `Fixed ${fix.user}.` })
      },
    })
    await openSql(user)

    const tree = await screen.findByRole('tree', { name: 'Objects' })
    await user.dblClick(await within(tree).findByText('Management'))
    await user.dblClick(await within(tree).findByText('Orphaned Users'))

    const tool = await screen.findByRole('tabpanel', { name: 'Orphaned Users' })
    const table = await within(tool).findByRole('table', { name: 'Orphaned users' })
    expect(within(table).getAllByRole('row')).toHaveLength(3)
    expect(within(tool).getByRole('button', { name: 'Fix all matched by name (1)' })).toBeEnabled()
    expect(within(tool).getByText('1 database wasn’t checked')).toBeInTheDocument()
    expect(within(tool).getByRole('table', { name: 'Databases without an owner' })).toHaveTextContent('Archive')

    // The usual fix: the login of its own name.
    await user.click(within(table).getByRole('button', { name: 'Map to app' }))
    await waitFor(() => expect(fixes[0]).toEqual({ action: 'map', database: 'Sales', user: 'app' }))
    expect(await within(tool).findByText('Fixed app.')).toBeInTheDocument()

    // A user that owns a schema can't be dropped: the dialog says so.
    fireEvent.contextMenu(within(table).getByText('report'))
    await user.click(screen.getByRole('menuitem', { name: 'Drop user…' }))
    const drop = await screen.findByRole('dialog', { name: 'Drop report?' })
    expect(within(drop).getByText(/owns reporting/)).toBeInTheDocument()
    expect(within(drop).getByRole('button', { name: 'Drop user' })).toBeDisabled()
    await user.click(within(drop).getByRole('button', { name: 'Cancel' }))

    // No login of its name: create one, keeping the user's SID.
    await user.click(within(table).getByRole('button', { name: 'Create login…' }))
    const create = await screen.findByRole('dialog', { name: 'Create a login for report' })
    expect(within(create).getByLabelText('Login name')).toHaveValue('report')
    expect(within(create).getByRole('button', { name: 'Create and map' })).toBeDisabled()
    await user.type(within(create).getByLabelText('Password'), 'S3cret-pass!')
    await user.click(within(create).getByRole('button', { name: 'Create and map' }))
    await waitFor(() =>
      expect(fixes[1]).toEqual({ action: 'create', database: 'Sales', user: 'report', login: 'report', password: 'S3cret-pass!', checkPolicy: true }),
    )
    expect(await within(tool).findByText('Fixed report.')).toBeInTheDocument()
  })

  it('selects orphaned users in bulk and drops them after confirming, listing the ones that couldn’t be', async () => {
    const sessions = sessionRoutes()
    const bulk: Record<string, unknown>[] = []
    const users: OrphanReport['users'] = [
      { database: 'Sales', user: 'app', type: 'SQL user', sid: '0x01', defaultSchema: 'dbo', matchingLogin: 'app', ownedSchemas: [] },
      { database: 'Sales', user: 'old1', type: 'SQL user', sid: '0x02', defaultSchema: 'dbo', matchingLogin: null, ownedSchemas: [] },
      { database: 'HR', user: 'old2', type: 'SQL user', sid: '0x03', defaultSchema: 'dbo', matchingLogin: null, ownedSchemas: ['legacy'] },
    ]
    const report: OrphanReport = { users, owners: [], logins: [{ name: 'app', windows: false, disabled: false }], skipped: [], databasesChecked: 2 }
    const { user } = renderWorkspace({
      ...sessions.routes,
      'GET /api/mssql/sessions/main/orphans': () => jsonResponse(200, report),
      'POST /api/mssql/sessions/main/orphans/fix-many': (_url, init) => {
        bulk.push(JSON.parse(String(init.body)))
        return jsonResponse(200, {
          report: { ...report, users: users.filter((u) => u.user !== 'old1') },
          message: 'Dropped 1 user. 1 couldn’t be.',
          results: [
            { database: 'Sales', user: 'old1', succeeded: true, message: 'Sales: user old1 dropped' },
            { database: 'HR', user: 'old2', succeeded: false, message: '“old2” owns the schema legacy, so it can’t be dropped.' },
          ],
        })
      },
    })
    await openSql(user)
    const tree = await screen.findByRole('tree', { name: 'Objects' })
    await user.dblClick(await within(tree).findByText('Management'))
    await user.dblClick(await within(tree).findByText('Orphaned Users'))
    const tool = await screen.findByRole('tabpanel', { name: 'Orphaned Users' })
    await within(tool).findByRole('table', { name: 'Orphaned users' })

    // Select the ones that can't simply be mapped back.
    await user.click(within(tool).getByRole('button', { name: 'Without a login of their name' }))
    expect(within(tool).getByRole('checkbox', { name: 'Select old1 in Sales' })).toBeChecked()
    expect(within(tool).getByRole('checkbox', { name: 'Select old2 in HR' })).toBeChecked()
    expect(within(tool).getByRole('checkbox', { name: 'Select app in Sales' })).not.toBeChecked()
    expect(within(tool).getByRole('checkbox', { name: 'Select all users' })).toBePartiallyChecked()
    expect(within(tool).getByRole('button', { name: 'Map selected to their logins (0)' })).toBeDisabled()

    await user.click(within(tool).getByRole('button', { name: 'Drop selected (2)…' }))
    const dialog = await screen.findByRole('dialog', { name: 'Drop 2 users?' })
    expect(within(dialog).getByText(/old2 own schemas/)).toBeInTheDocument()
    expect(bulk).toHaveLength(0)
    await user.click(within(dialog).getByRole('button', { name: 'Drop 2 users' }))

    await waitFor(() =>
      expect(bulk).toEqual([
        {
          fixes: [
            { action: 'drop', database: 'Sales', user: 'old1' },
            { action: 'drop', database: 'HR', user: 'old2' },
          ],
        },
      ]),
    )
    expect(await within(tool).findByText('Dropped 1 user. 1 couldn’t be.')).toBeInTheDocument()
    expect(within(tool).getByRole('list', { name: 'Couldn’t fix' })).toHaveTextContent('HR · old2: “old2” owns the schema legacy')
    expect(within(tool).getByRole('checkbox', { name: 'Select all users' })).not.toBeChecked()
  })

  it('shows blocking chains under their lead blocker, collapsed, and kills a session after confirming', async () => {
    const sessions = sessionRoutes()
    const killed: Record<string, unknown>[] = []
    const chain: BlockingReport = {
      leads: [
        {
          sessionId: 452,
          login: 'KOLIVER',
          host: 'AMTALPS2',
          program: '.Net SqlClient Data Provider',
          database: null,
          status: 'sleeping',
          openTransactions: 1,
          blockedBy: null,
          command: null,
          waitType: null,
          waitTimeMs: null,
          waitResource: null,
          sql: 'BEGIN TRAN; UPDATE dbo.Orders SET Total = 0',
          blocked: 1,
          blockedVolume: 4,
          blockedSessions: [
            {
              sessionId: 460,
              login: 'app',
              host: 'web01',
              program: 'api',
              database: 'Sales',
              status: 'suspended',
              openTransactions: 0,
              blockedBy: 452,
              command: 'UPDATE',
              waitType: 'LCK_M_X',
              waitTimeMs: 12_300,
              waitResource: 'KEY: 5:72057594043760640',
              sql: 'UPDATE dbo.Orders SET Total = 1',
              blocked: 3,
              blockedVolume: 3,
              blockedSessions: [],
            },
          ],
        },
      ],
      blockedCount: 4,
      canSeeAllSessions: true,
      atUtc: '2026-11-03T12:00:00Z',
    }
    let current = chain
    const { user } = renderWorkspace({
      ...sessions.routes,
      'GET /api/mssql/sessions/main/blocking': () => jsonResponse(200, current),
      'POST /api/mssql/sessions/main/kill': (_url, init) => {
        killed.push(JSON.parse(String(init.body)))
        current = { ...chain, leads: [], blockedCount: 0 }
        return new Response(null, { status: 204 })
      },
    })
    await openSql(user)

    // Management ▸ Blocking Manager, as in the explorer.
    const tree = await screen.findByRole('tree', { name: 'Objects' })
    await user.dblClick(await within(tree).findByText('Management'))
    await user.dblClick(await within(tree).findByText('Blocking Manager'))

    const manager = await screen.findByRole('tabpanel', { name: 'Blocking Manager' })
    expect(within(manager).getByRole('heading', { name: 'Lead Blockers' })).toBeInTheDocument()
    expect(within(manager).getByText('Sessions at the Root of Each Blocking Chain')).toBeInTheDocument()
    const table = await within(manager).findByRole('table', { name: 'Blocking chains' })
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Session',
      'Login',
      'Host',
      'Program',
      'Database',
      'Status',
      'Open Txns',
      'Blocked',
      'Blocked Volume',
      'Current SQL',
    ])
    const lead = within(table).getAllByRole('row')[1]
    expect(within(lead).getAllByRole('cell').map((c) => c.textContent)).toEqual([
      '▸452LEAD',
      'KOLIVER',
      'AMTALPS2',
      '.Net SqlClient Data Provider',
      'N/A',
      'sleeping',
      '1',
      '1',
      '4',
      'BEGIN TRAN; UPDATE dbo.Orders SET Total = 0',
    ])
    // Blocked sessions are collapsed under their lead until expanded.
    expect(within(table).queryByText('460')).not.toBeInTheDocument()
    await user.click(within(lead).getByRole('button', { name: 'Expand the sessions 452 blocks' }))
    const blocked = within(table).getByText('460').closest('tr')!
    expect(within(blocked).getByText('LCK_M_X · 12.3 s')).toBeInTheDocument()
    await user.click(within(manager).getByRole('button', { name: 'Collapse all' }))
    expect(within(table).queryByText('460')).not.toBeInTheDocument()

    fireEvent.contextMenu(lead)
    await user.click(screen.getByRole('menuitem', { name: 'Kill session 452…' }))
    const dialog = await screen.findByRole('dialog', { name: 'Kill session 452?' })
    expect(within(dialog).getByText('KOLIVER')).toBeInTheDocument()
    expect(killed).toHaveLength(0)
    await user.click(within(dialog).getByRole('button', { name: 'Kill session 452' }))

    await waitFor(() => expect(killed).toEqual([{ sessionId: 452 }]))
    expect(await within(manager).findByText('No blocking right now.')).toBeInTheDocument()
    expect(within(manager).getByText(/Session 452 was killed/)).toBeInTheDocument()
  })
})
