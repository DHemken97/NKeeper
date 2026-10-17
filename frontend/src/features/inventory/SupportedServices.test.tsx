import { render, screen, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import { SUPPORTED_PROTOCOLS, type Host } from './types'

const admin = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'], mustChangePassword: false }

const switch01: Host = {
  id: 'h-switch01',
  name: 'switch01',
  hostname: null,
  ipAddress: '10.0.0.2',
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
  tags: [],
  // FTP was stored by an older version (or an import).
  services: [
    { protocol: 'Ssh', port: null, label: null },
    { protocol: 'Ftp', port: null, label: null },
  ],
  credentials: [],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

function renderAt(path: string) {
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => jsonResponse(200, [switch01]),
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: [path] })} />
    </AuthProvider>,
  )
}

describe('service types', () => {
  it('offers only the types NKeeper can connect with', async () => {
    renderAt('/?host=h-switch01&edit=1')

    const type = await screen.findByLabelText('Type for service 1')
    expect(within(type).getAllByRole('option').map((o) => (o as HTMLOptionElement).value)).toEqual([...SUPPORTED_PROTOCOLS])
    // The stored FTP service isn't in the draft, and the editor says why.
    expect(screen.queryByLabelText('Type for service 2')).not.toBeInTheDocument()
    expect(screen.getByRole('note')).toHaveTextContent('NKeeper can’t connect with FTP, so that service is removed when you save. Cancel to keep it.')
  })

  it('marks stored types NKeeper can’t connect with in the details', async () => {
    renderAt('/?host=h-switch01')

    expect(await screen.findByText(/Not supported: removed when the host is next edited/)).toBeInTheDocument()
  })
})
