import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { PasswordField } from '../../../components/ui/PasswordField'
import { Spinner } from '../../../components/ui/Spinner'
import { TextField } from '../../../components/ui/TextField'
import field from '../../../components/ui/TextField.module.css'
import { toFormErrors } from '../../../lib/formErrors'
import { useResource } from '../../inventory/hooks/useResource'
import styles from '../../transfer/pages/TransferPage.module.css'
import own from './SettingsPage.module.css'
import { PluginBoundary } from '../../../sdk/PluginSessionView'
import { usePlugins } from '../../../sdk/registry'
import { PluginsPanel } from '../components/PluginsPanel'
import { settingsApi, SMTP_SECURITY, type EmailSettings, type EmailSettingsInput, type SmtpSecurity } from '../settingsApi'

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** Instance-wide settings (administrators): the SMTP server NKeeper sends email through, the plugins, and the plugins' own settings. */
export function SettingsPage() {
  const email = useResource((signal) => settingsApi.getEmail(signal))
  const plugins = usePlugins()

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <h1 className={styles.title}>Settings</h1>
        <p className={styles.lead}>Settings for the whole NKeeper instance. Only administrators see this page.</p>
      </header>

      {email.error && !email.data ? <Alert>{toFormErrors(email.error).message}</Alert> : null}
      {!email.data && !email.error ? <Spinner label="Loading" /> : null}
      {email.data ? <EmailPanel key={email.data.updatedAtUtc ?? 'new'} saved={email.data} onSaved={email.reload} /> : null}

      {plugins.flatMap((plugin) =>
        (plugin.settingsSections ?? []).map((Section, index) => (
          <PluginBoundary key={`${plugin.id}-${index}`} plugin={plugin.id}>
            <Section />
          </PluginBoundary>
        )),
      )}

      <PluginsPanel />
    </div>
  )
}

const FIELDS = ['enabled', 'host', 'port', 'security', 'userName', 'password', 'fromAddress', 'fromName']

function EmailPanel({ saved, onSaved }: { saved: EmailSettings; onSaved: () => void }) {
  const [enabled, setEnabled] = useState(saved.enabled)
  const [host, setHost] = useState(saved.host)
  const [port, setPort] = useState(String(saved.port))
  const [security, setSecurity] = useState<SmtpSecurity>(saved.security)
  const [userName, setUserName] = useState(saved.userName ?? '')
  const [password, setPassword] = useState('')
  const [clearPassword, setClearPassword] = useState(false)
  const [fromAddress, setFromAddress] = useState(saved.fromAddress)
  const [fromName, setFromName] = useState(saved.fromName ?? '')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<{ kind: 'info' | 'error'; text: string } | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | null>(null)

  function input(): EmailSettingsInput {
    return {
      enabled,
      host: host.trim(),
      port: Number(port),
      security,
      userName: userName.trim() || null,
      password: password || null,
      clearPassword: clearPassword && !password,
      fromAddress: fromAddress.trim(),
      fromName: fromName.trim() || null,
    }
  }

  async function run(kind: 'save' | 'test') {
    setMessage(null)
    setErrors({})
    setBusy(kind)
    try {
      if (kind === 'save') {
        await settingsApi.saveEmail(input())
        // Remounts with the saved values (the key is the update time).
        onSaved()
      } else {
        const { sentTo } = await settingsApi.testEmail(input())
        setMessage({ kind: 'info', text: `Test email sent to ${sentTo}. Check that it arrived.` })
      }
    } catch (error) {
      const { fields, message: text } = toFormErrors(error, FIELDS)
      setErrors(fields)
      setMessage({ kind: 'error', text: text ?? fields.enabled ?? (kind === 'test' ? fields.host : null) ?? 'Please check the highlighted fields.' })
    } finally {
      setBusy(null)
    }
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    void run('save')
  }

  return (
    <section className={styles.panel} aria-labelledby="email-title">
      <div>
        <h2 id="email-title" className={styles.panelTitle}>
          Email
        </h2>
        <p className={styles.hint}>
          The SMTP server NKeeper sends email through, such as the sign-in codes of users with “Email 2FA challenge code”.
          {saved.updatedAtUtc ? ` Last saved ${dateTimeFormat.format(new Date(saved.updatedAtUtc))}.` : ' Not set up yet.'}
        </p>
      </div>
      {message ? <Alert variant={message.kind}>{message.text}</Alert> : null}
      <form className={styles.form} onSubmit={handleSubmit} noValidate>
        <Checkbox label="Send email" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <TextField label="SMTP server" value={host} onChange={(e) => setHost(e.target.value)} error={errors.host} placeholder="smtp.example.com" maxLength={253} />
        <div className={field.field}>
          <label htmlFor="smtp-security" className={field.label}>
            Encryption
          </label>
          <select
            id="smtp-security"
            className={field.input}
            value={security}
            onChange={(e) => {
              const next = e.target.value as SmtpSecurity
              // Follow the usual port unless one was typed in.
              if (Number(port) === SMTP_SECURITY[security].port) {
                setPort(String(SMTP_SECURITY[next].port))
              }
              setSecurity(next)
            }}
          >
            {(Object.keys(SMTP_SECURITY) as SmtpSecurity[]).map((value) => (
              <option key={value} value={value}>
                {SMTP_SECURITY[value].label}
              </option>
            ))}
          </select>
        </div>
        <TextField label="Port" type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} error={errors.port} />
        <TextField
          label="Username"
          value={userName}
          onChange={(e) => setUserName(e.target.value)}
          error={errors.userName}
          hint="Leave empty if the server takes mail without signing in."
          autoComplete="off"
          maxLength={256}
        />
        <PasswordField
          label="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={errors.password}
          hint={saved.hasPassword ? 'A password is saved. Leave this empty to keep it.' : undefined}
          autoComplete="new-password"
          maxLength={512}
        />
        {saved.hasPassword ? (
          <Checkbox label="Forget the saved password" checked={clearPassword} disabled={password.length > 0} onChange={(e) => setClearPassword(e.target.checked)} />
        ) : null}
        <TextField
          label="From address"
          type="email"
          value={fromAddress}
          onChange={(e) => setFromAddress(e.target.value)}
          error={errors.fromAddress}
          placeholder="nkeeper@example.com"
          maxLength={256}
        />
        <TextField label="From name" value={fromName} onChange={(e) => setFromName(e.target.value)} error={errors.fromName} placeholder="NKeeper" maxLength={128} />
        <div className={own.actions}>
          <Button type="submit" loading={busy === 'save'} disabled={busy !== null}>
            Save
          </Button>
          <Button variant="secondary" loading={busy === 'test'} disabled={busy !== null} onClick={() => void run('test')}>
            Send test email
          </Button>
        </div>
      </form>
    </section>
  )
}
