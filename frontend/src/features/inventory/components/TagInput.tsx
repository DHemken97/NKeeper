import { useId, useState, type KeyboardEvent } from 'react'
import styles from './TagInput.module.css'

interface TagInputProps {
  label: string
  value: string[]
  onChange: (tags: string[]) => void
  /** Existing tags offered as autocomplete suggestions. */
  suggestions?: string[]
  error?: string
  maxTags?: number
}

const MAX_TAG_LENGTH = 50

/** Free-form tags: Enter or comma adds, Backspace on an empty input removes the last one. */
export function TagInput({ label, value, onChange, suggestions = [], error, maxTags = 20 }: TagInputProps) {
  const id = useId()
  const listId = `${id}-suggestions`
  const hintId = `${id}-hint`
  const errorId = `${id}-error`
  const [draft, setDraft] = useState('')

  function add(raw: string) {
    const tag = raw.trim().slice(0, MAX_TAG_LENGTH)
    if (!tag || value.length >= maxTags) {
      return
    }
    // Tags are case-insensitive on the server; keep the first spelling.
    if (!value.some((existing) => existing.toLowerCase() === tag.toLowerCase())) {
      onChange([...value, tag])
    }
    setDraft('')
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' || event.key === ',') {
      event.preventDefault()
      add(draft)
    } else if (event.key === 'Backspace' && draft === '' && value.length > 0) {
      onChange(value.slice(0, -1))
    }
  }

  const available = suggestions.filter((s) => !value.some((v) => v.toLowerCase() === s.toLowerCase()))

  return (
    <div className={styles.field}>
      <label htmlFor={id} className={styles.label}>
        {label}
      </label>
      <div className={styles.control} data-invalid={error ? true : undefined}>
        {value.length > 0 ? (
          <ul className={styles.tags} aria-label={`${label} added`}>
            {value.map((tag) => (
              <li key={tag} className={styles.tag}>
                <span>{tag}</span>
                <button
                  type="button"
                  className={styles.remove}
                  onClick={() => onChange(value.filter((t) => t !== tag))}
                  aria-label={`Remove tag ${tag}`}
                >
                  ×
                </button>
              </li>
            ))}
          </ul>
        ) : null}
        <input
          id={id}
          className={styles.input}
          value={draft}
          list={available.length > 0 ? listId : undefined}
          onChange={(event) => {
            const next = event.target.value
            // Pasting "a, b, c" adds each tag.
            if (next.includes(',')) {
              next.split(',').slice(0, -1).forEach(add)
              setDraft(next.slice(next.lastIndexOf(',') + 1))
            } else {
              setDraft(next)
            }
          }}
          onKeyDown={handleKeyDown}
          onBlur={() => add(draft)}
          placeholder={value.length === 0 ? 'e.g. production, linux' : 'Add tag'}
          maxLength={MAX_TAG_LENGTH}
          disabled={value.length >= maxTags}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? `${hintId} ${errorId}` : hintId}
        />
        {available.length > 0 ? (
          <datalist id={listId}>
            {available.map((s) => (
              <option key={s} value={s} />
            ))}
          </datalist>
        ) : null}
      </div>
      <p id={hintId} className={styles.hint}>
        Press Enter or comma to add a tag.
      </p>
      {error ? (
        <p id={errorId} className={styles.error}>
          {error}
        </p>
      ) : null}
    </div>
  )
}
