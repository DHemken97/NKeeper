import { useId } from 'react'
import { Button } from '../../../components/ui/Button'
import { tagNames, tagValues } from '../../inventory/grouping'
import type { Host } from '../../inventory/types'
import { primaryAddress } from '../../inventory/types'
import { toGrantDraft, type GrantDraft } from '../grantDraft'
import { ACCESS_LEVEL_INFO, grantMatches, type AccessLevel } from '../types'
import styles from './GrantsEditor.module.css'

interface GrantsEditorProps {
  value: GrantDraft[]
  onChange: (grants: GrantDraft[]) => void
  /** Every host (the editor runs for administrators, who see them all). */
  hosts: Host[]
}

/**
 * Which hosts a user may use or manage: specific hosts, or groups by tag (e.g. site: DC-A, or
 * just "site" for any value). Shows how many hosts each grant covers right now; group grants
 * also cover hosts tagged later.
 */
export function GrantsEditor({ value, onChange, hosts }: GrantsEditorProps) {
  const id = useId()
  const namesList = `${id}-names`
  const sortedHosts = [...hosts].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base', numeric: true }))
  const covered = hosts.filter((h) => value.some((g) => grantMatches(g, h)))
  const manageable = hosts.filter((h) => value.some((g) => g.level === 'Manage' && grantMatches(g, h)))

  function update(key: number, patch: Partial<GrantDraft>) {
    onChange(value.map((g) => (g.key === key ? { ...g, ...patch } : g)))
  }

  function add(kind: 'host' | 'group') {
    onChange([
      ...value,
      toGrantDraft(kind === 'host' ? { hostId: null, tagName: null, tagValue: null, level: 'Use' } : { hostId: null, tagName: '', tagValue: null, level: 'Use' }),
    ])
  }

  return (
    <div className={styles.editor}>
      <datalist id={namesList}>
        {tagNames(hosts).map((name) => (
          <option key={name} value={name} />
        ))}
      </datalist>

      {value.length === 0 ? <p className={styles.empty}>No access yet. This user will see an empty host tree.</p> : null}

      <ul className={styles.list} aria-label="Access grants">
        {value.map((grant, index) => {
          const isGroup = grant.tagName !== null
          const position = `grant ${index + 1}`
          const matches = hosts.filter((h) => grantMatches(grant, h)).length
          const valuesList = `${id}-values-${grant.key}`
          return (
            <li key={grant.key} className={styles.row}>
              <span className={styles.kind}>{isGroup ? 'Group' : 'Host'}</span>
              {isGroup ? (
                <div className={styles.target}>
                  <input
                    className={styles.input}
                    value={grant.tagName ?? ''}
                    onChange={(e) => update(grant.key, { tagName: e.target.value.replace(/:/g, '') })}
                    placeholder="Tag name, e.g. site"
                    list={namesList}
                    aria-label={`Tag name for ${position}`}
                    autoComplete="off"
                  />
                  <span className={styles.separator}>:</span>
                  <input
                    className={styles.input}
                    value={grant.tagValue ?? ''}
                    onChange={(e) => update(grant.key, { tagValue: e.target.value || null })}
                    placeholder="any value"
                    list={valuesList}
                    aria-label={`Tag value for ${position}`}
                    autoComplete="off"
                  />
                  <datalist id={valuesList}>
                    {tagValues(hosts, grant.tagName ?? '').map((v) => (
                      <option key={v} value={v} />
                    ))}
                  </datalist>
                </div>
              ) : (
                <select
                  className={`${styles.input} ${styles.target}`}
                  value={grant.hostId ?? ''}
                  onChange={(e) => update(grant.key, { hostId: e.target.value || null })}
                  aria-label={`Host for ${position}`}
                >
                  <option value="">Choose a host…</option>
                  {grant.hostId && !hosts.some((h) => h.id === grant.hostId) ? (
                    <option value={grant.hostId}>{grant.hostName ?? 'Deleted host'}</option>
                  ) : null}
                  {sortedHosts.map((host) => (
                    <option key={host.id} value={host.id}>
                      {host.name} — {primaryAddress(host)}
                    </option>
                  ))}
                </select>
              )}
              <select
                className={styles.input}
                value={grant.level}
                onChange={(e) => update(grant.key, { level: e.target.value as AccessLevel })}
                aria-label={`Access level for ${position}`}
              >
                {Object.entries(ACCESS_LEVEL_INFO).map(([level, info]) => (
                  <option key={level} value={level}>
                    {info.label} — {info.description}
                  </option>
                ))}
              </select>
              <span className={styles.matches}>{matches === 1 ? '1 host' : `${matches} hosts`}</span>
              <button type="button" className={styles.remove} onClick={() => onChange(value.filter((g) => g.key !== grant.key))} aria-label={`Remove ${position}`}>
                ×
              </button>
            </li>
          )
        })}
      </ul>

      <div className={styles.footer}>
        <div className={styles.actions}>
          <Button variant="secondary" size="sm" onClick={() => add('group')}>
            Add group
          </Button>
          <Button variant="secondary" size="sm" onClick={() => add('host')}>
            Add host
          </Button>
        </div>
        <p className={styles.summary} aria-live="polite">
          Can use {covered.length} of {hosts.length} hosts{manageable.length > 0 ? `, manage ${manageable.length}` : ''}.
        </p>
      </div>
    </div>
  )
}
