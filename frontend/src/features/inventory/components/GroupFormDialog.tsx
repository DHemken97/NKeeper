import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { TextArea } from '../../../components/ui/TextArea'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { groupsApi } from '../api/inventoryApi'
import type { Group } from '../types'
import { DeleteButton } from './DeleteButton'
import styles from './Form.module.css'

interface GroupFormDialogProps {
  group: Group | null
  onClose: () => void
  onChanged: () => void
}

export function GroupFormDialog({ group, onClose, onChanged }: GroupFormDialogProps) {
  const [name, setName] = useState(group?.name ?? '')
  const [description, setDescription] = useState(group?.description ?? '')
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
    setSaving(true)
    try {
      const input = { name: name.trim(), description: description.trim() || null }
      if (group) {
        await groupsApi.update(group.id, input)
      } else {
        await groupsApi.create(input)
      }
      onChanged()
      onClose()
    } catch (error) {
      const { fields, message } = toFormErrors(error, ['name', 'description'])
      setFieldErrors(fields)
      setFormError(message)
      setSaving(false)
    }
  }

  async function handleDelete() {
    if (!group) {
      return
    }
    setDeleting(true)
    try {
      await groupsApi.remove(group.id)
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

  const formId = 'group-form'
  const hostCount = group?.hostCount ?? 0

  return (
    <Dialog
      title={group ? 'Edit group' : 'Add group'}
      description="Groups organize hosts, e.g. by site, customer or environment."
      onClose={onClose}
      footer={
        <>
          {group ? (
            <DeleteButton
              question={hostCount > 0 ? `Delete? Its ${hostCount} ${hostCount === 1 ? 'host becomes' : 'hosts become'} ungrouped.` : 'Delete this group?'}
              onConfirm={handleDelete}
              loading={deleting}
              disabled={saving}
            />
          ) : null}
          <Button variant="secondary" onClick={onClose} disabled={saving || deleting}>
            Cancel
          </Button>
          <Button type="submit" form={formId} loading={saving} disabled={deleting}>
            {group ? 'Save changes' : 'Add group'}
          </Button>
        </>
      }
    >
      <form id={formId} className={styles.form} onSubmit={handleSubmit} noValidate>
        {formError ? <Alert>{formError}</Alert> : null}
        <TextField
          label="Name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          error={fieldErrors.name}
          maxLength={100}
          autoComplete="off"
          data-autofocus
          required
        />
        <TextArea
          label="Description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          error={fieldErrors.description}
          maxLength={1000}
          rows={2}
        />
      </form>
    </Dialog>
  )
}
