import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../../lib/api'
import type { LoginCredentials } from '../types'
import { LoginForm } from './LoginForm'

function renderForm(onSubmit: (credentials: LoginCredentials) => Promise<void> = vi.fn().mockResolvedValue(undefined)) {
  render(
    <MemoryRouter>
      <LoginForm onSubmit={onSubmit} />
    </MemoryRouter>,
  )
  return {
    user: userEvent.setup(),
    onSubmit,
    userName: screen.getByLabelText('Username or email'),
    password: screen.getByLabelText('Password'),
    submit: screen.getByRole('button', { name: 'Sign in' }),
  }
}

describe('LoginForm', () => {
  it('has labelled inputs with the right autocomplete hints', () => {
    const { userName, password } = renderForm()

    expect(userName).toHaveAttribute('autocomplete', 'username')
    expect(password).toHaveAttribute('autocomplete', 'current-password')
    expect(password).toHaveAttribute('type', 'password')
    expect(screen.getByRole('checkbox', { name: 'Remember me' })).not.toBeChecked()
    expect(screen.getByRole('link', { name: 'Forgot password?' })).toHaveAttribute('href', '/forgot-password')
  })

  it('shows accessible validation errors and does not submit an empty form', async () => {
    const { user, onSubmit, userName, password, submit } = renderForm()

    await user.click(submit)

    expect(onSubmit).not.toHaveBeenCalled()
    expect(userName).toHaveAttribute('aria-invalid', 'true')
    expect(userName).toHaveAccessibleDescription('Enter your username or email.')
    expect(password).toHaveAccessibleDescription('Enter your password.')
    expect(userName).toHaveFocus()
  })

  it('clears a field error once the user edits that field', async () => {
    const { user, userName, submit } = renderForm()

    await user.click(submit)
    await user.type(userName, 'admin')

    expect(userName).not.toHaveAttribute('aria-invalid')
    expect(screen.queryByText('Enter your username or email.')).not.toBeInTheDocument()
  })

  it('submits trimmed credentials including Remember me', async () => {
    const { user, onSubmit, userName, password, submit } = renderForm()

    await user.type(userName, '  admin@example.com ')
    await user.type(password, 'secret-password')
    await user.click(screen.getByRole('checkbox', { name: 'Remember me' }))
    await user.click(submit)

    expect(onSubmit).toHaveBeenCalledWith({
      userNameOrEmail: 'admin@example.com',
      password: 'secret-password',
      rememberMe: true,
    })
  })

  it('shows a loading state while the request is in flight', async () => {
    let finish: () => void = () => {}
    const pending = new Promise<void>((resolve) => (finish = resolve))
    const { user, userName, password, submit } = renderForm(() => pending)

    await user.type(userName, 'admin')
    await user.type(password, 'secret-password')
    await user.click(submit)

    const button = screen.getByRole('button', { name: /signing in/i })
    expect(button).toBeDisabled()
    expect(button).toHaveAttribute('aria-busy', 'true')
    finish()
  })

  it('announces invalid credentials, clears the password and focuses it', async () => {
    const onSubmit = vi.fn().mockRejectedValue(new ApiError(401, 'The username or password is incorrect.'))
    const { user, userName, password, submit } = renderForm(onSubmit)

    await user.type(userName, 'admin')
    await user.type(password, 'wrong-password')
    await user.click(submit)

    expect(await screen.findByRole('alert')).toHaveTextContent('Incorrect username or password.')
    expect(password).toHaveValue('')
    expect(password).toHaveFocus()
    expect(userName).toHaveValue('admin')
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled()
  })

  it('explains network failures', async () => {
    const onSubmit = vi.fn().mockRejectedValue(new ApiError(0, 'offline'))
    const { user, userName, password, submit } = renderForm(onSubmit)

    await user.type(userName, 'admin')
    await user.type(password, 'secret-password')
    await user.click(submit)

    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to reach the server')
    expect(password).toHaveValue('secret-password')
  })

  it('explains rate limiting with the retry delay', async () => {
    const onSubmit = vi.fn().mockRejectedValue(new ApiError(429, 'Too many', undefined, 42))
    const { user, userName, password, submit } = renderForm(onSubmit)

    await user.type(userName, 'admin')
    await user.type(password, 'secret-password')
    await user.click(submit)

    expect(await screen.findByRole('alert')).toHaveTextContent('Try again in 42 seconds.')
  })

  it('toggles password visibility', async () => {
    const { user, password } = renderForm()
    const toggle = screen.getByRole('button', { name: 'Show password' })

    await user.click(toggle)
    expect(password).toHaveAttribute('type', 'text')
    expect(screen.getByRole('button', { name: 'Hide password' })).toHaveAttribute('aria-pressed', 'true')

    await user.click(screen.getByRole('button', { name: 'Hide password' }))
    expect(password).toHaveAttribute('type', 'password')
  })
})
