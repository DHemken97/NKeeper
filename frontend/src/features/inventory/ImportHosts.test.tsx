import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '../../app/router'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../../test/http'
import { AuthProvider } from '../auth/components/AuthProvider'
import type { HostImportResult } from './types'

const adminUser = { id: 'u1', userName: 'admin', email: 'admin@example.com', roles: ['Administrator'] }
const xml = '<mrng:Connections xmlns:mrng="http://mremoteng.org" ConfVersion="2.6"/>'

const result = (overrides: Partial<HostImportResult>): HostImportResult => ({
  dryRun: true,
  added: [],
  updated: [],
  unchanged: [],
  deleted: [],
  skipped: [],
  warnings: [],
  ...overrides,
})

function renderWorkspace(importHandler: (body: Record<string, unknown>) => Response, user = adminUser) {
  const bodies: Record<string, unknown>[] = []
  let hostLoads = 0
  mockFetch({
    ...antiforgeryRoute,
    'GET /api/auth/me': () => jsonResponse(200, user),
    'GET /api/hosts': () => {
      hostLoads++
      return jsonResponse(200, [])
    },
    'POST /api/hosts/import': (_url, init) => {
      const body = JSON.parse(String(init.body)) as Record<string, unknown>
      bodies.push(body)
      return importHandler(body)
    },
  })
  render(
    <AuthProvider>
      <RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />
    </AuthProvider>,
  )
  return { bodies, hostLoads: () => hostLoads, user: userEvent.setup() }
}

async function openWithFile(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Import' }))
  const dialog = screen.getByRole('dialog', { name: 'Import from mRemoteNG' })
  await user.upload(within(dialog).getByLabelText('mRemoteNG file'), new File([xml], 'confCons.xml', { type: 'text/xml' }))
  return dialog
}

describe('importing from mRemoteNG', () => {
  it('previews first, then imports with the chosen actions and reloads the hosts', async () => {
    const { bodies, hostLoads, user } = renderWorkspace((body) =>
      jsonResponse(200, body.dryRun
        ? result({ added: ['web01'], updated: ['sql01'], skipped: [{ name: 'weird', reason: 'It has no hostname or IP address.' }] })
        : result({ dryRun: false, added: ['web01'], updated: ['sql01'] })),
    )

    const dialog = await openWithFile(user)
    expect(within(dialog).getByRole('checkbox', { name: /^Add/ })).toBeChecked()
    expect(within(dialog).getByRole('checkbox', { name: /^Update/ })).toBeChecked()
    expect(within(dialog).getByRole('checkbox', { name: /^Delete/ })).not.toBeChecked()
    await user.click(within(dialog).getByRole('button', { name: 'Preview' }))

    const preview = await within(dialog).findByRole('region', { name: 'Preview' })
    expect(within(preview).getByText('Will add')).toBeInTheDocument()
    expect(within(preview).getByText('Will update')).toBeInTheDocument()
    expect(within(preview).getByText('Skipped')).toBeInTheDocument()
    expect(bodies[0]).toEqual({ content: xml, add: true, update: true, delete: false, dryRun: true })

    const loadsBefore = hostLoads()
    await user.click(within(dialog).getByRole('button', { name: 'Import' }))

    expect(await screen.findByRole('dialog', { name: 'Import finished' })).toBeInTheDocument()
    expect(bodies[1]).toEqual({ content: xml, add: true, update: true, delete: false, dryRun: false })
    await waitFor(() => expect(hostLoads()).toBeGreaterThan(loadsBefore))
  })

  it('shows deletions before they happen, and a changed option needs a new preview', async () => {
    const { bodies, user } = renderWorkspace((body) => jsonResponse(200, result({ dryRun: Boolean(body.dryRun), deleted: body.delete ? ['old01', 'old02'] : [] })))

    const dialog = await openWithFile(user)
    await user.click(within(dialog).getByRole('checkbox', { name: /^Delete/ }))
    await user.click(within(dialog).getByRole('button', { name: 'Preview' }))

    expect(await within(dialog).findByText('old01')).toBeVisible()
    expect(within(dialog).getByRole('button', { name: 'Import and delete 2' })).toBeInTheDocument()

    await user.click(within(dialog).getByRole('checkbox', { name: /^Delete/ }))
    expect(within(dialog).queryByRole('region', { name: 'Preview' })).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Preview' })).toBeInTheDocument()
    expect(bodies).toHaveLength(1)
  })

  it('asks for the master password when the file is protected', async () => {
    const { bodies, user } = renderWorkspace((body) =>
      body.masterPassword === 'right'
        ? jsonResponse(200, result({ added: ['db01'] }))
        : jsonResponse(400, { title: 'Invalid', status: 400, errors: { MasterPassword: ['That master password doesn’t open this file.'] } }),
    )

    const dialog = await openWithFile(user)
    await user.click(within(dialog).getByRole('button', { name: 'Preview' }))
    expect(await within(dialog).findByText('That master password doesn’t open this file.')).toBeInTheDocument()

    await user.type(within(dialog).getByLabelText('Master password'), 'right')
    await user.click(within(dialog).getByRole('button', { name: 'Preview' }))
    expect(await within(dialog).findByText('Will add')).toBeInTheDocument()
    expect(bodies[1]).toMatchObject({ masterPassword: 'right' })
  })

  it('is only offered to administrators', async () => {
    renderWorkspace(() => jsonResponse(500), { ...adminUser, roles: [] })

    expect(await screen.findByText(/No hosts have been shared with you yet/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Import' })).not.toBeInTheDocument()
  })
})
