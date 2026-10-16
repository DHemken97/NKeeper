import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { scanProposals } from './scanProposals'
import type { Host, ScanFindings } from './types'

const adminUser = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }

const web01: Host = {
  id: 'h-web01',
  name: 'web01',
  hostname: null,
  ipAddress: '10.0.0.30',
  macAddress: null,
  description: null,
  tags: [{ name: 'site', value: 'DC-A' }],
  services: [{ protocol: 'Ssh', port: null, label: null, rdpSecurity: null }],
  credentials: [{ id: 'c-ops', label: null, username: 'ops', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  canManage: true,
  createdAtUtc: '2026-10-01T12:00:00Z',
  updatedAtUtc: '2026-10-01T12:00:00Z',
  version: 7,
}

const findings: ScanFindings = {
  hostname: 'web01.corp.example',
  ipAddress: '10.0.0.30',
  macAddress: 'BC:24:11:AA:BB:CC',
  operatingSystem: 'Ubuntu 24.04.1 LTS',
  ports: [
    { port: 22, name: 'SSH', protocol: 'Ssh' },
    { port: 443, name: 'HTTPS', protocol: 'Https' },
    { port: 3306, name: 'MySQL / MariaDB', protocol: null },
  ],
  isKvmHost: false,
  users: [
    { username: 'root', uid: 0, fullName: 'root', shell: '/bin/bash' },
    { username: 'ops', uid: 1000, fullName: null, shell: '/bin/bash' },
    { username: 'dana', uid: 1001, fullName: 'Dana Admin', shell: '/bin/bash' },
  ],
  notes: ['libvirt was not found.'],
}

function renderWorkspace(routesOverride: Parameters<typeof mockFetch>[0], hosts: Host[] = [web01]) {
  const fetchMock = mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, adminUser),
    'GET /api/hosts': () => jsonResponse(200, hosts),
    ...routesOverride,
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
    </AuthProvider>,
  )
  return { fetchMock, user: userEvent.setup() }
}

async function openMenu(user: ReturnType<typeof userEvent.setup>, ...path: string[]) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /web01/ }))
  for (const item of path.slice(0, -1)) {
    await user.hover(screen.getByRole('menuitem', { name: new RegExp(`^${item}`) }))
  }
  await user.click(screen.getByRole('menuitem', { name: new RegExp(`^${path[path.length - 1]}`) }))
}

describe('scan proposals', () => {
  it('suggests only what the host lacks, and doesn’t assume root', () => {
    const proposals = scanProposals(web01, findings, 22)

    expect(proposals.map((p) => [p.key, p.checked])).toEqual([
      ['hostname', true],
      ['mac', true],
      ['os', true],
      ['service-Https-443', true],
      ['user-root', false],
      ['user-dana', true],
    ])
  })

  it('offers replacing values unchecked, and a KVM service for hypervisors', () => {
    const host = { ...web01, hostname: 'old.example', macAddress: 'AA:AA:AA:AA:AA:AA', tags: [{ name: 'os', value: 'Ubuntu 22.04' }] }
    const proposals = scanProposals(host, { ...findings, isKvmHost: true, users: [] }, 2222)

    expect(proposals.find((p) => p.key === 'hostname')).toMatchObject({ checked: false, detail: 'web01.corp.example (now old.example)' })
    expect(proposals.find((p) => p.key === 'mac')?.checked).toBe(false)
    expect(proposals.find((p) => p.key === 'os')).toMatchObject({ checked: true, detail: 'os: Ubuntu 24.04.1 LTS (now Ubuntu 22.04)' })
    expect(proposals.find((p) => p.key === 'service-Kvm-2222')).toBeDefined()
  })
})

describe('Tasks ▸ Scan', () => {
  it('runs an internal scan with a saved credential, confirms the host key, and applies the chosen findings', async () => {
    const scans: unknown[] = []
    let saved: Record<string, unknown> | undefined
    const { user } = renderWorkspace({
      'POST /api/hosts/h-web01/scan/internal': (_url, init) => {
        scans.push(JSON.parse(String(init.body)))
        return scans.length === 1
          ? jsonResponse(200, { status: 'trustRequired', findings: null, hostKey: { algorithm: 'ssh-ed25519', fingerprint: 'abc123' } })
          : jsonResponse(200, { status: 'ready', findings, hostKey: null })
      },
      'PUT /api/hosts/h-web01': (_url, init) => {
        saved = JSON.parse(String(init.body)) as Record<string, unknown>
        return jsonResponse(200, { ...web01, version: 8 })
      },
    })

    await openMenu(user, 'Tasks', 'Scan', 'Internal', 'ops')
    const dialog = await screen.findByRole('dialog', { name: 'Internal scan of web01' })
    expect(await within(dialog).findByText(/SHA256:abc123/)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Trust and scan' }))

    const results = await screen.findByRole('dialog', { name: 'Internal scan of web01' })
    expect(await within(results).findByRole('checkbox', { name: 'MAC address' })).toBeChecked()
    expect(within(results).getByRole('checkbox', { name: 'root' })).not.toBeChecked()
    expect(within(results).getByText('3306 (MySQL / MariaDB)')).toBeInTheDocument()
    expect(scans).toEqual([
      { serviceIndex: 0, credentialId: 'c-ops' },
      { serviceIndex: 0, credentialId: 'c-ops', trustFingerprint: 'abc123' },
    ])

    await user.click(within(results).getByRole('checkbox', { name: 'Hostname' }))
    await user.click(within(results).getByRole('button', { name: 'Apply 4 changes' }))

    await waitFor(() => expect(saved).toBeDefined())
    expect(saved).toMatchObject({
      hostname: null,
      macAddress: 'BC:24:11:AA:BB:CC',
      version: 7,
      tags: [
        { name: 'site', value: 'DC-A' },
        { name: 'os', value: 'Ubuntu 24.04.1 LTS' },
      ],
      services: [
        { protocol: 'Ssh', port: null },
        { protocol: 'Https', port: null },
      ],
    })
    // The new login has no password: using it asks for one.
    expect((saved!.credentials as unknown[]).slice(1)).toEqual([{ label: null, username: 'dana', domain: null, notes: null }])
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('asks for a login when the internal scan uses one-off credentials', async () => {
    let scanBody: unknown
    const { user } = renderWorkspace({
      'POST /api/hosts/h-web01/scan/internal': (_url, init) => {
        scanBody = JSON.parse(String(init.body))
        return jsonResponse(200, { status: 'ready', findings: { ...findings, users: [], ports: [], macAddress: null, hostname: null, operatingSystem: null }, hostKey: null })
      },
    })

    await openMenu(user, 'Tasks', 'Scan', 'Internal', 'Enter credentials')
    const prompt = await screen.findByRole('dialog', { name: 'Enter credentials' })
    await user.type(within(prompt).getByLabelText('Username'), 'admin')
    await user.type(within(prompt).getByLabelText('Password'), 's3cret')
    await user.click(within(prompt).getByRole('button', { name: 'Scan' }))

    expect(await screen.findByText('The scan found nothing that the host doesn’t already have.')).toBeInTheDocument()
    expect(scanBody).toEqual({ serviceIndex: 0, username: 'admin', password: 's3cret' })
  })

  it('starts an external scan only after the warning, and reports failures', async () => {
    let scans = 0
    const { user } = renderWorkspace({
      'POST /api/hosts/h-web01/scan/external': () => {
        scans++
        return jsonResponse(502, { title: 'Bad Gateway', status: 502, detail: '“web01” couldn’t be resolved to an address.', code: 'host_not_found' })
      },
    })

    await openMenu(user, 'Tasks', 'Scan', 'External')
    const dialog = await screen.findByRole('dialog', { name: 'External scan of web01' })
    expect(within(dialog).getByText(/Network monitoring will see this as a port scan/)).toBeInTheDocument()
    expect(scans).toBe(0)

    await user.click(within(dialog).getByRole('button', { name: 'Start scan' }))
    expect(await within(dialog).findByText('“web01” couldn’t be resolved to an address.')).toBeInTheDocument()
    expect(scans).toBe(1)
  })

  it('is only offered on hosts the user can manage', async () => {
    const { user } = renderWorkspace({}, [{ ...web01, canManage: false }])

    fireEvent.contextMenu(await screen.findByRole('button', { name: /web01/ }))
    expect(screen.getByRole('menuitem', { name: /^Connect/ })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /^Tasks/ })).not.toBeInTheDocument()
    await user.keyboard('{Escape}')
  })
})

describe('credentials without a password', () => {
  it('ask for the password when connecting, with the username filled in', async () => {
    const host: Host = {
      ...web01,
      credentials: [{ id: 'c-dana', label: null, username: 'dana', domain: null, notes: null, hasPassword: false, hasPrivateKey: false }],
    }
    const { user } = renderWorkspace({}, [host])

    fireEvent.contextMenu(await screen.findByRole('button', { name: /web01/ }))
    await user.hover(screen.getByRole('menuitem', { name: /^Connect/ }))
    await user.hover(screen.getByRole('menuitem', { name: /^SSH/ }))
    const dana = screen.getByRole('menuitem', { name: /^dana/ })
    expect(dana).toHaveTextContent('Asks for password')
    expect(dana).not.toHaveAttribute('aria-disabled', 'true')
    await user.click(dana)

    const prompt = await screen.findByRole('dialog', { name: 'Enter credentials' })
    expect(within(prompt).getByLabelText('Username')).toHaveValue('dana')
    expect(within(prompt).getByLabelText('Password')).toHaveFocus()
  })
})
