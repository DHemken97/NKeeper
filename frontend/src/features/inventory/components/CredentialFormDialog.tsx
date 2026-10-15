import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { PasswordField } from '../../../components/ui/PasswordField'
import { TextArea } from '../../../components/ui/TextArea'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { credentialsApi } from '../api/inventoryApi'
import type { Credential, CredentialInput } from '../types'
import { DeleteButton } from './DeleteButton'
import styles from './Form.module.css'

interface CredentialFormDialogProps {
  /** The credential to edit, or null to create one. */
  credential: Credential | null
  onClose: () => void
  onChanged: () => void
}

const FIELDS = ['name', 'username', 'domain', 'password', 'privateKey', 'notes']

/**
 * Secrets are write-only: the API only says whether a password or key is stored.
 * Leaving a secret field empty keeps the stored value; "Remove" clears it.
 */
export function CredentialFormDialog({ credential, onClose, onChanged }: CredentialFormDialogProps) {
  const isEdit = credential !== null
  const [name, setName] = useState(credential?.name ?? '')
  const [username, setUsername] = useState(credential?.username ?? '')
  const [domain, setDomain] = useState(credential?.domain ?? '')
  const [password, setPassword] = useState('')
  const [clearPassword, setClearPassword] = useState(false)
  const [privateKey, setPrivateKey] = useState('')
  const [clearPrivateKey, setClearPrivateKey] = useState(false)
  const [notes, setNotes] = useState(credential?.notes ?? '')
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [deleting, setDeleting] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setFieldErrors({})
    setFormError(null)
    if (!name.trim()) {
      setFieldErrors({ name: 'Enter a name.' })
      return
    }

    const input: CredentialInput = {
      name: name.trim(),
      username: username.trim() || null,
      domain: domain.trim() || null,
      notes: notes.trim() || null,
      version: credential?.version,
    }
    if (clearPassword) {
      input.clearPassword = true
    } else if (password) {
      input.password = password
    }
    if (clearPrivateKey) {
      input.clearPrivateKey = true
    } else if (privateKey.trim()) {
      input.privateKey = privateKey
    }

    setSaving(true)
    try {
      if (credential) {
        await credentialsApi.update(credential.id, input)
      } else {
        await credentialsApi.create(input)
      }
      onChanged()
      onClose()
    } catch (error) {
      const { fields, message } = toFormErrors(error, FIELDS)
      setFieldErrors(fields)
      setFormError(message)
      setSaving(false)
    }
  }

  async function handleDelete() {
    if (!credential) {
      return
    }
    setDeleting(true)
    setFormError(null)
    try {
      await credentialsApi.remove(credential.id)
      onChanged()
      onClose()
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) {
        onChanged()
        onClose()
        return
      }
      setFormError(toFormErrors(error).message)
      setDeleting(false)
    }
  }

  const formId = 'credential-form'
  const usedBy = credential?.hostCount ?? 0

  return (
    <Dialog
      title={isEdit ? 'Edit credential' : 'Add credential'}
      description={
        isEdit
          ? usedBy > 0
            ? `Used by ${usedBy} ${usedBy === 1 ? 'host' : 'hosts'}.`
            : 'Not attached to any hosts.'
          : 'Saved credentials can be attached to any number of hosts.'
      }
      onClose={onClose}
      footer={
        <>
          {isEdit ? (
            <DeleteButton
              question={usedBy > 0 ? `Detach from ${usedBy} ${usedBy === 1 ? 'host' : 'hosts'} and delete?` : 'Delete this credential?'}
              onConfirm={handleDelete}
              loading={deleting}
              disabled={saving}
            />
          ) : null}
          <Button variant="secondary" onClick={onClose} disabled={saving || deleting}>
            Cancel
          </Button>
          <Button type="submit" form={formId} loading={saving} disabled={deleting}>
            {isEdit ? 'Save changes' : 'Add credential'}
          </Button>
        </>
      }
    >
      <form id={formId} className={styles.form} onSubmit={handleSubmit} noValidate autoComplete="off">
        {formError ? <Alert>{formError}</Alert> : null}

        <div className={styles.grid}>
          <TextField
            className={styles.full}
            label="Name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            error={fieldErrors.name}
            hint="How this credential appears in lists, e.g. “Domain admin”."
            maxLength={100}
            data-autofocus
            required
          />
          <TextField
            label="Username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            error={fieldErrors.username}
            maxLength={256}
            autoComplete="off"
            spellCheck={false}
          />
          <TextField
            label="Domain"
            value={domain}
            onChange={(e) => setDomain(e.target.value)}
            error={fieldErrors.domain}
            hint="Optional, for Windows accounts."
            maxLength={256}
            autoComplete="off"
            spellCheck={false}
          />
        </div>

        <div className={styles.section}>
          <h3 className={styles.sectionTitle}>Secrets</h3>
          <PasswordField
            label="Password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            error={fieldErrors.password}
            hint={credential?.hasPassword ? 'A password is stored. Leave empty to keep it.' : undefined}
            disabled={clearPassword}
            maxLength={1024}
            autoComplete="new-password"
          />
          {credential?.hasPassword ? (
            <Checkbox label="Remove the stored password" checked={clearPassword} onChange={(e) => setClearPassword(e.target.checked)} />
          ) : null}

          <TextArea
            label="SSH private key"
            value={privateKey}
            onChange={(e) => setPrivateKey(e.target.value)}
            error={fieldErrors.privateKey}
            hint={credential?.hasPrivateKey ? 'A key is stored. Leave empty to keep it.' : 'Optional. Paste a PEM or OpenSSH private key.'}
            disabled={clearPrivateKey}
            placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
            spellCheck={false}
            rows={3}
          />
          {credential?.hasPrivateKey ? (
            <Checkbox label="Remove the stored key" checked={clearPrivateKey} onChange={(e) => setClearPrivateKey(e.target.checked)} />
          ) : null}
          <p className={styles.secretStatus}>Secrets are encrypted at rest and are never shown again after saving.</p>
        </div>

        <div className={styles.section}>
          <TextArea label="Notes" value={notes} onChange={(e) => setNotes(e.target.value)} error={fieldErrors.notes} maxLength={2000} rows={2} />
        </div>
      </form>
    </Dialog>
  )
}
