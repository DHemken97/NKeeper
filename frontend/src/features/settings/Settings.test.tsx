import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
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

const diagnostics = {
  enabled: true,
  maxMegabytes: 200,
  minMegabytes: 20,
  ceilingMegabytes: 10240,
  sizeBytes: 3 * 1024 * 1024,
  oldestUtc: '2026-11-01T10:00:00Z',
  newestUtc: '2026-11-06T10:00:00Z',
  lastDownloadedUtc: null,
  droppedUtc: null,
  pausedForDiskSpace: false,
  writeError: null,
  directory: '/var/lib/nkeeper/diagnostics',
  version: '1.0',
}

describe('Settings: diagnostics', () => {
  const emailRoute = { 'GET /api/admin/email': () => jsonResponse(200, saved), 'GET /api/admin/zabbix': () => jsonResponse(200, { enabled: false, url: '', hasApiToken: false }) }

  it('shows the running version at the bottom of the page', async () => {
    mockFetch({ ...emailRoute })
    renderPage()

    const version = await screen.findByText('NKeeper version 1.0')
    expect(version.parentElement?.lastElementChild).toBe(version)
  })

  it('shows what the file holds, and saves the switch and the size limit', async () => {
    let body: Record<string, unknown> | undefined
    mockFetch({
      ...antiforgeryRoute,
      ...emailRoute,
      'GET /api/admin/diagnostics': () => jsonResponse(200, diagnostics),
      'PUT /api/admin/diagnostics': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, { ...diagnostics, enabled: false, maxMegabytes: 500 })
      },
    })
    const user = renderPage()
    const panel = within(await screen.findByRole('region', { name: 'Diagnostics' }))

    expect(panel.getByText(/3\.0 MB on disk/)).toBeInTheDocument()
    expect(panel.getByText(/Never downloaded/)).toBeInTheDocument()
    expect(panel.getByText(/\/var\/lib\/nkeeper\/diagnostics/)).toBeInTheDocument()

    await user.click(panel.getByLabelText('Keep the diagnostics file'))
    await user.clear(panel.getByLabelText('Size limit (MB)'))
    await user.type(panel.getByLabelText('Size limit (MB)'), '500')
    await user.click(panel.getByRole('button', { name: 'Save' }))

    expect(await panel.findByText('Saved. It applies now.')).toBeInTheDocument()
    expect(body).toEqual({ enabled: false, maxMegabytes: 500 })
  })

  it('shows a refused size limit on its field', async () => {
    mockFetch({
      ...antiforgeryRoute,
      ...emailRoute,
      'PUT /api/admin/diagnostics': () => jsonResponse(400, { status: 400, errors: { maxMegabytes: ['Too small.'] } }),
    })
    const user = renderPage()
    const panel = within(await screen.findByRole('region', { name: 'Diagnostics' }))

    await user.click(panel.getByRole('button', { name: 'Save' }))

    expect(await panel.findByText('Too small.')).toBeInTheDocument()
  })

  it('warns when writing paused, failed, or data went before it was downloaded', async () => {
    mockFetch({
      ...emailRoute,
      'GET /api/admin/diagnostics': () =>
        jsonResponse(200, { ...diagnostics, pausedForDiskSpace: true, writeError: 'Access denied', droppedUtc: '2026-11-05T10:00:00Z' }),
    })
    renderPage()
    const panel = within(await screen.findByRole('region', { name: 'Diagnostics' }))

    expect(panel.getByText(/Writing is paused/)).toBeInTheDocument()
    expect(panel.getByText(/Access denied/)).toBeInTheDocument()
    expect(panel.getByText(/so there’s a gap/)).toBeInTheDocument()
  })

  it('downloads the file and then shows when', async () => {
    const createObjectURL = vi.fn(() => 'blob:diagnostics')
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() }))
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    let current: typeof diagnostics | (Omit<typeof diagnostics, 'lastDownloadedUtc'> & { lastDownloadedUtc: string }) = diagnostics
    mockFetch({
      ...emailRoute,
      'GET /api/admin/diagnostics': () => jsonResponse(200, current),
      'GET /api/admin/diagnostics/download': () => {
        current = { ...diagnostics, lastDownloadedUtc: '2026-11-06T12:00:00Z' }
        return new Response(new Blob(['zip']), { status: 200 })
      },
    })
    const user = renderPage()
    const panel = within(await screen.findByRole('region', { name: 'Diagnostics' }))

    await user.click(panel.getByRole('button', { name: 'Download diagnostics' }))

    expect(await panel.findByText(/Last downloaded/)).toBeInTheDocument()
    expect(createObjectURL).toHaveBeenCalledOnce()
    expect(click).toHaveBeenCalledOnce()
    click.mockRestore()
  })

  it('has nothing to download until something is recorded', async () => {
    mockFetch({ ...emailRoute, 'GET /api/admin/diagnostics': () => jsonResponse(200, { ...diagnostics, sizeBytes: 0, oldestUtc: null, newestUtc: null }) })
    renderPage()
    const panel = within(await screen.findByRole('region', { name: 'Diagnostics' }))

    expect(panel.getByText(/Nothing recorded yet/)).toBeInTheDocument()
    expect(panel.getByRole('button', { name: 'Download diagnostics' })).toBeDisabled()
  })
})

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
    await user.click(within(screen.getByRole('region', { name: 'Email' })).getByRole('button', { name: 'Save' }))

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

describe('Settings: limits', () => {
  it('shows the limits in force and saves new ones', async () => {
    const puts: unknown[] = []
    mockFetch({
      ...antiforgeryRoute,
      'GET /api/admin/email': () => jsonResponse(200, savedEmail),
      'GET /api/admin/zabbix': () => jsonResponse(200, { enabled: false, url: '', hasToken: false, trustedFingerprint: null, updatedAtUtc: null }),
      'PUT /api/admin/limits': (_url, init) => {
        const body = JSON.parse(String(init.body))
        puts.push(body)
        return jsonResponse(200, { ...body, updatedAtUtc: '2026-11-04T12:00:00Z', ceilingServices: 1000, ceilingCredentials: 1000, ceilingTags: 500, ceilingScanUsers: 1000, ceilingZipReserveGigabytes: 1000 })
      },
    })
    const user = renderPage()

    const panel = within(await screen.findByRole('region', { name: 'Limits' }))
    expect(panel.getByLabelText('Services per host')).toHaveValue(100)
    expect(panel.getByLabelText('Users a scan ticks for you')).toHaveValue(10)
    await user.clear(panel.getByLabelText('Services per host'))
    await user.type(panel.getByLabelText('Services per host'), '250')
    await user.clear(panel.getByLabelText('Users a scan ticks for you'))
    await user.type(panel.getByLabelText('Users a scan ticks for you'), '5')
    expect(panel.getByLabelText('Disk space to keep free when zipping (GB)')).toHaveValue(5)
    await user.clear(panel.getByLabelText('Disk space to keep free when zipping (GB)'))
    await user.type(panel.getByLabelText('Disk space to keep free when zipping (GB)'), '20')
    await user.click(panel.getByRole('button', { name: 'Save' }))

    expect(await panel.findByText('Saved. They apply now.')).toBeInTheDocument()
    expect(puts).toEqual([{ maxServicesPerHost: 250, maxCredentialsPerHost: 100, maxTagsPerHost: 50, scanUsersPreselectedUpTo: 5, zipReserveGigabytes: 20 }])
  })
})

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
