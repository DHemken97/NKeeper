import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
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

const savedEmail = saved

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

const zabbixSaved = {
  enabled: false,
  url: '',
  hasApiToken: false,
  trustedCertificateFingerprint: null,
  updatedAtUtc: null,
}

const certificate = {
  fingerprint: 'AA:BB:CC',
  subject: 'CN=zabbix.example.com',
  issuer: 'CN=zabbix.example.com',
  expires: '2027-10-20T10:00:00Z',
}

describe('Settings: Zabbix', () => {
  it('asks to trust a self-signed certificate, then tests and saves with it', { timeout: 20_000 }, async () => {
    const tests: Record<string, unknown>[] = []
    let saved: Record<string, unknown> | undefined
    let current: Record<string, unknown> = zabbixSaved
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/admin/email': () => jsonResponse(200, savedEmail),
      'GET /api/admin/zabbix': () => jsonResponse(200, current),
      'POST /api/admin/zabbix/test': (_url, init) => {
        const body = JSON.parse(String(init.body))
        tests.push(body)
        return jsonResponse(
          200,
          body.trustFingerprint
            ? { status: 'ready', version: '7.0.5', hostCount: 9, certificate: null }
            : { status: 'trustRequired', version: null, hostCount: null, certificate },
        )
      },
      'PUT /api/admin/zabbix': (_url, init) => {
        saved = JSON.parse(String(init.body))
        current = { ...zabbixSaved, enabled: true, url: 'https://zabbix.example.com/', hasApiToken: true, updatedAtUtc: '2026-10-28T10:00:00Z' }
        return jsonResponse(200, current)
      },
    })
    const user = renderPage()
    const panel = within(await screen.findByRole('region', { name: 'Zabbix' }))

    await user.click(panel.getByLabelText('Read host information from Zabbix'))
    await user.type(panel.getByLabelText('Zabbix address'), 'https://zabbix.example.com')
    await user.type(panel.getByLabelText('API token'), 'secret-token')
    await user.click(panel.getByRole('button', { name: 'Test connection' }))

    expect(await panel.findByText('AA:BB:CC')).toBeInTheDocument()
    await user.click(panel.getByRole('button', { name: 'Trust and test' }))

    expect(await panel.findByText(/Connected to Zabbix 7\.0\.5\. The API token can see 9 hosts\./)).toBeInTheDocument()
    expect(tests[1]).toMatchObject({ url: 'https://zabbix.example.com', apiToken: 'secret-token', trustFingerprint: 'AA:BB:CC' })

    await user.click(panel.getByRole('button', { name: 'Save' }))
    await screen.findByText('A token is saved. Leave this empty to keep it.')
    expect(saved).toMatchObject({ enabled: true, apiToken: 'secret-token', trustFingerprint: 'AA:BB:CC' })
  })

  it('shows a refused token on its field', async () => {
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/admin/email': () => jsonResponse(200, savedEmail),
      'GET /api/admin/zabbix': () => jsonResponse(200, { ...zabbixSaved, enabled: true, url: 'https://zabbix.example.com/', hasApiToken: true }),
      'POST /api/admin/zabbix/test': () =>
        jsonResponse(400, { status: 400, errors: { apiToken: ['Zabbix at zabbix.example.com refused the API token: Not authorized.'] } }),
    })
    const user = renderPage()
    const panel = within(await screen.findByRole('region', { name: 'Zabbix' }))

    await user.click(panel.getByRole('button', { name: 'Test connection' }))

    expect((await panel.findAllByText('Zabbix at zabbix.example.com refused the API token: Not authorized.')).length).toBeGreaterThan(0)
  })
})
