import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from '../inventory/types'

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
  plugins: {},
  tags: [],
  services: [{ protocol: 'Ssh', port: null, label: null }],
  credentials: [{ id: 'c-root', label: null, username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

const entry = (name: string, kind: 'directory' | 'file', path = `/root/${name}`) => ({
  name,
  path,
  kind,
  size: kind === 'file' ? 2048 : 0,
  modifiedUtc: '2026-10-10T12:00:00Z',
  permissions: kind === 'file' ? '-rw-r--r--' : 'drwxr-xr-x',
})

const home = {
  status: 'ready',
  path: '/root',
  parent: '/',
  entries: [entry('logs', 'directory'), entry('notes.txt', 'file')],
  hostKey: null,
}

function renderWorkspace(routesOverride: Parameters<typeof mockFetch>[0]) {
  const fetchMock = mockFetch({
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
  return { fetchMock, user: userEvent.setup() }
}

async function openFiles(user: ReturnType<typeof userEvent.setup>) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /app01/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: /^Files/ }))
  await user.click(screen.getByRole('menuitem', { name: /^root/ }))
  return screen.findByRole('tabpanel', { name: 'app01 files' })
}

describe('files (SFTP)', () => {
  it('asks to trust the host key, then lists the home folder and opens a subfolder', async () => {
    const bodies: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/sftp/list': (_url, init) => {
        const body = JSON.parse(String(init.body))
        bodies.push(body)
        if (bodies.length === 1) {
          return jsonResponse(200, { status: 'trustRequired', path: null, parent: null, entries: null, hostKey: { algorithm: 'ssh-ed25519', fingerprint: 'SHA256:abc' } })
        }
        return jsonResponse(
          200,
          body.path === '/root/logs' ? { ...home, path: '/root/logs', parent: '/root', entries: [entry('app.log', 'file', '/root/logs/app.log')] } : home,
        )
      },
    })

    const panel = await openFiles(user)
    await user.click(await within(panel).findByRole('button', { name: 'Trust and connect' }))

    expect(await within(panel).findByRole('button', { name: /logs/ })).toBeInTheDocument()
    expect(within(panel).getByText('2.0 KB')).toBeInTheDocument()
    expect(bodies[0]).toMatchObject({ hostId: 'h-app01', serviceIndex: 0, credentialId: 'c-root', path: null })
    expect(bodies[1]).toMatchObject({ trustFingerprint: 'SHA256:abc', path: null })

    await user.click(within(panel).getByRole('button', { name: /logs/ }))

    expect(await within(panel).findByText(/app\.log/)).toBeInTheDocument()
    expect(bodies[2]).toMatchObject({ path: '/root/logs', trustFingerprint: 'SHA256:abc' })
    expect(within(panel).getByRole('navigation', { name: 'Folder' })).toHaveTextContent('/root/logs')
  })

  it('offers to replace a file that already exists, and re-uploads with overwrite', async () => {
    const overwrites: (string | null)[] = []
    const { user } = renderWorkspace({
      'POST /api/sftp/list': () => jsonResponse(200, home),
      'POST /api/sftp/upload': (_url, init) => {
        const form = init.body as FormData
        overwrites.push(form.get('overwrite') as string | null)
        expect(form.get('directory')).toBe('/root')
        expect((form.get('files') as File).name).toBe('notes.txt')
        return overwrites.length === 1
          ? jsonResponse(409, { title: 'Conflict', status: 409, code: 'file_exists', detail: 'notes.txt already exists.' })
          : jsonResponse(200, home)
      },
    })

    const panel = await openFiles(user)
    await within(panel).findByRole('button', { name: /logs/ })
    await user.upload(within(panel).getByLabelText('Files to upload'), new File(['hello'], 'notes.txt', { type: 'text/plain' }))

    await user.click(await within(panel).findByRole('button', { name: 'Replace' }))

    expect(await within(panel).findByText('Uploaded notes.txt.')).toBeInTheDocument()
    expect(overwrites).toEqual(['false', 'true'])
  })

  it('deletes only after confirming', async () => {
    const bodies: Record<string, unknown>[] = []
    const { user } = renderWorkspace({
      'POST /api/sftp/list': () => jsonResponse(200, home),
      'POST /api/sftp/delete': (_url, init) => {
        bodies.push(JSON.parse(String(init.body)))
        return jsonResponse(200, home)
      },
    })

    const panel = await openFiles(user)
    const row = (await within(panel).findByText(/notes\.txt/)).closest('tr')!
    await user.click(within(row).getByRole('button', { name: 'Delete' }))
    expect(bodies).toEqual([])
    await user.click(within(row).getByRole('button', { name: 'Delete notes.txt' }))

    expect(await within(panel).findByText('Deleted notes.txt.')).toBeInTheDocument()
    expect(bodies).toEqual([expect.objectContaining({ hostId: 'h-app01', credentialId: 'c-root', path: '/root/notes.txt' })])
  })

  it('renames within the same folder', async () => {
    let body: Record<string, unknown> | undefined
    const { user } = renderWorkspace({
      'POST /api/sftp/list': () => jsonResponse(200, home),
      'POST /api/sftp/rename': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, home)
      },
    })

    const panel = await openFiles(user)
    const row = (await within(panel).findByText(/notes\.txt/)).closest('tr')!
    await user.click(within(row).getByRole('button', { name: 'Rename' }))
    const name = within(row).getByLabelText('New name for notes.txt')
    await user.clear(name)
    await user.type(name, 'todo.txt{Enter}')

    await waitFor(() => expect(body).toBeDefined())
    expect(body).toMatchObject({ path: '/root/notes.txt', newPath: 'todo.txt' })
  })
})
