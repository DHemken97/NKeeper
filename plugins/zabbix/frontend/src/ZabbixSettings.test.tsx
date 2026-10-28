import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { antiforgeryRoute, jsonResponse, mockFetch } from '@nkeeper/plugin-sdk/testing'
import { ZabbixSettingsSection } from './components/ZabbixSettingsPanel'

function renderPage() {
  render(<ZabbixSettingsSection />)
  return userEvent.setup()
}

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
      'GET /api/plugins/zabbix/settings': () => jsonResponse(200, current),
      'POST /api/plugins/zabbix/settings/test': (_url, init) => {
        const body = JSON.parse(String(init.body))
        tests.push(body)
        return jsonResponse(
          200,
          body.trustFingerprint
            ? { status: 'ready', version: '7.0.5', hostCount: 9, certificate: null }
            : { status: 'trustRequired', version: null, hostCount: null, certificate },
        )
      },
      'PUT /api/plugins/zabbix/settings': (_url, init) => {
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
      'GET /api/plugins/zabbix/settings': () => jsonResponse(200, { ...zabbixSaved, enabled: true, url: 'https://zabbix.example.com/', hasApiToken: true }),
      'POST /api/plugins/zabbix/settings/test': () =>
        jsonResponse(400, { status: 400, errors: { apiToken: ['Zabbix at zabbix.example.com refused the API token: Not authorized.'] } }),
    })
    const user = renderPage()
    const panel = within(await screen.findByRole('region', { name: 'Zabbix' }))

    await user.click(panel.getByRole('button', { name: 'Test connection' }))

    expect((await panel.findAllByText('Zabbix at zabbix.example.com refused the API token: Not authorized.')).length).toBeGreaterThan(0)
  })
})
