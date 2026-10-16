import { decodeGroupKey, encodeGroupKey, groupKeyLabel, type GroupKey } from '../grouping'
import styles from './GroupByPicker.module.css'

interface GroupByPickerProps {
  value: GroupKey[]
  onChange: (keys: GroupKey[]) => void
  /** Tag names in use across the inventory. */
  tagNames: string[]
}

const MAX_LEVELS = 3

/** Choose up to three nested grouping levels, e.g. site → role. */
export function GroupByPicker({ value, onChange, tagNames }: GroupByPickerProps) {
  const used = new Set(value.map((k) => encodeGroupKey(k).toLowerCase()))
  const candidates: GroupKey[] = [{ kind: 'service' }, { kind: 'hypervisor' }, ...tagNames.map((name): GroupKey => ({ kind: 'tag', name }))]
  const options = candidates.filter((k) => !used.has(encodeGroupKey(k).toLowerCase()))

  return (
    <div className={styles.picker}>
      <span className={styles.label} id="group-by-label">
        Group by
      </span>
      <ol className={styles.levels} aria-labelledby="group-by-label">
        {value.map((key, index) => (
          <li key={encodeGroupKey(key)} className={styles.level}>
            {index > 0 ? (
              <span className={styles.arrow} aria-hidden="true">
                ›
              </span>
            ) : null}
            <span className={styles.chip}>
              {groupKeyLabel(key)}
              <button
                type="button"
                className={styles.remove}
                onClick={() => onChange(value.filter((_, i) => i !== index))}
                aria-label={`Stop grouping by ${groupKeyLabel(key)}`}
              >
                ×
              </button>
            </span>
          </li>
        ))}
      </ol>
      {value.length < MAX_LEVELS && options.length > 0 ? (
        <select
          className={styles.add}
          value=""
          onChange={(event) => {
            if (event.target.value) {
              onChange([...value, decodeGroupKey(event.target.value)])
            }
          }}
          aria-label={value.length === 0 ? 'Group by' : 'Add grouping level'}
        >
          <option value="">{value.length === 0 ? 'None' : '+ Level'}</option>
          {options.map((key) => (
            <option key={encodeGroupKey(key)} value={encodeGroupKey(key)}>
              {key.kind === 'service' ? 'Service' : key.kind === 'hypervisor' ? 'Runs on (hypervisor)' : `Tag: ${key.name}`}
            </option>
          ))}
        </select>
      ) : null}
    </div>
  )
}
