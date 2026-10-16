import { useId, useMemo } from 'react'
import { hypervisorChoices } from '../hypervisors'
import type { Host } from '../types'
import listStyles from './ListEditor.module.css'
import styles from './HostPanel.module.css'

interface HypervisorFieldProps {
  /** Hosts the VM can run on (every host but itself). */
  hosts: Host[]
  /** The chosen host's id, or empty for "unknown". */
  value: string
  onChange: (id: string) => void
  error?: string
  /** When a scan last found the VM on the current choice. */
  seenAtUtc: string | null
}

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

/** "Runs on": the KVM or Proxmox node a virtual machine runs on. Hypervisors are listed first. */
export function HypervisorField({ hosts, value, onChange, error, seenAtUtc }: HypervisorFieldProps) {
  const id = useId()
  const options = useMemo(() => hypervisorChoices(hosts), [hosts])

  const hint = seenAtUtc
    ? `Seen there by a scan on ${dateFormat.format(new Date(seenAtUtc))}. VMs that move between nodes are updated by scanning the nodes.`
    : 'Scanning a KVM or Proxmox node (Tasks ▸ Scan ▸ Internal) fills this in and keeps it current as VMs move.'

  return (
    <div className={`${styles.full} ${styles.field}`}>
      <label htmlFor={id} className={styles.fieldLabel}>
        Runs on
      </label>
      <select
        id={id}
        className={listStyles.input}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={`${id}-hint`}
      >
        <option value="">Unknown</option>
        {options.hypervisors.length > 0 ? (
          <optgroup label="Hypervisors">
            {options.hypervisors.map((h) => (
              <option key={h.id} value={h.id}>
                {h.name}
              </option>
            ))}
          </optgroup>
        ) : null}
        <optgroup label={options.hypervisors.length > 0 ? 'Other hosts' : 'Hosts'}>
          {options.others.map((h) => (
            <option key={h.id} value={h.id}>
              {h.name}
            </option>
          ))}
        </optgroup>
      </select>
      {error ? <p className={styles.fieldError}>{error}</p> : null}
      <p id={`${id}-hint`} className={styles.fieldHint}>
        {hint}
      </p>
    </div>
  )
}
