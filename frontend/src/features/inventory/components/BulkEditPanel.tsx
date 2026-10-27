import { useId, useMemo, useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../api/inventoryApi'
import { BulkScanDialog } from './BulkScanDialog'
import { ServicesEditor } from './ServicesEditor'
import type { ServiceDraft } from '../drafts'
import { hypervisorChoices } from '../hypervisors'
import { PROTOCOL_INFO, SUPPORTED_PROTOCOLS, type Host, type HostService, type HostTag } from '../types'
import listStyles from './ListEditor.module.css'
import styles from './HostPanel.module.css'

interface BulkEditPanelProps {
  /** Every selected host, including ones hidden by the current search. */
  selected: Host[]
  /** How many of them the current search hides. */
  hiddenCount: number
  allHosts: Host[]
  knownTagNames: string[]
  knownTagValues: (name: string) => string[]
  onApplied: (hosts: Host[]) => void
  onClear: () => void
  /** A host a bulk scan changed or added. */
  onScanned: (host: Host) => void
  /** Administrators can add hosts for VMs a scan finds. */
  canAddHosts: boolean
}

type TagAction = 'add' | 'set' | 'remove'

interface TagChange {
  key: number
  action: TagAction
  name: string
  value: string
}

const KEEP = '__keep'
const CLEAR = '__clear'
let nextKey = 1

// Each iDRAC has its own address, so iDRAC services are added one host at a time.
const BULK_PROTOCOLS = SUPPORTED_PROTOCOLS.filter((p) => p !== 'Idrac')

const toService = (draft: ServiceDraft): HostService => ({
  protocol: draft.protocol,
  port: draft.port === '' ? null : Number(draft.port),
  label: draft.label.trim() || null,
  ...(draft.protocol === 'Rdp' ? { rdpSecurity: draft.rdpSecurity } : {}),
})

/** How many hosts a new service would be added to, in words: hosts with the same type and port keep theirs. */
function serviceImpact(draft: ServiceDraft, hosts: Host[]): string {
  const { protocol, port } = toService(draft)
  const count = hosts.filter((h) => !h.services.some((s) => s.protocol === protocol && s.port === port)).length
  const name = PROTOCOL_INFO[protocol].label
  return count === 0 ? `All selected hosts already have ${name}${port ? ` on port ${port}` : ''}` : `Adds it to ${count === 1 ? '1 host' : `${count} hosts`}`
}

const same = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: 'accent' }) === 0
const hasTag = (host: Host, name: string, value: string | null) =>
  host.tags.some((t) => same(t.name, name) && (value === null || same(t.value ?? '', value)))

/** How many hosts a tag change would change, in words. */
function impact(change: TagChange, hosts: Host[]): string | null {
  const name = change.name.trim()
  if (!name) {
    return null
  }
  const value = change.value.trim()
  const count =
    change.action === 'add'
      ? hosts.filter((h) => !hasTag(h, name, value)).length
      : change.action === 'set'
        ? hosts.filter((h) => !(h.tags.filter((t) => same(t.name, name)).length === 1 && hasTag(h, name, value))).length
        : hosts.filter((h) => hasTag(h, name, value || null)).length
  const hostWord = (n: number) => (n === 1 ? '1 host' : `${n} hosts`)
  if (change.action === 'remove') {
    return count === 0 ? 'No selected host has it' : count === 1 ? '1 host has it' : `${count} hosts have it`
  }
  return count === 0 ? 'All selected hosts already have it' : `Changes ${hostWord(count)}`
}

/**
 * Changes for several hosts at once (Ctrl/⌘+click, Shift+click, or a group's "Select hosts"):
 * add, set or remove tags, set or clear the host they run on, and add services. Applied in one request; hosts the
 * user can't manage are left out.
 */
export function BulkEditPanel({
  selected,
  hiddenCount,
  allHosts,
  knownTagNames,
  knownTagValues,
  onApplied,
  onClear,
  onScanned,
  canAddHosts,
}: BulkEditPanelProps) {
  const id = useId()
  const [changes, setChanges] = useState<TagChange[]>([{ key: nextKey++, action: 'add', name: '', value: '' }])
  const [hypervisor, setHypervisor] = useState(KEEP)
  const [services, setServices] = useState<ServiceDraft[]>([])
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const [scanning, setScanning] = useState(false)

  const manageable = selected.filter((h) => h.canManage)
  const locked = selected.length - manageable.length
  const selectedIds = useMemo(() => new Set(selected.map((h) => h.id)), [selected])
  const choices = useMemo(() => hypervisorChoices(allHosts.filter((h) => !selectedIds.has(h.id))), [allHosts, selectedIds])
  const filled = changes.filter((c) => c.name.trim())
  const nothing = filled.length === 0 && hypervisor === KEEP && services.length === 0

  function update(key: number, patch: Partial<TagChange>) {
    setChanges((current) => current.map((c) => (c.key === key ? { ...c, ...patch } : c)))
    setDone(null)
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (nothing || manageable.length === 0) {
      return
    }
    const tags = (action: TagAction): HostTag[] =>
      filled.filter((c) => c.action === action).map((c) => ({ name: c.name.trim(), value: c.value.trim() || null }))
    setSaving(true)
    setError(null)
    try {
      const updated = await hostsApi.bulkUpdate({
        hostIds: manageable.map((h) => h.id),
        addTags: tags('add'),
        setTags: tags('set'),
        removeTags: tags('remove'),
        ...(hypervisor === KEEP ? {} : { hypervisor: { hypervisorId: hypervisor === CLEAR ? null : hypervisor } }),
        ...(services.length > 0 ? { addServices: services.map(toService) } : {}),
      })
      onApplied(updated)
      setChanges([{ key: nextKey++, action: 'add', name: '', value: '' }])
      setHypervisor(KEEP)
      setServices([])
      setDone(`Updated ${updated.length === 1 ? '1 host' : `${updated.length} hosts`}.`)
    } catch (failure) {
      const errors = toFormErrors(failure)
      setError([errors.message, ...Object.values(errors.fields)].filter(Boolean).join(' ') || 'The changes couldn’t be saved.')
    } finally {
      setSaving(false)
    }
  }

  const title = `${selected.length} hosts selected`
  return (
    <form className={styles.panel} onSubmit={(event) => void handleSubmit(event)} noValidate aria-label={title}>
      <header className={styles.header}>
        <div className={styles.heading}>
          <h1 className={styles.title}>{title}</h1>
          <p className={styles.subtitle}>
            Changes apply to every selected host.{hiddenCount > 0 ? ` ${hiddenCount} of them aren’t shown by the current search.` : ''}
          </p>
        </div>
        <div className={styles.actions}>
          <Button variant="secondary" onClick={onClear}>
            Clear selection
          </Button>
          <Button variant="secondary" onClick={() => setScanning(true)} disabled={manageable.length === 0}>
            Internal scan…
          </Button>
        </div>
      </header>

      <div className={styles.body}>
        {error ? <Alert>{error}</Alert> : null}
        {done ? <Alert variant="info">{done}</Alert> : null}
        {locked > 0 ? (
          <Alert variant="info">
            You can’t change {locked === 1 ? '1 of them' : `${locked} of them`}, so {locked === 1 ? 'it is' : 'they are'} left out.
          </Alert>
        ) : null}

        <details className={styles.section}>
          <summary className={styles.sectionTitle}>Selected hosts</summary>
          <ul className={styles.guests}>
            {selected.map((h) => (
              <li key={h.id}>
                {h.name}
                {h.canManage ? null : <span className={styles.muted}> (can’t change)</span>}
              </li>
            ))}
          </ul>
        </details>

        <fieldset className={listStyles.fieldset}>
          <legend className={listStyles.legend}>Tags</legend>
          <p className={listStyles.hint}>Set replaces every value of the tag; Remove without a value removes all of them.</p>
          <datalist id={`${id}-names`}>
            {knownTagNames.map((name) => (
              <option key={name} value={name} />
            ))}
          </datalist>
          <ol className={listStyles.list}>
            {changes.map((change, index) => {
              const position = `tag change ${index + 1}`
              const values = change.name.trim() ? knownTagValues(change.name) : []
              const effect = impact(change, manageable)
              return (
                <li key={change.key} className={styles.bulkRow}>
                  <select
                    className={listStyles.input}
                    value={change.action}
                    onChange={(event) => update(change.key, { action: event.target.value as TagAction })}
                    aria-label={`Action for ${position}`}
                  >
                    <option value="add">Add</option>
                    <option value="set">Set</option>
                    <option value="remove">Remove</option>
                  </select>
                  <input
                    className={listStyles.input}
                    value={change.name}
                    onChange={(event) => update(change.key, { name: event.target.value.replace(/:/g, '') })}
                    placeholder="Name"
                    list={`${id}-names`}
                    maxLength={64}
                    aria-label={`Name for ${position}`}
                    autoComplete="off"
                  />
                  <input
                    className={listStyles.input}
                    value={change.value}
                    onChange={(event) => update(change.key, { value: event.target.value })}
                    placeholder={change.action === 'remove' ? 'Any value' : 'Value (optional)'}
                    list={values.length > 0 ? `${id}-values-${change.key}` : undefined}
                    maxLength={255}
                    aria-label={`Value for ${position}`}
                    autoComplete="off"
                  />
                  {values.length > 0 ? (
                    <datalist id={`${id}-values-${change.key}`}>
                      {values.map((v) => (
                        <option key={v} value={v} />
                      ))}
                    </datalist>
                  ) : null}
                  <button
                    type="button"
                    className={listStyles.iconButton}
                    onClick={() => setChanges((current) => current.filter((c) => c.key !== change.key))}
                    aria-label={`Remove ${position}`}
                  >
                    ×
                  </button>
                  {effect ? <p className={`${styles.fieldHint} ${styles.bulkEffect}`}>{effect}</p> : null}
                </li>
              )
            })}
          </ol>
          <div>
            <Button variant="secondary" size="sm" onClick={() => setChanges((current) => [...current, { key: nextKey++, action: 'add', name: '', value: '' }])}>
              Add a tag change
            </Button>
          </div>
        </fieldset>

        <div className={styles.field}>
          <label htmlFor={`${id}-runs-on`} className={styles.fieldLabel}>
            Runs on
          </label>
          <select
            id={`${id}-runs-on`}
            className={listStyles.input}
            value={hypervisor}
            onChange={(event) => {
              setHypervisor(event.target.value)
              setDone(null)
            }}
          >
            <option value={KEEP}>Don’t change</option>
            <option value={CLEAR}>Unknown (clear it)</option>
            {choices.hypervisors.length > 0 ? (
              <optgroup label="Hypervisors">
                {choices.hypervisors.map((h) => (
                  <option key={h.id} value={h.id}>
                    {h.name}
                  </option>
                ))}
              </optgroup>
            ) : null}
            <optgroup label={choices.hypervisors.length > 0 ? 'Other hosts' : 'Hosts'}>
              {choices.others.map((h) => (
                <option key={h.id} value={h.id}>
                  {h.name}
                </option>
              ))}
            </optgroup>
          </select>
          <p className={styles.fieldHint}>Choosing a host also marks the selected hosts as virtual machines.</p>
        </div>

        <ServicesEditor
          value={services}
          onChange={(next) => {
            setServices(next)
            setDone(null)
          }}
          errors={{}}
          protocols={BULK_PROTOCOLS}
          hint="Added after each host’s own services. Hosts that already have the same type on the same port keep theirs. Leave the port empty to use the default."
          emptyText="No services to add."
          reorderable={false}
          note={(service) => serviceImpact(service, manageable)}
        />
      </div>

      <footer className={styles.footer}>
        <Button type="submit" loading={saving} disabled={nothing || manageable.length === 0}>
          {manageable.length === 1 ? 'Apply to 1 host' : `Apply to ${manageable.length} hosts`}
        </Button>
      </footer>

      {scanning ? <BulkScanDialog hosts={manageable} canAddHosts={canAddHosts} onClose={() => setScanning(false)} onApplied={onScanned} /> : null}
    </form>
  )
}
