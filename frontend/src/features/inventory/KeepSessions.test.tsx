import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { Host } from './types'

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
  zabbixHostId: null,
  tags: [],
  services: [{ protocol: 'Ssh', port: null, label: null }],
  credentials: [{ id: 'c-root', label: null, username: 'root', domain: null, notes: null, hasPassword: true, hasPrivateKey: false }],
  trustedKeys: [],
  createdAtUtc: '2026-10-01T00:00:00Z',
  updatedAtUtc: '2026-10-01T00:00:00Z',
  version: 1,
  canManage: true,
}

function renderApp(initialEntry = '/') {
  const counts = { hosts: 0, health: 0, storage: 0 }
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, admin),
    'GET /api/hosts': () => {
      counts.hosts++
      return jsonResponse(200, [app01])
    },
    'GET /api/health': () => {
      counts.health++
      return jsonResponse(200, [])
    },
    'POST /api/storage/status': () => {
      counts.storage++
      return jsonResponse(200, { status: 'ready', storage: { iscsi: null, multipath: null, drbd: null }, hostKey: null })
    },
  })
  const router = createMemoryRouter(routes, { initialEntries: [initialEntry] })
  render(
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>,
  )
  return { router, counts, user: userEvent.setup() }
}

async function openStorage(user: ReturnType<typeof userEvent.setup>) {
  fireEvent.contextMenu(await screen.findByRole('button', { name: /app01/ }))
  await user.hover(screen.getByRole('menuitem', { name: 'Connect' }))
  await user.hover(screen.getByRole('menuitem', { name: 'Tools' }))
  await user.hover(screen.getByRole('menuitem', { name: /^Storage/ }))
  await user.click(screen.getByRole('menuitem', { name: /^root/ }))
  await screen.findByText(/has none of iSCSI/)
}

const nav = (name: string) => within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name })

describe('sessions while on other pages', () => {
  it('keeps a session running on the other pages, and shows it again on the way back', async () => {
    const { user, counts } = renderApp()
    await openStorage(user)

    for (const page of ['Reports', 'Users', 'Settings']) {
      await user.click(nav(page))
      expect(screen.queryByRole('tabpanel', { name: 'app01 storage' })).not.toBeInTheDocument()
      expect(screen.queryByRole('complementary', { name: 'Host inventory' })).not.toBeInTheDocument()
    }

    const hostsBefore = counts.hosts
    await user.click(nav('Hosts'))

    expect(screen.getByRole('tabpanel', { name: 'app01 storage' })).toBeVisible()
    expect(screen.getByText(/has none of iSCSI/)).toBeVisible()
    // The same session, not a new connection; the host list is fetched again, as it may have changed.
    expect(counts.storage).toBe(1)
    await waitFor(() => expect(counts.hosts).toBe(hostsBefore + 1))
  })

  it('leaves the keyboard to the page that is showing', async () => {
    const { user } = renderApp()
    await openStorage(user)
    await user.click(nav('Reports'))

    await user.keyboard('{Alt>}{Shift>}w{/Shift}{/Alt}')
    await user.click(nav('Hosts'))

    expect(screen.getByRole('tabpanel', { name: 'app01 storage' })).toBeVisible()
  })

  it('comes back to the view it left', async () => {
    const { user, router } = renderApp('/?host=h-app01')
    await screen.findByRole('heading', { name: 'app01' })

    await user.click(nav('Reports'))
    expect(router.state.location.search).toBe('')
    await user.click(nav('Hosts'))

    expect(router.state.location.search).toBe('?host=h-app01')
    expect(screen.getByRole('heading', { name: 'app01' })).toBeVisible()
  })

  it('shows the host’s details when a report links to it, with the session still open', async () => {
    const { user, router } = renderApp()
    await openStorage(user)
    await user.click(nav('Reports'))

    await user.click(await screen.findByRole('link', { name: 'app01' }))

    expect(router.state.location.search).toBe('?host=h-app01')
    expect(screen.getByRole('tab', { name: /Details/ })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: /app01.*root/ })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'app01' })).toBeVisible()
  })

  it('doesn’t pull you back when a paused search finishes after you left', async () => {
    const { user, router } = renderApp()
    await user.type(await screen.findByRole('searchbox', { name: 'Search hosts' }), 'app')
    await user.click(nav('Reports'))
    const left = router.state.location.pathname

    await new Promise((resolve) => setTimeout(resolve, 1100))

    expect(left).not.toBe('/')
    expect(router.state.location.pathname).toBe(left)
    expect(router.state.location.search).toBe('')
  })

  it('doesn’t load the hosts workspace until it’s first opened', async () => {
    const { user, counts } = renderApp('/reports')
    await screen.findByRole('link', { name: 'app01' })
    expect(counts.health).toBe(0)

    await user.click(nav('Hosts'))

    await screen.findByRole('button', { name: /app01/ })
    await waitFor(() => expect(counts.health).toBe(1))
  })
})
