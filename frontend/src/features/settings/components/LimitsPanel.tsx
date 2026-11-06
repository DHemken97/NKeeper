import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { TextField } from '../../../components/ui/TextField'
import { toFormErrors } from '../../../lib/formErrors'
import { limitsApi, setInventoryLimits, type InventoryLimits } from '../../inventory/limits'
import styles from '../../transfer/pages/TransferPage.module.css'
import own from '../pages/SettingsPage.module.css'

const FIELDS = ['maxServicesPerHost', 'maxCredentialsPerHost', 'maxTagsPerHost', 'scanUsersPreselectedUpTo', 'zipReserveGigabytes']

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** Settings ▸ Limits: how much a host may hold, how many found users a scan ticks for you, and the disk space folder downloads leave free. */
export function LimitsPanel({ saved }: { saved: InventoryLimits }) {
  const [services, setServices] = useState(String(saved.maxServicesPerHost))
  const [credentials, setCredentials] = useState(String(saved.maxCredentialsPerHost))
  const [tags, setTags] = useState(String(saved.maxTagsPerHost))
  const [scanUsers, setScanUsers] = useState(String(saved.scanUsersPreselectedUpTo))
  const [zipReserve, setZipReserve] = useState(String(saved.zipReserveGigabytes))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<{ kind: 'info' | 'error'; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [savedAt, setSavedAt] = useState(saved.updatedAtUtc)

  async function save(event: FormEvent) {
    event.preventDefault()
    setMessage(null)
    setErrors({})
    setBusy(true)
    try {
      const next = await limitsApi.save({
        maxServicesPerHost: Number(services),
        maxCredentialsPerHost: Number(credentials),
        maxTagsPerHost: Number(tags),
        scanUsersPreselectedUpTo: Number(scanUsers),
        zipReserveGigabytes: Number(zipReserve),
      })
      setInventoryLimits(next)
      setSavedAt(next.updatedAtUtc)
      setMessage({ kind: 'info', text: 'Saved. They apply now.' })
    } catch (error) {
      const { fields, message: text } = toFormErrors(error, FIELDS)
      setErrors(fields)
      setMessage({ kind: 'error', text: text ?? 'Please check the highlighted fields.' })
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className={styles.panel} aria-labelledby="limits-title">
      <div>
        <h2 id="limits-title" className={styles.panelTitle}>
          Limits
        </h2>
        <p className={styles.hint}>
          How much one host may hold, how scans pre-select the users they find, and the disk space folder downloads leave free.
          {savedAt ? ` Last saved ${dateTimeFormat.format(new Date(savedAt))}.` : ' Using the defaults.'}
        </p>
      </div>
      {message ? <Alert variant={message.kind}>{message.text}</Alert> : null}
      <form className={styles.form} onSubmit={(event) => void save(event)} noValidate>
        <TextField
          label="Services per host"
          type="number"
          min={1}
          max={saved.ceilingServices}
          value={services}
          onChange={(e) => setServices(e.target.value)}
          error={errors.maxServicesPerHost}
          hint={`Up to ${saved.ceilingServices}.`}
        />
        <TextField
          label="Credentials per host"
          type="number"
          min={1}
          max={saved.ceilingCredentials}
          value={credentials}
          onChange={(e) => setCredentials(e.target.value)}
          error={errors.maxCredentialsPerHost}
          hint={`Up to ${saved.ceilingCredentials}.`}
        />
        <TextField
          label="Tags per host"
          type="number"
          min={1}
          max={saved.ceilingTags}
          value={tags}
          onChange={(e) => setTags(e.target.value)}
          error={errors.maxTagsPerHost}
          hint={`Up to ${saved.ceilingTags}.`}
        />
        <TextField
          label="Users a scan ticks for you"
          type="number"
          min={0}
          max={saved.ceilingScanUsers}
          value={scanUsers}
          onChange={(e) => setScanUsers(e.target.value)}
          error={errors.scanUsersPreselectedUpTo}
          hint="When a scan finds more new users than this, they all start unticked. 0: never ticked."
        />
        <TextField
          label="Disk space to keep free when zipping (GB)"
          type="number"
          min={0}
          max={saved.ceilingZipReserveGigabytes}
          value={zipReserve}
          onChange={(e) => setZipReserve(e.target.value)}
          error={errors.zipReserveGigabytes}
          hint="A folder download is refused if zipping it could leave the NKeeper server with less than this free. 0: it may use all the free space."
        />
        <div className={own.actions}>
          <Button type="submit" loading={busy} disabled={busy}>
            Save
          </Button>
        </div>
      </form>
    </section>
  )
}
