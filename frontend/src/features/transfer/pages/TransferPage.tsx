import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { PasswordField } from '../../../components/ui/PasswordField'
import { Spinner } from '../../../components/ui/Spinner'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { useAuth } from '../../auth/context'
import { useResource } from '../../inventory/hooks/useResource'
import { generatePassphrase, MIN_PASSPHRASE_LENGTH, transferApi, type ImportSummary } from '../transferApi'
import styles from './TransferPage.module.css'

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/**
 * Moving NKeeper to a new server (administrators): export everything into a file encrypted with a
 * passphrase, then import it on a fresh NKeeper. The file holds every stored password, so it is
 * only as safe as the passphrase, which travels separately.
 */
export function TransferPage() {
  const status = useResource((signal) => transferApi.status(signal))

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <h1 className={styles.title}>Move to a new server</h1>
        <p className={styles.lead}>
          Moves everything (users and their passwords, access, hosts, credentials and trusted keys) to a new NKeeper in one encrypted file. The new
          server re-encrypts the stored passwords with its own keys.
        </p>
      </header>

      {status.error && !status.data ? <Alert>{toFormErrors(status.error).message}</Alert> : null}
      {!status.data && !status.error ? <Spinner label="Loading" /> : null}

      {status.data ? (
        <>
          {status.data.locked ? <LockedNotice lockedBy={status.data.lockedBy} lockedAtUtc={status.data.lockedAtUtc} onUnlocked={status.reload} /> : null}
          <ExportPanel onExported={status.reload} />
          <ImportPanel canImport={status.data.canImport} blockedReason={status.data.importBlockedReason} />
        </>
      ) : null}
    </div>
  )
}

function LockedNotice({ lockedBy, lockedAtUtc, onUnlocked }: { lockedBy: string | null; lockedAtUtc: string | null; onUnlocked: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function unlock() {
    setBusy(true)
    setError(null)
    try {
      await transferApi.unlock()
      onUnlocked()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Couldn’t unlock.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className={`${styles.panel} ${styles.locked}`} aria-labelledby="locked-title">
      <h2 id="locked-title" className={styles.panelTitle}>
        This instance is locked
      </h2>
      <p>
        {lockedBy ?? 'An administrator'} exported its data{lockedAtUtc ? ` on ${dateTimeFormat.format(new Date(lockedAtUtc))}` : ''}, so hosts and users can’t
        be changed here: make changes on the new server. Connecting to hosts still works.
      </p>
      {error ? <Alert>{error}</Alert> : null}
      <div>
        <Button variant="secondary" size="sm" loading={busy} onClick={() => void unlock()}>
          Unlock (the move was called off)
        </Button>
      </div>
    </section>
  )
}

function ExportPanel({ onExported }: { onExported: () => void }) {
  const [password, setPassword] = useState('')
  const [custom, setCustom] = useState(false)
  const [generated, setGenerated] = useState(generatePassphrase)
  const [passphrase, setPassphrase] = useState('')
  const [lock, setLock] = useState(true)
  const [busy, setBusy] = useState(false)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)

  const chosen = custom ? passphrase : generated

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setErrors({})
    if (custom && passphrase.length < MIN_PASSPHRASE_LENGTH) {
      setErrors({ passphrase: `Use at least ${MIN_PASSPHRASE_LENGTH} characters.` })
      return
    }
    setBusy(true)
    try {
      const file = await transferApi.export(password, chosen, lock)
      const name = `nkeeper-${new Date().toISOString().slice(0, 10)}.nkeeper`
      const url = URL.createObjectURL(file)
      const link = document.createElement('a')
      link.href = url
      link.download = name
      link.click()
      URL.revokeObjectURL(url)
      setDone(name)
      setPassword('')
      onExported()
    } catch (err) {
      if (err instanceof ApiError && err.code === 'password_incorrect') {
        setErrors({ password: 'That isn’t your password.' })
      } else {
        const form = toFormErrors(err)
        setErrors(form.fields)
        setError(form.message)
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className={styles.panel} aria-labelledby="export-title">
      <h2 id="export-title" className={styles.panelTitle}>
        1. Export from this server
      </h2>
      <form className={styles.form} onSubmit={handleSubmit} aria-labelledby="export-title" noValidate>
        <PasswordField
          label="Your password"
          hint="To confirm it’s you"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={errors.password}
          autoComplete="current-password"
          required
        />

        <fieldset className={styles.fieldset}>
          <legend className={styles.legend}>Passphrase</legend>
          <p className={styles.hint}>
            Anyone with the file and the passphrase can read every stored password. Keep them apart: carry the passphrase separately (a password
            manager, or on paper), never next to the file.
          </p>
          <label className={styles.choice}>
            <input type="radio" name="passphrase-kind" checked={!custom} onChange={() => setCustom(false)} />
            Use this generated passphrase
          </label>
          {!custom ? (
            <div className={styles.generated}>
              <code className={styles.passphrase} aria-label="Generated passphrase">
                {generated}
              </code>
              <Button variant="ghost" size="sm" onClick={() => void navigator.clipboard?.writeText(generated)}>
                Copy
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setGenerated(generatePassphrase())}>
                New one
              </Button>
            </div>
          ) : null}
          <label className={styles.choice}>
            <input type="radio" name="passphrase-kind" checked={custom} onChange={() => setCustom(true)} />
            Choose my own
          </label>
          {custom ? (
            <PasswordField
              label="Passphrase"
              hint={`At least ${MIN_PASSPHRASE_LENGTH} characters`}
              value={passphrase}
              onChange={(e) => setPassphrase(e.target.value)}
              error={errors.passphrase}
              autoComplete="new-password"
            />
          ) : null}
        </fieldset>

        <Checkbox label="Lock this server after the export, so no changes are left behind" checked={lock} onChange={(e) => setLock(e.target.checked)} />

        {error ? <Alert>{error}</Alert> : null}
        {done ? (
          <Alert variant="info">
            Downloaded {done}. Copy it to the new server, then delete it from this computer once the import has worked.
          </Alert>
        ) : null}
        <div>
          <Button type="submit" loading={busy} disabled={!password}>
            Export and download
          </Button>
        </div>
      </form>
    </section>
  )
}

function ImportPanel({ canImport, blockedReason }: { canImport: boolean; blockedReason: string | null }) {
  const { refresh } = useAuth()
  const [file, setFile] = useState<File | null>(null)
  const [passphrase, setPassphrase] = useState('')
  const [summary, setSummary] = useState<ImportSummary | null>(null)
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  if (!canImport) {
    return (
      <section className={styles.panel} aria-labelledby="import-title">
        <h2 id="import-title" className={styles.panelTitle}>
          2. Import on the new server
        </h2>
        <p className={styles.hint}>
          {blockedReason} To import, install a new NKeeper, sign in with the administrator the installer created, and open this page there.
        </p>
      </section>
    )
  }

  async function run(action: 'preview' | 'import') {
    if (!file) {
      return
    }
    setBusy(true)
    setError(null)
    try {
      const result = action === 'preview' ? await transferApi.preview(file, passphrase) : await transferApi.import(file, passphrase)
      setSummary(result)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'The file couldn’t be read.')
    } finally {
      setBusy(false)
    }
  }

  if (summary?.imported) {
    return (
      <section className={styles.panel} aria-labelledby="import-title">
        <h2 id="import-title" className={styles.panelTitle}>
          Imported
        </h2>
        <Alert variant="info">
          {summary.users} users and {summary.hosts} hosts are on this server now. The account you signed in with was replaced: sign in with your account
          from the old server.
        </Alert>
        <div>
          <Button onClick={() => void refresh()}>Sign in</Button>
        </div>
      </section>
    )
  }

  return (
    <section className={styles.panel} aria-labelledby="import-title">
      <h2 id="import-title" className={styles.panelTitle}>
        2. Import on the new server
      </h2>
      <p className={styles.hint}>This server is new and empty, so it can receive an export.</p>
      <div className={styles.form}>
        <label className={styles.fileField}>
          <span>Export file</span>
          <input
            type="file"
            accept=".nkeeper"
            onChange={(e) => {
              setFile(e.target.files?.[0] ?? null)
              setSummary(null)
              setConfirmed(false)
            }}
          />
        </label>
        <PasswordField
          label="Passphrase"
          value={passphrase}
          onChange={(e) => {
            setPassphrase(e.target.value)
            setSummary(null)
          }}
          autoComplete="off"
        />
        {error ? <Alert>{error}</Alert> : null}
        {!summary ? (
          <div>
            <Button loading={busy} disabled={!file || !passphrase} onClick={() => void run('preview')}>
              Check the file
            </Button>
          </div>
        ) : (
          <>
            <dl className={styles.summary} aria-label="What the file holds">
              <dt>Exported</dt>
              <dd>
                by {summary.exportedBy}, {dateTimeFormat.format(new Date(summary.exportedAtUtc))} (NKeeper {summary.sourceVersion})
              </dd>
              <dt>Users</dt>
              <dd>
                {summary.users} ({summary.administrators} administrator{summary.administrators === 1 ? '' : 's'})
              </dd>
              <dt>Access grants</dt>
              <dd>{summary.grants}</dd>
              <dt>Hosts</dt>
              <dd>{summary.hosts}</dd>
              <dt>Credentials</dt>
              <dd>{summary.credentials}</dd>
              <dt>Trusted keys</dt>
              <dd>{summary.trustedKeys}</dd>
            </dl>
            {summary.warnings.map((warning) => (
              <Alert key={warning} variant="info">
                {warning}
              </Alert>
            ))}
            <Checkbox
              label="Replace this server’s data with the file’s. The administrator account I’m using is replaced too."
              checked={confirmed}
              onChange={(e) => setConfirmed(e.target.checked)}
            />
            <div>
              <Button variant="danger" loading={busy} disabled={!confirmed} onClick={() => void run('import')}>
                Import
              </Button>
            </div>
          </>
        )}
      </div>
    </section>
  )
}
