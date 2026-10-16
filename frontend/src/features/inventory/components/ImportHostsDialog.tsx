import { useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { PasswordField } from '../../../components/ui/PasswordField'
import { ApiError } from '../../../lib/api'
import { toFormErrors, type FormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../api/inventoryApi'
import type { HostImportResult } from '../types'
import styles from './ImportHostsDialog.module.css'

interface ImportHostsDialogProps {
  onClose: () => void
  /** Called after an import saved changes, so the host list can reload. */
  onImported: () => void
}

const FIELDS = ['content', 'masterPassword', 'add', 'delete'] as const
/** The server accepts files up to 20 MB (HostImportRequest.MaxContentLength). */
const MAX_FILE_BYTES = 20_000_000
const NO_ERRORS: FormErrors = { fields: {}, message: null }

/**
 * Imports an mRemoteNG connection file. Hosts match connections by name. A preview (a dry run on
 * the server) always comes first, so deletions are seen before anything is saved.
 */
export function ImportHostsDialog({ onClose, onImported }: ImportHostsDialogProps) {
  const [file, setFile] = useState<File | null>(null)
  const [masterPassword, setMasterPassword] = useState('')
  const [add, setAdd] = useState(true)
  const [update, setUpdate] = useState(true)
  const [remove, setRemove] = useState(false)
  const [preview, setPreview] = useState<HostImportResult | null>(null)
  const [done, setDone] = useState<HostImportResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [errors, setErrors] = useState<FormErrors>(NO_ERRORS)

  /** Any change to the inputs makes the preview stale. */
  function changed<T>(set: (value: T) => void) {
    return (value: T) => {
      set(value)
      setPreview(null)
      setErrors(NO_ERRORS)
    }
  }

  async function run(dryRun: boolean) {
    if (!file) {
      setErrors({ fields: { content: 'Choose an mRemoteNG file (confCons.xml or an XML export).' }, message: null })
      return
    }
    if (file.size > MAX_FILE_BYTES) {
      setErrors({ fields: { content: 'The file is too large (20 MB at most).' }, message: null })
      return
    }
    setBusy(true)
    setErrors(NO_ERRORS)
    try {
      const result = await hostsApi.importMRemoteNg({
        content: await file.text(),
        masterPassword: masterPassword || undefined,
        add,
        update,
        delete: remove,
        dryRun,
      })
      if (dryRun) {
        setPreview(result)
      } else {
        setDone(result)
        onImported()
      }
    } catch (error) {
      setErrors(
        error instanceof ApiError && error.status === 413
          ? {
              fields: {},
              message: 'The server refused the file as too large. A reverse proxy in front of NKeeper may limit uploads: allow up to 40 MB for /api/hosts/import.',
            }
          : toFormErrors(error, FIELDS),
      )
    } finally {
      setBusy(false)
    }
  }

  if (done) {
    return (
      <Dialog title="Import finished" onClose={onClose} footer={<Button onClick={onClose}>Done</Button>}>
        <ImportSummary result={done} />
      </Dialog>
    )
  }

  const nothingToDo = preview !== null && preview.added.length + preview.updated.length + preview.deleted.length === 0
  return (
    <Dialog
      title="Import from mRemoteNG"
      description="Hosts are matched to connections by name. Folders become folder tags."
      size="lg"
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          {preview ? (
            <Button variant={preview.deleted.length > 0 ? 'danger' : 'primary'} onClick={() => void run(false)} loading={busy} disabled={nothingToDo}>
              {preview.deleted.length > 0 ? `Import and delete ${preview.deleted.length}` : 'Import'}
            </Button>
          ) : (
            <Button onClick={() => void run(true)} loading={busy} disabled={!add && !update && !remove}>
              Preview
            </Button>
          )}
        </>
      }
    >
      <div className={styles.form}>
        {errors.message ? <Alert>{errors.message}</Alert> : null}

        <div className={styles.field}>
          <label htmlFor="import-file" className={styles.label}>
            mRemoteNG file
          </label>
          <input
            id="import-file"
            type="file"
            accept=".xml,text/xml,application/xml"
            className={styles.file}
            aria-invalid={errors.fields.content ? true : undefined}
            aria-describedby={errors.fields.content ? 'import-file-error' : 'import-file-hint'}
            onChange={(event) => changed(setFile)(event.target.files?.[0] ?? null)}
          />
          {errors.fields.content ? (
            <p id="import-file-error" className={styles.error}>
              {errors.fields.content}
            </p>
          ) : (
            <p id="import-file-hint" className={styles.hint}>
              confCons.xml, or File ▸ Export in mRemoteNG (XML).
            </p>
          )}
        </div>

        <PasswordField
          label="Master password"
          hint="Only if you set one in mRemoteNG."
          autoComplete="off"
          value={masterPassword}
          onChange={(event) => changed(setMasterPassword)(event.target.value)}
          error={errors.fields.masterPassword}
        />

        <fieldset className={styles.actions}>
          <legend className={styles.label}>What to do</legend>
          <Checkbox label="Add: hosts that aren't in NKeeper yet" checked={add} onChange={(event) => changed(setAdd)(event.target.checked)} />
          <Checkbox
            label="Update: hosts already in NKeeper, with the file's details"
            checked={update}
            onChange={(event) => changed(setUpdate)(event.target.checked)}
          />
          <Checkbox label="Delete: hosts that aren't in the file" checked={remove} onChange={(event) => changed(setRemove)(event.target.checked)} />
          {errors.fields.add || errors.fields.delete ? <p className={styles.error}>{errors.fields.add ?? errors.fields.delete}</p> : null}
        </fieldset>

        {preview ? (
          <section aria-label="Preview" className={styles.preview}>
            <h3 className={styles.previewTitle}>Preview: nothing has been saved yet</h3>
            {nothingToDo ? <Alert variant="info">Nothing would change.</Alert> : null}
            <ImportSummary result={preview} />
          </section>
        ) : null}
      </div>
    </Dialog>
  )
}

/** The import's outcome as counted, expandable lists. */
function ImportSummary({ result }: { result: HostImportResult }) {
  const tense = result.dryRun ? { add: 'Will add', update: 'Will update', remove: 'Will delete' } : { add: 'Added', update: 'Updated', remove: 'Deleted' }
  return (
    <div className={styles.summary}>
      <NameList title={tense.add} names={result.added} />
      <NameList title={tense.update} names={result.updated} />
      <NameList title={tense.remove} names={result.deleted} danger />
      <NameList title="Unchanged" names={result.unchanged} />
      {result.skipped.length > 0 ? (
        <details className={styles.group}>
          <summary>
            Skipped <span className={styles.count}>{result.skipped.length}</span>
          </summary>
          <ul className={styles.list}>
            {result.skipped.map((skip, index) => (
              <li key={index}>
                <strong>{skip.name}</strong>: {skip.reason}
              </li>
            ))}
          </ul>
        </details>
      ) : null}
      {result.warnings.length > 0 ? (
        <details className={styles.group}>
          <summary>
            Warnings <span className={styles.count}>{result.warnings.length}</span>
          </summary>
          <ul className={styles.list}>
            {result.warnings.map((warning, index) => (
              <li key={index}>{warning}</li>
            ))}
          </ul>
        </details>
      ) : null}
    </div>
  )
}

function NameList({ title, names, danger = false }: { title: string; names: string[]; danger?: boolean }) {
  if (names.length === 0) {
    return null
  }
  return (
    <details className={`${styles.group} ${danger ? styles.danger : ''}`} open={danger}>
      <summary>
        {title} <span className={styles.count}>{names.length}</span>
      </summary>
      <ul className={styles.list}>
        {names.map((name, index) => (
          <li key={index}>{name}</li>
        ))}
      </ul>
    </details>
  )
}
