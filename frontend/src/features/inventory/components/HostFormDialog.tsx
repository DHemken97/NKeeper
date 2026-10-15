import { useEffect, useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { Select } from '../../../components/ui/Select'
import { Spinner } from '../../../components/ui/Spinner'
import { TextArea } from '../../../components/ui/TextArea'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { errorFor, toFormErrors } from '../../../lib/formErrors'
import { credentialsApi, groupsApi, hostsApi, tagsApi } from '../api/inventoryApi'
import { useResource } from '../hooks/useResource'
import { newServiceDraft, type ServiceDraft } from '../serviceDraft'
import type { Host, HostInput } from '../types'
import { CredentialPicker } from './CredentialPicker'
import { DeleteButton } from './DeleteButton'
import styles from './Form.module.css'
import { ServicesEditor } from './ServicesEditor'
import { TagInput } from './TagInput'

interface HostFormDialogProps {
  /** The host to edit, or null to create a new one. */
  hostId: string | null
  onClose: () => void
  /** Called after a successful save or delete so the caller can refresh. */
  onChanged: () => void
}

const NEW_GROUP = '__new__'
const FIELDS = ['name', 'hostname', 'ipAddress', 'macAddress', 'description', 'groupId', 'tags', 'services', 'credentialIds']

interface FormState {
  name: string
  hostname: string
  ipAddress: string
  macAddress: string
  description: string
  groupId: string
  newGroupName: string
  tags: string[]
  services: ServiceDraft[]
  credentialIds: string[]
}

const EMPTY_FORM: FormState = {
  name: '',
  hostname: '',
  ipAddress: '',
  macAddress: '',
  description: '',
  groupId: '',
  newGroupName: '',
  tags: [],
  services: [],
  credentialIds: [],
}

function toFormState(host: Host): FormState {
  return {
    name: host.name,
    hostname: host.hostname ?? '',
    ipAddress: host.ipAddress ?? '',
    macAddress: host.macAddress ?? '',
    description: host.description ?? '',
    groupId: host.group?.id ?? '',
    newGroupName: '',
    tags: host.tags,
    services: host.services.map((s) => newServiceDraft(s.protocol, s.port, s.label)),
    credentialIds: host.credentials.map((c) => c.id),
  }
}

const blankToNull = (value: string) => (value.trim() === '' ? null : value.trim())

export function HostFormDialog({ hostId, onClose, onChanged }: HostFormDialogProps) {
  const isEdit = hostId !== null
  const [form, setForm] = useState<FormState>(EMPTY_FORM)
  const [version, setVersion] = useState<number>()
  const [loadError, setLoadError] = useState<string | null>(null)
  const [loadingHost, setLoadingHost] = useState(isEdit)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [deleting, setDeleting] = useState(false)

  const groups = useResource((signal) => groupsApi.list(signal))
  const credentials = useResource((signal) => credentialsApi.list(signal))
  const tags = useResource((signal) => tagsApi.list(signal))

  useEffect(() => {
    if (!hostId) {
      return
    }
    const controller = new AbortController()
    hostsApi.get(hostId, controller.signal).then(
      (host) => {
        setForm(toFormState(host))
        setVersion(host.version)
        setLoadingHost(false)
      },
      (error: unknown) => {
        if (!controller.signal.aborted) {
          setLoadError(error instanceof ApiError && error.status === 404 ? 'This host no longer exists.' : toFormErrors(error).message)
          setLoadingHost(false)
        }
      },
    )
    return () => controller.abort()
  }, [hostId])

  function set<K extends keyof FormState>(key: K, value: FormState[K]) {
    setForm((current) => ({ ...current, [key]: value }))
  }

  async function resolveGroupId(): Promise<string | null | undefined> {
    if (form.groupId !== NEW_GROUP) {
      return form.groupId || null
    }
    if (!form.newGroupName.trim()) {
      setFieldErrors({ newGroupName: 'Enter a name for the new group.' })
      return undefined
    }
    try {
      const group = await groupsApi.create({ name: form.newGroupName.trim(), description: null })
      // Keep the created group selected so a failed host save can be retried without duplicating it.
      setForm((current) => ({ ...current, groupId: group.id, newGroupName: '' }))
      groups.reload()
      return group.id
    } catch (error) {
      const { fields, message } = toFormErrors(error, ['name'])
      setFieldErrors(fields.name ? { newGroupName: fields.name } : {})
      setFormError(message)
      return undefined
    }
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setFieldErrors({})
    setFormError(null)

    const clientErrors: Record<string, string> = {}
    if (!form.name.trim()) {
      clientErrors.name = 'Enter a name.'
    }
    if (!form.hostname.trim() && !form.ipAddress.trim()) {
      clientErrors.hostname = 'Enter a hostname or an IP address.'
    }
    if (Object.keys(clientErrors).length > 0) {
      setFieldErrors(clientErrors)
      return
    }

    setSaving(true)
    try {
      const groupId = await resolveGroupId()
      if (groupId === undefined) {
        return
      }
      const input: HostInput = {
        name: form.name.trim(),
        hostname: blankToNull(form.hostname),
        ipAddress: blankToNull(form.ipAddress),
        macAddress: blankToNull(form.macAddress),
        description: blankToNull(form.description),
        groupId,
        tags: form.tags,
        services: form.services.map((s) => ({
          protocol: s.protocol,
          port: s.port === '' ? null : Number(s.port),
          label: blankToNull(s.label),
        })),
        credentialIds: form.credentialIds,
        version,
      }
      if (hostId) {
        await hostsApi.update(hostId, input)
      } else {
        await hostsApi.create(input)
      }
      onChanged()
      onClose()
    } catch (error) {
      const { fields, message } = toFormErrors(error, FIELDS)
      setFieldErrors(fields)
      setFormError(message)
    } finally {
      setSaving(false)
    }
  }

  async function handleDelete() {
    if (!hostId) {
      return
    }
    setDeleting(true)
    setFormError(null)
    try {
      await hostsApi.remove(hostId)
      onChanged()
      onClose()
    } catch (error) {
      // Already gone is what the user wanted.
      if (error instanceof ApiError && error.status === 404) {
        onChanged()
        onClose()
        return
      }
      setFormError(toFormErrors(error).message)
      setDeleting(false)
    }
  }

  const busy = saving || deleting
  const formId = 'host-form'

  return (
    <Dialog
      title={isEdit ? 'Edit host' : 'Add host'}
      description={isEdit ? form.name || undefined : 'Add a server or device to the inventory.'}
      onClose={onClose}
      size="lg"
      footer={
        loadError || loadingHost ? (
          <Button variant="secondary" onClick={onClose}>
            Close
          </Button>
        ) : (
          <>
            {isEdit ? <DeleteButton question="Delete this host?" onConfirm={handleDelete} loading={deleting} disabled={saving} /> : null}
            <Button variant="secondary" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button type="submit" form={formId} loading={saving} disabled={deleting}>
              {isEdit ? 'Save changes' : 'Add host'}
            </Button>
          </>
        )
      }
    >
      {loadError ? (
        <Alert>{loadError}</Alert>
      ) : loadingHost ? (
        <div className={styles.loading}>
          <Spinner /> Loading host…
        </div>
      ) : (
        <form id={formId} className={styles.form} onSubmit={handleSubmit} noValidate>
          {formError ? <Alert>{formError}</Alert> : null}

          <div className={styles.grid}>
            <TextField
              className={styles.full}
              label="Name"
              value={form.name}
              onChange={(e) => set('name', e.target.value)}
              error={fieldErrors.name}
              maxLength={200}
              autoComplete="off"
              data-autofocus
              required
            />
            <TextField
              label="Hostname"
              value={form.hostname}
              onChange={(e) => set('hostname', e.target.value)}
              error={fieldErrors.hostname}
              placeholder="server01.example.com"
              maxLength={253}
              autoComplete="off"
              spellCheck={false}
            />
            <TextField
              label="IP address"
              value={form.ipAddress}
              onChange={(e) => set('ipAddress', e.target.value)}
              error={fieldErrors.ipAddress}
              placeholder="10.0.0.10"
              maxLength={45}
              autoComplete="off"
              spellCheck={false}
            />
            <TextField
              label="MAC address"
              value={form.macAddress}
              onChange={(e) => set('macAddress', e.target.value)}
              error={fieldErrors.macAddress}
              hint="Optional"
              placeholder="AA:BB:CC:DD:EE:FF"
              maxLength={32}
              autoComplete="off"
              spellCheck={false}
            />
            <Select
              label="Group"
              value={form.groupId}
              onChange={(e) => set('groupId', e.target.value)}
              error={fieldErrors.groupId}
            >
              <option value="">No group</option>
              {groups.data?.map((group) => (
                <option key={group.id} value={group.id}>
                  {group.name}
                </option>
              ))}
              <option value={NEW_GROUP}>+ New group…</option>
            </Select>
            {form.groupId === NEW_GROUP ? (
              <TextField
                className={styles.full}
                label="New group name"
                value={form.newGroupName}
                onChange={(e) => set('newGroupName', e.target.value)}
                error={fieldErrors.newGroupName}
                hint="Created when you save."
                maxLength={100}
                autoFocus
              />
            ) : null}
            <div className={styles.full}>
              <TagInput
                label="Tags"
                value={form.tags}
                onChange={(value) => set('tags', value)}
                suggestions={tags.data?.map((t) => t.name)}
                error={errorFor(fieldErrors, 'tags')}
              />
            </div>
            <TextArea
              className={styles.full}
              label="Description"
              value={form.description}
              onChange={(e) => set('description', e.target.value)}
              error={fieldErrors.description}
              maxLength={4000}
              rows={2}
            />
          </div>

          <div className={styles.section}>
            <ServicesEditor value={form.services} onChange={(value) => set('services', value)} errors={fieldErrors} />
          </div>

          <div className={styles.section}>
            <CredentialPicker
              credentials={credentials.data}
              value={form.credentialIds}
              onChange={(value) => set('credentialIds', value)}
              error={errorFor(fieldErrors, 'credentialIds')}
            />
          </div>
        </form>
      )}
    </Dialog>
  )
}
