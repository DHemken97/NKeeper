import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { generatePassphrase, type ImportSummary, type TransferStatus } from './transferApi'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const busy: TransferStatus = {
  canImport: false,
  importBlockedReason: 'This instance already has hosts.',
  locked: false,
  lockedAtUtc: null,
  lockedBy: null,
  version: '1.0.0',
}
const fresh: TransferStatus = { ...busy, canImport: true, importBlockedReason: null }

const summary: ImportSummary = {
  exportedBy: 'admin',
  exportedAtUtc: '2026-10-17T09:00:00Z',
  sourceVersion: '1.0.0',
  users: 3,
  administrators: 1,
  grants: 2,
  hosts: 40,
  credentials: 25,
  trustedKeys: 12,
  warnings: ['1 account has no password; an administrator must set one before they can sign in.'],
  imported: false,
}

let downloads: string[]

beforeEach(() => {
  downloads = []
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:export', revokeObjectURL: () => {} }))
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    downloads.push(this.download)
  })
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

function renderPage(routesOverride: Parameters<typeof mockFetch>[0]) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    ...routesOverride,
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/transfer'] })} />
    </AuthProvider>,
  )
  return { fetchMock, user: userEvent.setup() }
}

describe('moving to a new server', () => {
  it('generates passphrases of six readable groups, different every time', () => {
    const passphrase = generatePassphrase()
    expect(passphrase).toMatch(/^[a-hj-km-np-z2-9]{5}(-[a-hj-km-np-z2-9]{5}){5}$/)
    expect(generatePassphrase()).not.toBe(passphrase)
  })

  it('exports with the generated passphrase, confirming with the admin’s password, and locks', async () => {
    let body: Record<string, unknown> | undefined
    const { user } = renderPage({
      'GET /api/admin/transfer/status': () => jsonResponse(200, busy),
      'POST /api/admin/transfer/export': (_url, init) => {
        body = JSON.parse(String(init.body))
        return new Response(new Blob(['bundle']), { status: 200, headers: { 'Content-Type': 'application/octet-stream' } })
      },
    })

    const shown = (await screen.findByLabelText('Generated passphrase')).textContent
    await user.type(screen.getByLabelText(/Your password/), 'my-password')
    await user.click(screen.getByRole('button', { name: 'Export and download' }))

    await waitFor(() => expect(body).toEqual({ password: 'my-password', passphrase: shown, lockAfterExport: true }))
    expect(downloads).toHaveLength(1)
    expect(downloads[0]).toMatch(/^nkeeper-\d{4}-\d\d-\d\d\.nkeeper$/)
    expect(await screen.findByText(/delete it from this computer once the import has worked/)).toBeInTheDocument()
    // The import panel explains why this server can't receive one.
    expect(screen.getByText(/This instance already has hosts/)).toBeInTheDocument()
  })

  it('refuses a short custom passphrase, and says when the password is wrong', async () => {
    const { user, fetchMock } = renderPage({
      'GET /api/admin/transfer/status': () => jsonResponse(200, busy),
      'POST /api/admin/transfer/export': () => jsonResponse(403, { status: 403, code: 'password_incorrect', detail: 'That isn’t your password.' }),
    })

    await user.type(await screen.findByLabelText(/Your password/), 'wrong')
    await user.click(screen.getByRole('radio', { name: 'Choose my own' }))
    await user.type(screen.getByLabelText(/^Passphrase/), 'too short')
    await user.click(screen.getByRole('button', { name: 'Export and download' }))
    expect(await screen.findByText('Use at least 16 characters.')).toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/export'))).toBe(false)

    await user.type(screen.getByLabelText(/^Passphrase/), ' but now long enough')
    await user.click(screen.getByRole('button', { name: 'Export and download' }))
    expect(await screen.findByText('That isn’t your password.')).toBeInTheDocument()
    expect(downloads).toHaveLength(0)
  })

  it('checks the file on a fresh server, then imports only once confirmed', async () => {
    const sent: { path: string; passphrase: FormDataEntryValue | null; confirm: FormDataEntryValue | null }[] = []
    const record = (path: string) => (_url: string, init: RequestInit) => {
      const form = init.body as FormData
      sent.push({ path, passphrase: form.get('passphrase'), confirm: form.get('confirm') })
      return jsonResponse(200, path === 'import' ? { ...summary, imported: true } : summary)
    }
    const { user } = renderPage({
      'GET /api/admin/transfer/status': () => jsonResponse(200, fresh),
      'POST /api/admin/transfer/import/preview': record('preview'),
      'POST /api/admin/transfer/import': record('import'),
    })

    const file = new File(['bundle'], 'nkeeper-2026-10-17.nkeeper')
    await user.upload(await screen.findByLabelText('Export file'), file)
    const passphrase = screen.getAllByLabelText(/^Passphrase/).at(-1)!
    await user.type(passphrase, 'abcde-fghjk-mnpqr-stuvw-xyz23-45678')
    await user.click(screen.getByRole('button', { name: 'Check the file' }))

    const held = await screen.findByLabelText('What the file holds')
    expect(within(held).getByText('3 (1 administrator)')).toBeInTheDocument()
    expect(within(held).getByText('40')).toBeInTheDocument()
    expect(screen.getByText(/1 account has no password/)).toBeInTheDocument()

    const importButton = screen.getByRole('button', { name: 'Import' })
    expect(importButton).toBeDisabled()
    await user.click(screen.getByRole('checkbox', { name: /Replace this server’s data/ }))
    await user.click(importButton)

    expect(await screen.findByText(/sign in with your account from the old server/)).toBeInTheDocument()
    expect(sent).toEqual([
      { path: 'preview', passphrase: 'abcde-fghjk-mnpqr-stuvw-xyz23-45678', confirm: 'false' },
      { path: 'import', passphrase: 'abcde-fghjk-mnpqr-stuvw-xyz23-45678', confirm: 'true' },
    ])
  })

  it('shows a locked server, and unlocks it when the move is called off', async () => {
    let unlocked = false
    const { user } = renderPage({
      'GET /api/admin/transfer/status': () =>
        jsonResponse(200, unlocked ? busy : { ...busy, locked: true, lockedBy: 'admin', lockedAtUtc: '2026-10-17T09:00:00Z' }),
      'POST /api/admin/transfer/unlock': () => {
        unlocked = true
        return jsonResponse(204)
      },
    })

    expect(await screen.findByRole('heading', { name: 'This instance is locked' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /Unlock/ }))

    await waitFor(() => expect(screen.queryByRole('heading', { name: 'This instance is locked' })).not.toBeInTheDocument())
  })
})
