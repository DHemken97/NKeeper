import { ApiError } from './api'

export interface FormErrors {
  /** Errors keyed by the request's JSON property path, e.g. "name" or "services[0].port". */
  fields: Record<string, string>
  /** A message for the whole form, or null when every error belongs to a field. */
  message: string | null
}

/**
 * Turns a failed save into form errors. Validation problems (400) map onto fields;
 * anything else becomes a single form-level message.
 */
export function toFormErrors(error: unknown, knownFields: readonly string[] = []): FormErrors {
  if (!(error instanceof ApiError)) {
    return { fields: {}, message: 'Something went wrong. Please try again.' }
  }

  const problemErrors = error.status === 400 ? error.problem?.errors : undefined
  if (!problemErrors) {
    return { fields: {}, message: error.message }
  }

  const fields: Record<string, string> = {}
  const unplaced: string[] = []
  for (const [rawKey, messages] of Object.entries(problemErrors)) {
    const key = normalizeKey(rawKey)
    const text = messages[0] ?? 'This value is invalid.'
    const placed = knownFields.some((field) => key === field || key.startsWith(`${field}[`) || key.startsWith(`${field}.`))
    if (placed && !(key in fields)) {
      fields[key] = text
    } else if (!placed) {
      unplaced.push(text)
    }
  }

  return { fields, message: unplaced.length > 0 ? unplaced.join(' ') : null }
}

/** "$.Services[0].Port" / "Services[0].Port" → "services[0].port". */
function normalizeKey(key: string): string {
  return key
    .replace(/^\$\.?/, '')
    .split('.')
    .map((part) => (part ? part[0].toLowerCase() + part.slice(1) : part))
    .join('.')
}

/** The first error for a field or any of its children (e.g. "tags" matches "tags[2]"). */
export function errorFor(fields: Record<string, string>, field: string): string | undefined {
  if (fields[field]) {
    return fields[field]
  }
  const childKey = Object.keys(fields).find((key) => key.startsWith(`${field}[`) || key.startsWith(`${field}.`))
  return childKey ? fields[childKey] : undefined
}
