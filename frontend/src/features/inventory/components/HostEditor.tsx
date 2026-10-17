import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { TextArea } from '../../../components/ui/TextArea'
import { TextField } from '../../../components/ui/TextField'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../api/inventoryApi'
import { toCloneDraft, toHostDraft, toHostInput, type HostDraft } from '../drafts'
import { isSupportedProtocol, type Host } from '../types'
import { CredentialsEditor } from './CredentialsEditor'
import { HypervisorField } from './HypervisorField'
import styles from './HostPanel.module.css'
import { ServicesEditor } from './ServicesEditor'
import { TagEditor } from './TagEditor'

interface HostEditorProps {
  /** The host to edit, or null to create one. */
  host: Host | null
  /** When creating: start from a copy of this host. */
  cloneOf?: Host
  knownTagNames: string[]
  knownTagValues: (name: string) => string[]
  /** Every host, to choose the one a virtual machine runs on. */
  allHosts: Host[]
  onSaved: (host: Host) => void
  onCancel: () => void
}

const FIELDS = [
  'name',
  'hostname',
  'ipAddress',
  'macAddress',
  'description',
  'cpuCores',
  'memoryMiB',
  'serialNumber',
  'hypervisorId',
  'tags',
  'services',
  'credentials',
]

/** Create or edit a host in the details panel, including its tags, services and credentials. */
export function HostEditor({ host, cloneOf, knownTagNames, knownTagValues, allHosts, onSaved, onCancel }: HostEditorProps) {
  const [draft, setDraft] = useState<HostDraft>(() => (!host && cloneOf ? toCloneDraft(cloneOf) : toHostDraft(host)))
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  function set<K extends keyof HostDraft>(key: K, value: HostDraft[K]) {
    setDraft((current) => ({ ...current, [key]: value }))
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setFormError(null)

    const clientErrors: Record<string, string> = {}
    if (!draft.name.trim()) {
      clientErrors.name = 'Enter a name.'
    }
    if (!draft.hostname.trim() && !draft.ipAddress.trim()) {
      clientErrors.hostname = 'Enter a hostname or an IP address.'
    }
    if (draft.cpuCores.trim() && !/^\d+$/.test(draft.cpuCores.trim())) {
      clientErrors.cpuCores = 'Enter a whole number of cores.'
    }
    const gib = Number(draft.memoryGiB.trim().replace(',', '.'))
    if (draft.memoryGiB.trim() && !(gib > 0)) {
      clientErrors.memoryMiB = 'Enter the memory in GiB, e.g. 64 or 0.5.'
    }
    draft.tags.forEach((tag, index) => {
      if (!tag.name.trim() && tag.value.trim()) {
        clientErrors[`tags[${index}].name`] = 'Enter a tag name.'
      }
    })
    draft.credentials.forEach((credential, index) => {
      if (!credential.username.trim() && !credential.label.trim()) {
        clientErrors[`credentials[${index}].username`] = 'Enter a username or a label.'
      }
    })
    setFieldErrors(clientErrors)
    if (Object.keys(clientErrors).length > 0) {
      setFormError('Fix the highlighted fields and try again.')
      return
    }

    setSaving(true)
    try {
      const input = toHostInput(draft, host?.version)
      onSaved(host ? await hostsApi.update(host.id, input) : await hostsApi.create(input))
    } catch (error) {
      const { fields, message } = toFormErrors(error, FIELDS)
      setFieldErrors(fields)
      setFormError(message ?? 'Fix the highlighted fields and try again.')
      setSaving(false)
    }
  }

  const cloning = !host && cloneOf
  const title = host ? `Edit ${host.name}` : cloning ? `Clone ${cloneOf.name}` : 'New host'

  return (
    <form className={styles.panel} onSubmit={handleSubmit} noValidate aria-label={title}>
      <header className={styles.header}>
        <div className={styles.heading}>
          <h1 className={styles.title}>{title}</h1>
          {host ? null : (
            <p className={styles.subtitle}>
              {cloning
                ? 'A new host with the same details, tags, services and credentials. Change what differs, then create it.'
                : 'Add a server or device to the inventory.'}
            </p>
          )}
        </div>
      </header>

      <div className={styles.body}>
        {formError ? <Alert>{formError}</Alert> : null}

        <section className={styles.section} aria-labelledby="host-details-title">
          <h2 id="host-details-title" className={styles.sectionTitle}>
            Details
          </h2>
          <div className={styles.formGrid}>
            <TextField
              className={styles.full}
              label="Name"
              value={draft.name}
              onChange={(e) => set('name', e.target.value)}
              error={fieldErrors.name}
              maxLength={200}
              autoComplete="off"
              autoFocus
              required
            />
            <TextField
              label="Hostname"
              value={draft.hostname}
              onChange={(e) => set('hostname', e.target.value)}
              error={fieldErrors.hostname}
              placeholder="server01.example.com"
              maxLength={253}
              autoComplete="off"
              spellCheck={false}
            />
            <TextField
              label="IP address"
              value={draft.ipAddress}
              onChange={(e) => set('ipAddress', e.target.value)}
              error={fieldErrors.ipAddress}
              placeholder="10.0.0.10"
              maxLength={45}
              autoComplete="off"
              spellCheck={false}
            />
            <TextField
              label="MAC address"
              value={draft.macAddress}
              onChange={(e) => set('macAddress', e.target.value)}
              error={fieldErrors.macAddress}
              hint="Optional"
              placeholder="AA:BB:CC:DD:EE:FF"
              maxLength={32}
              autoComplete="off"
              spellCheck={false}
            />
            <TextField
              label={draft.isVirtualMachine ? 'vCPUs' : 'CPU cores'}
              value={draft.cpuCores}
              onChange={(e) => set('cpuCores', e.target.value)}
              error={fieldErrors.cpuCores}
              hint={draft.isVirtualMachine ? 'What the VM is given' : 'Logical processors (threads)'}
              inputMode="numeric"
              maxLength={6}
              autoComplete="off"
            />
            <TextField
              label="Memory (GiB)"
              value={draft.memoryGiB}
              onChange={(e) => set('memoryGiB', e.target.value)}
              error={fieldErrors.memoryMiB}
              hint={draft.isVirtualMachine ? 'What the VM is given' : 'Optional'}
              inputMode="decimal"
              maxLength={10}
              autoComplete="off"
            />
            {!draft.isVirtualMachine ? (
              <TextField
                label="Serial number"
                value={draft.serialNumber}
                onChange={(e) => set('serialNumber', e.target.value)}
                error={fieldErrors.serialNumber}
                hint="Dell: the service tag"
                maxLength={128}
                autoComplete="off"
                spellCheck={false}
              />
            ) : null}
            <TextArea
              className={styles.full}
              label="Description"
              value={draft.description}
              onChange={(e) => set('description', e.target.value)}
              error={fieldErrors.description}
              maxLength={4000}
              rows={2}
            />
            <Checkbox
              className={styles.full}
              label="Virtual machine"
              checked={draft.isVirtualMachine}
              onChange={(e) => set('isVirtualMachine', e.target.checked)}
            />
            {draft.isVirtualMachine ? (
              <HypervisorField
                hosts={allHosts.filter((h) => h.id !== host?.id)}
                value={draft.hypervisorId}
                onChange={(id) => set('hypervisorId', id)}
                error={fieldErrors.hypervisorId}
                seenAtUtc={host && host.hypervisorId === draft.hypervisorId ? host.hypervisorSeenAtUtc : null}
              />
            ) : null}
          </div>
        </section>

        <section className={styles.section}>
          <TagEditor
            value={draft.tags}
            onChange={(tags) => set('tags', tags)}
            knownNames={knownTagNames}
            knownValues={knownTagValues}
            errors={fieldErrors}
          />
        </section>

        <section className={styles.section}>
          <ServicesEditor
            value={draft.services}
            onChange={(services) => set('services', services)}
            errors={fieldErrors}
            dropped={(host ?? cloneOf)?.services.filter((s) => !isSupportedProtocol(s.protocol))}
          />
        </section>

        <section className={styles.section}>
          <CredentialsEditor value={draft.credentials} onChange={(credentials) => set('credentials', credentials)} errors={fieldErrors} />
        </section>
      </div>

      <footer className={styles.footer}>
        <Button variant="secondary" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" loading={saving}>
          {host ? 'Save changes' : 'Create host'}
        </Button>
      </footer>
    </form>
  )
}
