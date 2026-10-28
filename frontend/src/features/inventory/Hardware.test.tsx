import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { toCloneDraft } from './drafts'
import { scanProposals } from './scanProposals'
import { formatMemory, type Host, type ScanFindings } from './types'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const r750: Host = {
  id: 'h-r750',
  name: 'r750',
  hostname: null,
  ipAddress: '10.0.0.20',
  macAddress: null,
  description: null,
  isVirtualMachine: false,
  hypervisorId: null,
  virtualMachineId: null,
  hypervisorSeenAtUtc: null,
  cpuCores: 64,
  memoryMiB: 262144,
  serialNumber: 'ABC1234',
  jumpHostId: null,
  jumpCredentialId: null,
  plugins: {},
  tags: [],
  services: [],
  credentials: [],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 3,
  canManage: true,
}

function renderAt(path: string, routesOverride: Parameters<typeof mockFetch>[0] = {}) {
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [r750]),
    ...routesOverride,
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: [path] })} />
    </AuthProvider>,
  )
  return userEvent.setup()
}

describe('host hardware', () => {
  it('shows CPU cores, memory and the serial number in the details', async () => {
    renderAt('/?host=h-r750')

    const details = await screen.findByRole('article', { name: 'r750' })
    expect(within(details).getByText('64 cores · 256 GiB')).toBeInTheDocument()
    expect(within(details).getByText('ABC1234')).toBeInTheDocument()
  })

  it('edits them, with memory typed in GiB and saved in MiB', async () => {
    let body: Record<string, unknown> | undefined
    const user = renderAt('/?host=h-r750&edit=1', {
      'PUT /api/hosts/h-r750': (_url, init) => {
        body = JSON.parse(String(init.body))
        return jsonResponse(200, r750)
      },
    })

    const memory = await screen.findByLabelText(/Memory \(GiB\)/)
    expect(memory).toHaveValue('256')
    await user.clear(memory)
    await user.type(memory, '1.5')
    await user.clear(screen.getByLabelText(/CPU cores/))
    await user.type(screen.getByLabelText(/CPU cores/), 'many')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByText('Enter a whole number of cores.')).toBeInTheDocument()
    expect(body).toBeUndefined()

    await user.clear(screen.getByLabelText(/CPU cores/))
    await user.type(screen.getByLabelText(/CPU cores/), '128')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(body).toBeDefined())
    expect(body).toMatchObject({ cpuCores: 128, memoryMiB: 1536, serialNumber: 'ABC1234' })
  })

  it('doesn’t copy the serial number when cloning', () => {
    const draft = toCloneDraft(r750)
    expect([draft.cpuCores, draft.memoryGiB, draft.serialNumber]).toEqual(['64', '256', ''])
  })

  it('offers what a scan found, filling gaps by default', () => {
    const findings: ScanFindings = {
      hostname: null,
      ipAddress: null,
      macAddress: null,
      operatingSystem: null,
      ports: [],
      isKvmHost: false,
      users: [],
      notes: [],
      virtualization: null,
      guests: [],
      neighbors: [],
      cpuCores: 128,
      memoryMiB: 524288,
      serialNumber: 'ABC1234',
    }

    const blank = scanProposals({ ...r750, cpuCores: null, memoryMiB: null, serialNumber: null }, findings, 22)
    expect(blank.map((p) => [p.label, p.detail, p.checked])).toEqual([
      ['CPU cores', '128', true],
      ['Memory', '512 GiB', true],
      ['Serial number', 'ABC1234', true],
    ])

    // Replacing a value is offered, not assumed; the same value isn't offered at all.
    const known = scanProposals(r750, findings, 22)
    expect(known.map((p) => [p.label, p.detail, p.checked])).toEqual([
      ['CPU cores', '128 (now 64)', false],
      ['Memory', '512 GiB (now 256 GiB)', false],
    ])
  })

  it('formats memory for people', () => {
    expect([formatMemory(512), formatMemory(1024), formatMemory(1536), formatMemory(16383), formatMemory(524288)]).toEqual([
      '512 MiB',
      '1 GiB',
      '1.5 GiB',
      '16 GiB',
      '512 GiB',
    ])
  })
})
