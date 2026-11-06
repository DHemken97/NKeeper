import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest'
import { accountApi } from '../../account/api/accountApi'
import { AuthContext, type AuthContextValue, useAuth } from '../../auth/context'
import type { ColorSchemePreferences } from '../colorSchemes'
import { useState } from 'react'
import { ColorSchemeDialog } from './ColorSchemeDialog'

const OCEAN = { id: 'custom-ocean', name: 'Ocean', background: '#001122', surface: '#112233', text: '#eeffff', accent: '#0088cc' }

/** Holds the choices the way AuthProvider does, without a session behind it. */
function Harness({ initial }: { initial: ColorSchemePreferences }) {
  const [preferences, setPreferences] = useState(initial)
  const value = {
    user: { id: '1', userName: 'alice', email: null, roles: [], mustChangePassword: false, isDirectoryAccount: false, signInAccount: null, colorScheme: preferences.active, customColorSchemes: preferences.custom },
    colorSchemesChanged: setPreferences,
  } as unknown as AuthContextValue
  return (
    <AuthContext.Provider value={value}>
      <ColorSchemeDialog onClose={() => undefined} />
      <ActiveScheme />
    </AuthContext.Provider>
  )
}

function ActiveScheme() {
  return <output data-testid="active">{useAuth().user?.colorScheme}</output>
}

describe('ColorSchemeDialog', () => {
  let saved: MockInstance<typeof accountApi.updateColorSchemes>

  beforeEach(() => {
    saved = vi.spyOn(accountApi, 'updateColorSchemes').mockImplementation((preferences) => Promise.resolve(preferences))
  })

  afterEach(() => vi.restoreAllMocks())

  it('applies and saves a preset as soon as it is chosen', async () => {
    const user = userEvent.setup()
    render(<Harness initial={{ active: 'navy', custom: [] }} />)
    expect(screen.getByRole('radio', { name: 'Navy' })).toBeChecked()

    await user.click(screen.getByRole('radio', { name: 'Light' }))

    expect(screen.getByRole('radio', { name: 'Light' })).toBeChecked()
    expect(screen.getByTestId('active')).toHaveTextContent('light')
    await waitFor(() => expect(saved).toHaveBeenCalledWith({ active: 'light', custom: [] }))
  })

  it('builds a new scheme from the current colors, then saves and uses it', async () => {
    const user = userEvent.setup()
    render(<Harness initial={{ active: 'light', custom: [] }} />)

    await user.click(screen.getByRole('button', { name: 'New color scheme' }))
    expect(screen.getByLabelText('Background hex color')).toHaveValue('#f3f5f9')

    await user.type(screen.getByLabelText('Name'), 'Mine')
    const accent = screen.getByLabelText('Accent hex color')
    await user.clear(accent)
    await user.type(accent, '#AA0055')
    await user.click(screen.getByRole('button', { name: 'Save and use' }))

    await waitFor(() => expect(saved).toHaveBeenCalledTimes(1))
    const sent = saved.mock.calls[0][0]
    expect(sent.custom).toEqual([{ id: sent.active, name: 'Mine', background: '#f3f5f9', surface: '#ffffff', text: '#1a2233', accent: '#aa0055' }])
    expect(screen.getByRole('radio', { name: 'Mine' })).toBeChecked()
  })

  it('needs a name, and refuses colors the text cannot be read on', async () => {
    const user = userEvent.setup()
    render(<Harness initial={{ active: 'navy', custom: [] }} />)
    await user.click(screen.getByRole('button', { name: 'New color scheme' }))

    await user.click(screen.getByRole('button', { name: 'Save and use' }))
    expect(screen.getByText('Give the color scheme a name.')).toBeInTheDocument()

    const text = screen.getByLabelText('Text hex color')
    await user.clear(text)
    await user.type(text, '#111a2e')
    expect(screen.getByRole('alert')).toHaveTextContent(/too close/)
    expect(screen.getByRole('button', { name: 'Save and use' })).toBeDisabled()
    expect(saved).not.toHaveBeenCalled()
  })

  it('edits a scheme without touching the others', async () => {
    const user = userEvent.setup()
    const other = { ...OCEAN, id: 'custom-other', name: 'Other' }
    render(<Harness initial={{ active: 'navy', custom: [OCEAN, other] }} />)

    await user.click(screen.getByRole('button', { name: 'Edit Ocean' }))
    const name = screen.getByLabelText('Name')
    await user.clear(name)
    await user.type(name, 'Deep ocean')
    await user.click(screen.getByRole('button', { name: 'Save and use' }))

    await waitFor(() => expect(saved).toHaveBeenCalledWith({ active: OCEAN.id, custom: [{ ...OCEAN, name: 'Deep ocean' }, other] }))
  })

  it('goes back to the default when the scheme in use is deleted', async () => {
    const user = userEvent.setup()
    render(<Harness initial={{ active: OCEAN.id, custom: [OCEAN] }} />)

    await user.click(screen.getByRole('button', { name: 'Delete' }))
    await user.click(within(screen.getByRole('group', { name: 'Confirm delete' })).getByRole('button', { name: 'Yes, delete' }))

    await waitFor(() => expect(saved).toHaveBeenCalledWith({ active: 'navy', custom: [] }))
    expect(screen.getByRole('radio', { name: 'Navy' })).toBeChecked()
  })

  it('says so when the choice could not be saved', async () => {
    saved.mockRejectedValueOnce(new Error('offline'))
    const user = userEvent.setup()
    render(<Harness initial={{ active: 'navy', custom: [] }} />)

    await user.click(screen.getByRole('radio', { name: 'Forest' }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })
})
