import { useId } from 'react'
import { Button } from '../../../components/ui/Button'
import { newTagDraft, type TagDraft } from '../drafts'
import styles from './ListEditor.module.css'

interface TagEditorProps {
  value: TagDraft[]
  onChange: (tags: TagDraft[]) => void
  /** Tag names already used across the inventory, offered as suggestions. */
  knownNames: string[]
  /** Values already used for a tag name, offered as suggestions. */
  knownValues: (name: string) => string[]
  /** Field errors from the server, keyed like "tags[0].name". */
  errors: Record<string, string>
}

const MAX_TAGS = 50

/** Zabbix-style `name: value` tags. Tags drive the tree's "group by", e.g. site → role. */
export function TagEditor({ value, onChange, knownNames, knownValues, errors }: TagEditorProps) {
  const id = useId()
  const namesListId = `${id}-names`

  function update(key: number, patch: Partial<TagDraft>) {
    onChange(value.map((t) => (t.key === key ? { ...t, ...patch } : t)))
  }

  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.legend}>Tags</legend>
      <p className={styles.hint}>
        Name and optional value, e.g. <code>site: Datacenter A</code> or <code>env: production</code>. Group the host tree by any tag name.
      </p>

      <datalist id={namesListId}>
        {knownNames.map((name) => (
          <option key={name} value={name} />
        ))}
      </datalist>

      {value.length === 0 ? <p className={styles.empty}>No tags yet.</p> : null}

      <ol className={styles.list}>
        {value.map((tag, index) => {
          const position = `tag ${index + 1}`
          const nameError = errors[`tags[${index}].name`]
          const valueError = errors[`tags[${index}].value`]
          const valuesListId = `${id}-values-${tag.key}`
          const suggestions = tag.name.trim() ? knownValues(tag.name) : []
          return (
            <li key={tag.key} className={styles.tagRow}>
              <input
                className={styles.input}
                value={tag.name}
                onChange={(event) => update(tag.key, { name: event.target.value.replace(/:/g, '') })}
                placeholder="Name"
                list={namesListId}
                maxLength={64}
                aria-label={`Name for ${position}`}
                aria-invalid={nameError ? true : undefined}
                autoComplete="off"
              />
              <span className={styles.separator} aria-hidden="true">
                :
              </span>
              <input
                className={styles.input}
                value={tag.value}
                onChange={(event) => update(tag.key, { value: event.target.value })}
                placeholder="Value (optional)"
                list={suggestions.length > 0 ? valuesListId : undefined}
                maxLength={255}
                aria-label={`Value for ${position}`}
                aria-invalid={valueError ? true : undefined}
                autoComplete="off"
              />
              {suggestions.length > 0 ? (
                <datalist id={valuesListId}>
                  {suggestions.map((v) => (
                    <option key={v} value={v} />
                  ))}
                </datalist>
              ) : null}
              <button
                type="button"
                className={styles.iconButton}
                onClick={() => onChange(value.filter((t) => t.key !== tag.key))}
                aria-label={`Remove ${position}`}
              >
                ×
              </button>
              {nameError || valueError ? <p className={styles.error}>{nameError ?? valueError}</p> : null}
            </li>
          )
        })}
      </ol>

      {errors.tags ? <p className={styles.error}>{errors.tags}</p> : null}

      <div>
        <Button variant="secondary" size="sm" onClick={() => onChange([...value, newTagDraft()])} disabled={value.length >= MAX_TAGS}>
          Add tag
        </Button>
      </div>
    </fieldset>
  )
}
