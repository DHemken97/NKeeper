import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { definePlugin, resetPlugins } from '../../sdk/registry'
import { SettingsPage } from './pages/SettingsPage'

const saved = {
  enabled: true,
  host: 'smtp.example.com',
  port: 587,
  security: 'StartTls',
  userName: 'nkeeper',
  hasPassword: true,
  fromAddress: 'nkeeper@example.com',
  fromName: null,
  updatedAtUtc: '2026-10-20T10:00:00Z',
}

function renderPage() {
  render(
    <MemoryRouter>
      <SettingsPage />
    </MemoryRouter>,
  )
  return userEvent.setup()
}

describe('Settings: email', () => {
  it('shows the saved settings without the password, and keeps it when saving', async () => {
    let body: Record<string, unknown> | undefined
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/admin/email': () => jsonResponse(200, saved),
      'PUT /api/admin/email': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, saved)
      },
    })
    const user = renderPage()

    expect(await screen.findByLabelText('SMTP server')).toHaveValue('smtp.example.com')
    expect(screen.getByLabelText('Password')).toHaveValue('')
    expect(screen.getByText('A password is saved. Leave this empty to keep it.')).toBeInTheDocument()

    await user.clear(screen.getByLabelText('SMTP server'))
    await user.type(screen.getByLabelText('SMTP server'), 'mail.example.com')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await screen.findByLabelText('SMTP server')
    expect(body).toMatchObject({ host: 'mail.example.com', password: null, clearPassword: false, security: 'StartTls', port: 587 })
  })

  it('follows the usual port when the encryption changes', async () => {
    mockFetch({ 'GET /api/admin/email': () => jsonResponse(200, saved) })
    const user = renderPage()

    await user.selectOptions(await screen.findByLabelText('Encryption'), 'SslOnConnect')

    expect(screen.getByLabelText('Port')).toHaveValue(465)
  })

  it('sends a test email and says where it went', async () => {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/admin/email': () => jsonResponse(200, saved),
      'POST /api/admin/email/test': () => jsonResponse(200, { sentTo: 'admin@example.com' }),
    })
    const user = renderPage()

    await user.click(await screen.findByRole('button', { name: 'Send test email' }))

    expect(await screen.findByText('Test email sent to admin@example.com. Check that it arrived.')).toBeInTheDocument()
  })
})


describe('Settings: plugins', () => {
  afterEach(() => resetPlugins())

  it('shows the installed plugins, why one wasn’t loaded, and the loaded ones’ own settings', async () => {
    definePlugin({ id: 'demo', settingsSections: [() => <section aria-label="Demo settings">Demo options</section>] })
    mockFetch({
      'GET /api/admin/email': () => jsonResponse(200, saved),
      'GET /api/admin/plugins': () =>
        jsonResponse(200, {
          sdkVersion: '1.0',
          plugins: [
            { id: 'demo', name: 'Demo', version: '1.2.0', description: 'Shows how.', author: null, status: 'loaded', error: null, signer: 'ab12cd34ef56ab12', source: 'demo.nkplugin', dependencies: [], hasBackend: true, hasFrontend: true },
            { id: 'old', name: 'Old', version: '0.1.0', description: null, author: null, status: 'failed', error: 'It needs plugin SDK 2.0; this server has 1.0.', signer: 'ab12cd34ef56ab12', source: 'old.nkplugin', dependencies: [], hasBackend: true, hasFrontend: false },
          ],
          rejected: [{ source: 'evil.nkplugin', error: 'The package is signed with key 0000000000000000, which this server doesn’t trust.' }],
          trustedKeys: ['ab12cd34ef56ab12'],
        }),
    })
    renderPage()

    expect(await screen.findByRole('region', { name: 'Demo settings' })).toHaveTextContent('Demo options')
    const plugins = within(await screen.findByRole('region', { name: 'Plugins' }))
    expect(await plugins.findByText('Signed with key ab12cd34ef56ab12 · demo.nkplugin')).toBeInTheDocument()
    expect(plugins.getByText('It needs plugin SDK 2.0; this server has 1.0.')).toBeInTheDocument()
    expect(plugins.getByText(/evil\.nkplugin was refused/)).toBeInTheDocument()
  })
})
