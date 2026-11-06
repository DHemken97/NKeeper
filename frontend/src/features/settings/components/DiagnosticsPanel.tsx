import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { TextField } from '../../../components/ui/TextField'
import { toFormErrors } from '../../../lib/formErrors'
import styles from '../../transfer/pages/TransferPage.module.css'
import own from '../pages/SettingsPage.module.css'
import { diagnosticsApi, type DiagnosticsStatus } from '../settingsApi'

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

function when(value: string) {
  return dateTimeFormat.format(new Date(value))
}

function size(bytes: number) {
  return bytes < 1024 * 1024 ? `${Math.ceil(bytes / 1024)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/** Settings ▸ Diagnostics: the file of errors and usage totals, its size limit, and downloading it. */
export function DiagnosticsPanel({ saved, onChanged }: { saved: DiagnosticsStatus; onChanged: () => void }) {
  const [enabled, setEnabled] = useState(saved.enabled)
  const [maxMegabytes, setMaxMegabytes] = useState(String(saved.maxMegabytes))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<{ kind: 'info' | 'error'; text: string } | null>(null)
  const [busy, setBusy] = useState<'save' | 'download' | null>(null)

  async function save(event: FormEvent) {
    event.preventDefault()
    setMessage(null)
    setErrors({})
    setBusy('save')
    try {
      await diagnosticsApi.save({ enabled, maxMegabytes: Number(maxMegabytes) })
      setMessage({ kind: 'info', text: 'Saved. It applies now.' })
      onChanged()
    } catch (error) {
      const { fields, message: text } = toFormErrors(error, ['enabled', 'maxMegabytes'])
      setErrors(fields)
      setMessage({ kind: 'error', text: text ?? 'Please check the highlighted fields.' })
    } finally {
      setBusy(null)
    }
  }

  async function download() {
    setMessage(null)
    setBusy('download')
    try {
      const url = URL.createObjectURL(await diagnosticsApi.download())
      const link = document.createElement('a')
      link.href = url
      link.download = `nkeeper-diagnostics-${new Date().toISOString().slice(0, 16).replace(/[-:]/g, '').replace('T', '-')}.zip`
      link.click()
      URL.revokeObjectURL(url)
      onChanged()
    } catch (error) {
      setMessage({ kind: 'error', text: toFormErrors(error).message ?? 'The download failed.' })
    } finally {
      setBusy(null)
    }
  }

  return (
    <section className={styles.panel} aria-labelledby="diagnostics-title">
      <div>
        <h2 id="diagnostics-title" className={styles.panelTitle}>
          Diagnostics
        </h2>
        <p className={styles.hint}>
          A file on this server of errors and usage totals, for finding bugs and seeing what gets used. Names and addresses are replaced with tokens, and it never holds stored
          passwords, recordings, commands or query text. Check it before sharing it: redaction can miss something. It stays on this server until you download it.
        </p>
      </div>

      {saved.pausedForDiskSpace ? <Alert>Writing is paused: this server is low on disk space (see Disk space to keep free under Limits). It resumes when there’s room.</Alert> : null}
      {saved.writeError ? <Alert>The file can’t be written in {saved.directory}: {saved.writeError}</Alert> : null}
      {saved.droppedUtc ? <Alert>Older data was removed to stay within the limit before it was downloaded ({when(saved.droppedUtc)}), so there’s a gap.</Alert> : null}
      {message ? <Alert variant={message.kind}>{message.text}</Alert> : null}

      <p className={styles.hint}>
        {saved.oldestUtc && saved.newestUtc ? `${size(saved.sizeBytes)} on disk, from ${when(saved.oldestUtc)} to ${when(saved.newestUtc)}.` : 'Nothing recorded yet.'}{' '}
        {saved.lastDownloadedUtc ? `Last downloaded ${when(saved.lastDownloadedUtc)}.` : 'Never downloaded.'} Kept in {saved.directory}.
      </p>

      <form className={styles.form} onSubmit={(event) => void save(event)} noValidate>
        <Checkbox label="Keep the diagnostics file" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <TextField
          label="Size limit (MB)"
          type="number"
          min={saved.minMegabytes}
          max={saved.ceilingMegabytes}
          value={maxMegabytes}
          onChange={(e) => setMaxMegabytes(e.target.value)}
          error={errors.maxMegabytes}
          hint={`${saved.minMegabytes} to ${saved.ceilingMegabytes}. At the limit, the oldest data goes first. Lowering it can remove data straight away.`}
        />
        <div className={own.actions}>
          <Button type="submit" loading={busy === 'save'} disabled={busy !== null}>
            Save
          </Button>
          <Button variant="secondary" loading={busy === 'download'} disabled={busy !== null || saved.sizeBytes === 0} onClick={() => void download()}>
            Download diagnostics
          </Button>
        </div>
      </form>
    </section>
  )
}
