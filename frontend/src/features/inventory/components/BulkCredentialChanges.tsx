import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { PasswordField } from '../../../components/ui/PasswordField'
import { TextArea } from '../../../components/ui/TextArea'
import { credentialImpact, newCredentialChange, type CredentialAction, type CredentialChange } from '../bulkCredentials'
import type { Host } from '../types'
import listStyles from './ListEditor.module.css'
import styles from './HostPanel.module.css'

interface BulkCredentialChangesProps {
  value: CredentialChange[]
  onChange: (changes: CredentialChange[]) => void
  /** The hosts the changes would apply to, for the counts. */
  hosts: Host[]
}

/**
 * Credentials for many hosts at once: add one to each (hosts with that account keep theirs), change
 * the password or key of an account wherever it is, or remove it. Without a domain, an account
 * matches whatever its domain.
 */
export function BulkCredentialChanges({ value, onChange, hosts }: BulkCredentialChangesProps) {
  const update = (key: number, patch: Partial<CredentialChange>) => onChange(value.map((c) => (c.key === key ? { ...c, ...patch } : c)))

  return (
    <fieldset className={listStyles.fieldset}>
      <legend className={listStyles.legend}>Credentials</legend>
      <p className={listStyles.hint}>
        Add one to every host (hosts that already have the account keep theirs), change an account’s password or key, or remove it. Leave the domain
        empty to match any.
      </p>
      {value.length === 0 ? <p className={listStyles.empty}>No credential changes.</p> : null}
      <ol className={listStyles.list}>
        {value.map((change, index) => {
          const position = `credential change ${index + 1}`
          const effect = credentialImpact(change, hosts)
          return (
            <li key={change.key} className={styles.bulkRow}>
              <select
                className={listStyles.input}
                value={change.action}
                onChange={(event) => update(change.key, { action: event.target.value as CredentialAction })}
                aria-label={`Action for ${position}`}
              >
                <option value="add">Add</option>
                <option value="change">Change</option>
                <option value="remove">Remove</option>
              </select>
              <input
                className={listStyles.input}
                value={change.username}
                onChange={(event) => update(change.key, { username: event.target.value })}
                placeholder="Username"
                maxLength={256}
                aria-label={`Username for ${position}`}
                autoComplete="off"
                spellCheck={false}
              />
              <input
                className={listStyles.input}
                value={change.domain}
                onChange={(event) => update(change.key, { domain: event.target.value })}
                placeholder={change.action === 'add' ? 'Domain (optional)' : 'Any domain'}
                maxLength={256}
                aria-label={`Domain for ${position}`}
                autoComplete="off"
                spellCheck={false}
              />
              <button
                type="button"
                className={listStyles.iconButton}
                onClick={() => onChange(value.filter((c) => c.key !== change.key))}
                aria-label={`Remove ${position}`}
              >
                ×
              </button>
              {change.action === 'remove' ? null : (
                <div className={styles.bulkEffect}>
                  <PasswordField
                    label={change.action === 'change' ? 'New password' : 'Password'}
                    value={change.password}
                    onChange={(event) => update(change.key, { password: event.target.value })}
                    disabled={change.clearPassword}
                    maxLength={1024}
                    autoComplete="new-password"
                  />
                  {change.action === 'change' ? (
                    <Checkbox
                      label="Remove the stored password"
                      checked={change.clearPassword}
                      onChange={(event) => update(change.key, { clearPassword: event.target.checked, password: '' })}
                    />
                  ) : null}
                  <TextArea
                    label={change.action === 'change' ? 'New SSH private key' : 'SSH private key'}
                    value={change.privateKey}
                    onChange={(event) => update(change.key, { privateKey: event.target.value })}
                    hint={change.action === 'change' ? 'Leave empty to keep each host’s key.' : 'Optional. PEM or OpenSSH format.'}
                    placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
                    spellCheck={false}
                    rows={2}
                  />
                </div>
              )}
              {effect ? <p className={`${styles.fieldHint} ${styles.bulkEffect}`}>{effect}</p> : null}
            </li>
          )
        })}
      </ol>
      <div>
        <Button variant="secondary" size="sm" onClick={() => onChange([...value, newCredentialChange()])}>
          Add a credential change
        </Button>
      </div>
    </fieldset>
  )
}
