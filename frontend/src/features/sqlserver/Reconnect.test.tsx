import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const ora01: Host = {
  id: 'h-ora01',
  name: 'ora01',
  hostname: null,
  ipAddress: '10.0.0.41',
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
  services: [{ protocol: 'Oracle', port: null, label: null, instance: 'ORCLPDB1' }],
  credentials: [{ id: 'c-dba', label: null, username: 'dba', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const gone = () => jsonResponse(404, { status: 404, title: 'This session has ended.', code: 'session_not_found' })

afterEach(() => {
  vi.useRealTimers()
})

describe('a database tab whose sessions the server closes', () => {
  it('stays as it is, disconnected, and reconnects when the explorer or a query needs it', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const opened: string[] = []
    const closedByServer = new Set<string>()
    const queriedOn: string[] = []
    const open = () => {
      const id = opened.length === 0 ? 'main' : opened.length === 1 ? 'win1' : `again${opened.length}`
      opened.push(id)
      return jsonResponse(200, {
        status: 'ready',
        sessionId: id,
        target: 'dba@10.0.0.41',
        server: { serverName: 'ora01', productVersion: '19.0', productLevel: null, edition: null, login: 'dba', sessionId: opened.length },
        database: 'APP',
        certificate: null,
      })
    }
    const live = (id: string, respond: () => Response) => (closedByServer.has(id) ? gone() : respond())
    const idOf = (url: string) => /sessions\/([^/]+)/.exec(url)![1]

    mockFetch({
      ...antiforgeryRoute,
      'GET /api/auth/me': () => jsonResponse(200, admin),
      'GET /api/hosts': () => jsonResponse(200, [ora01]),
      'POST /api/oracle/sessions': open,
      ...Object.fromEntries(
        ['main', 'win1', 'again2', 'again3', 'again4'].flatMap((id) => [
          [`GET /api/oracle/sessions/${id}/databases`, () => live(id, () => jsonResponse(200, ['APP']))],
          [`POST /api/oracle/sessions/${id}/keepalive`, () => live(id, () => new Response(null, { status: 204 }))],
          [`DELETE /api/oracle/sessions/${id}`, () => new Response(null, { status: 204 })],
          [
            `POST /api/oracle/sessions/${id}/explore`,
            (url: string, init: RequestInit) =>
              live(idOf(url), () =>
                jsonResponse(
                  200,
                  JSON.parse(String(init.body)).node.kind === 'server'
                    ? [{ kind: 'management', label: 'Management', expandable: true }]
                    : [{ kind: 'tool', label: 'Storage', expandable: false, name: 'storage' }],
                ),
              ),
          ],
          [
            `POST /api/oracle/sessions/${id}/query`,
            (url: string) =>
              live(idOf(url), () => {
                queriedOn.push(idOf(url))
                return jsonResponse(200, {
                  output: [{ kind: 'message', text: 'done' }],
                  database: 'APP',
                  elapsedMs: 1,
                  cancelled: false,
                  connectionLost: false,
                  hasErrors: false,
                })
              }),
          ],
        ]),
      ),
    })
    render(
      <AuthProvider>
        <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
      </AuthProvider>,
    )
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })

    fireEvent.contextMenu(await screen.findByRole('button', { name: /ora01/ }))
    await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
    await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
    await user.hover(screen.getByRole('menuitem', { name: /^Oracle/ }))
    await user.hover(screen.getByRole('menuitem', { name: /^ORCLPDB1/ }))
    await user.click(screen.getByRole('menuitem', { name: /^dba/ }))
    const tree = await screen.findByRole('tree', { name: 'Objects' })
    const editor = await screen.findByRole('textbox', { name: 'SQLQuery1 query' })
    await waitFor(() => expect(screen.getByRole('button', { name: /Execute/ })).toBeEnabled())

    // The server closes both sessions (idle); the next keep-alives find them gone.
    closedByServer.add('main')
    closedByServer.add('win1')
    await act(async () => {
      await vi.advanceTimersByTimeAsync(61_000)
    })

    expect(await screen.findByText('Disconnected · reconnects when used')).toBeInTheDocument()
    expect(screen.getByText('Disconnected · reconnects when you run')).toBeInTheDocument()
    expect(screen.queryByText(/has ended/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Reconnect/ })).not.toBeInTheDocument()
    expect(editor).toBeInTheDocument()

    // The explorer reconnects the tab's session to expand Management.
    await user.dblClick(within(tree).getByText('Management'))
    expect(await within(tree).findByText('Storage')).toBeInTheDocument()
    expect(opened).toEqual(['main', 'win1', 'again2'])
    expect(screen.queryByText('Disconnected · reconnects when used')).not.toBeInTheDocument()

    // Running the query reconnects the window and runs it there.
    fireEvent.change(editor, { target: { value: 'SELECT 1 FROM dual' } })
    fireEvent.keyDown(editor, { key: 'F5' })
    await waitFor(() => expect(queriedOn).toEqual(['again3']))
    expect(opened).toEqual(['main', 'win1', 'again2', 'again3'])
  })
})
