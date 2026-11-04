import { Button } from '../../../components/ui/Button'
import { useInventoryLimits } from '../limits'
import { Checkbox } from '../../../components/ui/Checkbox'
import { PasswordField } from '../../../components/ui/PasswordField'
import { TextArea } from '../../../components/ui/TextArea'
import { TextField } from '../../../components/ui/TextField'
import { newCredentialDraft, type CredentialDraft, type ServiceDraft } from '../drafts'
import { PROTOCOL_INFO } from '../types'
import styles from './CredentialsEditor.module.css'

interface CredentialsEditorProps {
  value: CredentialDraft[]
  onChange: (credentials: CredentialDraft[]) => void
  /** Field errors from the server, keyed like "credentials[0].username". */
  errors: Record<string, string>
  /** The host's services, to choose which each credential is used with. */
  services?: ServiceDraft[]
}

/** A service as the "Use with" list names it, e.g. "MS SQL · SQLEXPRESS :14330". */
function serviceName(service: ServiceDraft): string {
  return [PROTOCOL_INFO[service.protocol].label, service.protocol === 'Ssms' || service.protocol === 'Oracle' ? service.instance.trim() : '', service.label.trim()]
    .filter(Boolean)
    .join(' · ') + (service.port ? ` :${service.port}` : '')
}


/**
 * The host's own credentials. Stored secrets are never sent to the browser: an empty
 * secret field keeps what is stored, and "Remove" clears it.
 */
export function CredentialsEditor({ value, onChange, errors, services = [] }: CredentialsEditorProps) {
  const limits = useInventoryLimits()
  function update(key: number, patch: Partial<CredentialDraft>) {
    onChange(value.map((c) => (c.key === key ? { ...c, ...patch } : c)))
  }

  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.legend}>Credentials</legend>
      <p className={styles.hint}>Accounts for signing in to this host. Secrets are encrypted at rest and never shown again after saving.</p>

      {value.length === 0 ? <p className={styles.hint}>No credentials yet.</p> : null}

      <ol className={styles.list}>
        {value.map((credential, index) => {
          const field = (name: string) => errors[`credentials[${index}].${name}`]
          const rowError = field('id')
          const title = credential.label.trim() || credential.username.trim() || `Credential ${index + 1}`
          return (
            <li key={credential.key} className={styles.card} aria-label={title}>
              <div className={styles.cardHeader}>
                <span className={styles.cardTitle}>{title}</span>
                <Button variant="ghost" size="sm" onClick={() => onChange(value.filter((c) => c.key !== credential.key))}>
                  Remove
                </Button>
              </div>
              {rowError ? <p className={styles.error}>{rowError}</p> : null}

              <div className={styles.grid}>
                <TextField
                  label="Username"
                  value={credential.username}
                  onChange={(e) => update(credential.key, { username: e.target.value })}
                  error={field('username')}
                  maxLength={256}
                  autoComplete="off"
                  spellCheck={false}
                />
                <TextField
                  label="Domain"
                  value={credential.domain}
                  onChange={(e) => update(credential.key, { domain: e.target.value })}
                  error={field('domain')}
                  hint="Optional"
                  maxLength={256}
                  autoComplete="off"
                  spellCheck={false}
                />
                <TextField
                  label="Label"
                  value={credential.label}
                  onChange={(e) => update(credential.key, { label: e.target.value })}
                  error={field('label')}
                  hint="Optional, e.g. “Local admin”"
                  maxLength={100}
                  autoComplete="off"
                />
                <div className={styles.secret}>
                  <PasswordField
                    label="Password"
                    value={credential.password}
                    onChange={(e) => update(credential.key, { password: e.target.value })}
                    error={field('password')}
                    hint={credential.hasPassword && !credential.clearPassword ? 'Stored. Leave empty to keep it.' : undefined}
                    disabled={credential.clearPassword}
                    maxLength={1024}
                    autoComplete="new-password"
                  />
                  {credential.hasPassword ? (
                    <Checkbox
                      label="Remove stored password"
                      checked={credential.clearPassword}
                      onChange={(e) => update(credential.key, { clearPassword: e.target.checked, password: '' })}
                    />
                  ) : null}
                </div>
                <div className={`${styles.secret} ${styles.full}`}>
                  <TextArea
                    label="SSH private key"
                    value={credential.privateKey}
                    onChange={(e) => update(credential.key, { privateKey: e.target.value })}
                    error={field('privateKey')}
                    hint={credential.hasPrivateKey && !credential.clearPrivateKey ? 'Stored. Leave empty to keep it.' : 'Optional. PEM or OpenSSH format.'}
                    disabled={credential.clearPrivateKey}
                    placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
                    spellCheck={false}
                    rows={2}
                  />
                  {credential.hasPrivateKey ? (
                    <Checkbox
                      label="Remove stored key"
                      checked={credential.clearPrivateKey}
                      onChange={(e) => update(credential.key, { clearPrivateKey: e.target.checked, privateKey: '' })}
                    />
                  ) : null}
                </div>
                <fieldset className={`${styles.full} ${styles.useWith}`}>
                  <legend className={styles.useWithLegend}>Use with</legend>
                  <Checkbox
                    label="All services"
                    checked={credential.serviceKeys === null}
                    // Choosing services starts from all of them ticked: untick the ones it isn't for.
                    onChange={(e) => update(credential.key, { serviceKeys: e.target.checked ? null : services.map((s) => s.key) })}
                  />
                  {credential.serviceKeys !== null ? (
                    services.length === 0 ? (
                      <p className={styles.hint}>Add services above, then choose which this credential is for.</p>
                    ) : (
                      <div className={styles.serviceChoices}>
                        {services.map((service) => (
                          <Checkbox
                            key={service.key}
                            label={serviceName(service)}
                            checked={credential.serviceKeys!.includes(service.key)}
                            onChange={(e) =>
                              update(credential.key, {
                                serviceKeys: e.target.checked
                                  ? [...credential.serviceKeys!, service.key]
                                  : credential.serviceKeys!.filter((k) => k !== service.key),
                              })
                            }
                          />
                        ))}
                      </div>
                    )
                  ) : null}
                  {credential.serviceKeys !== null && credential.serviceKeys.length === 0 && services.length > 0 ? (
                    <p className={styles.hint}>Not used with any service: it isn’t offered anywhere.</p>
                  ) : null}
                  {field('serviceIndexes') ? <p className={styles.error}>{field('serviceIndexes')}</p> : null}
                </fieldset>
                <TextArea
                  className={styles.full}
                  label="Notes"
                  value={credential.notes}
                  onChange={(e) => update(credential.key, { notes: e.target.value })}
                  error={field('notes')}
                  maxLength={2000}
                  rows={1}
                />
              </div>
            </li>
          )
        })}
      </ol>

      {errors.credentials ? <p className={styles.error}>{errors.credentials}</p> : null}

      <div>
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onChange([...value, newCredentialDraft()])}
          disabled={value.length >= limits.maxCredentialsPerHost}
        >
          Add credential
        </Button>
      </div>
    </fieldset>
  )
}
