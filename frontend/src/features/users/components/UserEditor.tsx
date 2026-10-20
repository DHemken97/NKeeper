import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { errorFor, toFormErrors } from '../../../lib/formErrors'
import { DeleteButton } from '../../inventory/components/DeleteButton'
import panel from '../../inventory/components/HostPanel.module.css'
import type { Host } from '../../inventory/types'
import { usersApi } from '../api/usersApi'
import { toGrantDraft, type GrantDraft } from '../grantDraft'
import type { UserDetails, UserRole } from '../types'
import { GrantsEditor } from './GrantsEditor'
import { ResetPasswordDialog } from './ResetPasswordDialog'
import { RoleSelect } from './RoleSelect'

interface UserEditorProps {
  user: UserDetails
  hosts: Host[]
  /** The signed-in administrator; they can't demote, disable or delete themselves. */
  currentUserId: string
  onChanged: (user: UserDetails) => void
  onDeleted: () => void
}

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

/** One user's account, password and host access. Mount with `key={user.id}` to reset drafts. */
export function UserEditor({ user, hosts, currentUserId, onChanged, onDeleted }: UserEditorProps) {
  const isSelf = user.id === currentUserId
  const [email, setEmail] = useState(user.email ?? '')
  const [role, setRole] = useState<UserRole>(user.role)
  const [disabled, setDisabled] = useState(user.isDisabled)
  const [emailTwoFactor, setEmailTwoFactor] = useState(user.emailTwoFactor ?? false)
  const [grants, setGrants] = useState<GrantDraft[]>(() => user.grants.map(toGrantDraft))
  const [accountErrors, setAccountErrors] = useState<Record<string, string>>({})
  const [grantErrors, setGrantErrors] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<{ kind: 'info' | 'error'; text: string } | null>(null)
  const [savingAccount, setSavingAccount] = useState(false)
  const [savingAccess, setSavingAccess] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [resetting, setResetting] = useState(false)

  async function saveAccount(event: FormEvent) {
    event.preventDefault()
    setMessage(null)
    setAccountErrors({})
    setSavingAccount(true)
    try {
      const updated = await usersApi.update(user.id, { email: email.trim(), role, isDisabled: disabled, emailTwoFactor })
      onChanged(updated)
      const signedOut = role !== user.role || (disabled && !user.isDisabled)
      setMessage({ kind: 'info', text: signedOut ? 'Saved. The user was signed out everywhere so the change applies.' : 'Saved.' })
    } catch (error) {
      const { fields, message: text } = toFormErrors(error, ['email', 'role', 'isDisabled', 'emailTwoFactor'])
      setAccountErrors(fields)
      if (text || fields.isDisabled || fields.emailTwoFactor) {
        setMessage({ kind: 'error', text: text ?? fields.isDisabled ?? fields.emailTwoFactor })
      }
    } finally {
      setSavingAccount(false)
    }
  }

  async function saveAccess() {
    setMessage(null)
    setGrantErrors({})
    const incomplete = grants.findIndex((g) => (g.tagName === null ? !g.hostId : !g.tagName.trim()))
    if (incomplete !== -1) {
      setMessage({ kind: 'error', text: `Grant ${incomplete + 1} needs a host or a tag name.` })
      return
    }
    setSavingAccess(true)
    try {
      const updated = await usersApi.setGrants(user.id, grants)
      onChanged(updated)
      setGrants(updated.grants.map(toGrantDraft))
      setMessage({ kind: 'info', text: 'Access saved. It applies to the user’s next action.' })
    } catch (error) {
      const { fields, message: text } = toFormErrors(error, ['grants'])
      setGrantErrors(fields)
      setMessage({ kind: 'error', text: text ?? errorFor(fields, 'grants') ?? 'Couldn’t save access.' })
    } finally {
      setSavingAccess(false)
    }
  }

  async function handleDelete() {
    setDeleting(true)
    try {
      await usersApi.remove(user.id)
      onDeleted()
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) {
        onDeleted()
        return
      }
      setMessage({ kind: 'error', text: toFormErrors(error, ['id']).fields.id ?? toFormErrors(error).message ?? 'Couldn’t delete.' })
      setDeleting(false)
    }
  }

  return (
    <article className={panel.panel} aria-labelledby="user-title">
      <header className={panel.header}>
        <div className={panel.heading}>
          <h1 id="user-title" className={panel.title}>
            {user.userName}
          </h1>
          <p className={panel.subtitle}>
            {user.role === 'Administrator' ? 'Administrator' : 'User'}
            {user.isDirectoryAccount ? ' · Active Directory' : ''}
            {user.isSingleSignOnAccount ? ' · single sign-on' : ''} · created {dateFormat.format(new Date(user.createdAtUtc))}
            {user.isDisabled ? ' · disabled' : ''}
            {user.isLockedOut ? ' · locked out after failed sign-ins' : ''}
            {user.mustChangePassword ? ' · must change password' : ''}
            {user.emailTwoFactor ? ' · email sign-in code' : ''}
          </p>
        </div>
        <div className={panel.actions}>
          {isSelf ? <Badge variant="accent">You</Badge> : <DeleteButton question={`Delete ${user.userName}?`} onConfirm={handleDelete} loading={deleting} />}
        </div>
      </header>

      <div className={panel.body}>
        {message ? <Alert variant={message.kind}>{message.text}</Alert> : null}

        <form className={panel.section} onSubmit={saveAccount} noValidate aria-labelledby="account-title">
          <h2 id="account-title" className={panel.sectionTitle}>
            Account
          </h2>
          <TextField label="Email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} error={accountErrors.email} maxLength={256} />
          <RoleSelect value={role} onChange={setRole} error={accountErrors.role} disabled={isSelf} />
          <Checkbox
            label="Disable this account (blocks sign-in and ends all sessions)"
            checked={disabled}
            onChange={(e) => setDisabled(e.target.checked)}
            disabled={isSelf}
          />
          <Checkbox
            label="Email 2FA challenge code (a 9-digit code is emailed at every sign-in)"
            checked={emailTwoFactor}
            onChange={(e) => setEmailTwoFactor(e.target.checked)}
          />
          {isSelf ? <p className={panel.muted}>You can’t change your own role or disable yourself.</p> : null}
          <div>
            <Button type="submit" loading={savingAccount}>
              Save account
            </Button>
          </div>
        </form>

        <section className={panel.section} aria-labelledby="password-title">
          <h2 id="password-title" className={panel.sectionTitle}>
            Password
          </h2>
          {user.isDirectoryAccount ? (
            <p className={panel.muted}>
              {user.isLockedOut ? 'The account is locked after too many failed sign-ins and unlocks itself after 15 minutes. ' : null}
              This account signs in with Active Directory, so its password is managed there.
            </p>
          ) : user.isSingleSignOnAccount ? (
            <p className={panel.muted}>This account signs in with single sign-on, so it has no NKeeper password.</p>
          ) : (
            <>
              <p className={panel.muted}>
                {user.isLockedOut ? 'The account is locked after too many failed sign-ins. Resetting the password unlocks it. ' : null}
                Set a new password and give it to the user; they can be asked to change it at their next sign-in.
              </p>
              <div>
                <Button variant="secondary" onClick={() => setResetting(true)}>
                  Reset password…
                </Button>
              </div>
            </>
          )}
        </section>

        <section className={panel.section} aria-labelledby="access-title">
          <h2 id="access-title" className={panel.sectionTitle}>
            Host access
          </h2>
          {role === 'Administrator' ? (
            <p className={panel.muted}>Administrators can use and manage every host.</p>
          ) : (
            <>
              <p className={panel.muted}>
                Grant groups of hosts by tag (e.g. <code>site: Datacenter A</code>, or just <code>site</code> for any value) or individual hosts.
                Group grants include hosts tagged later.
              </p>
              <GrantsEditor value={grants} onChange={setGrants} hosts={hosts} />
              {grantErrors.grants ? <Alert>{grantErrors.grants}</Alert> : null}
              <div>
                <Button onClick={() => void saveAccess()} loading={savingAccess}>
                  Save access
                </Button>
              </div>
            </>
          )}
        </section>
      </div>

      {resetting ? (
        <ResetPasswordDialog
          userId={user.id}
          userName={user.userName}
          onClose={() => setResetting(false)}
          onDone={() => {
            setResetting(false)
            setMessage({ kind: 'info', text: `Password reset. ${user.userName} was signed out everywhere.` })
            void usersApi.get(user.id).then(onChanged, () => {})
          }}
        />
      ) : null}
    </article>
  )
}
