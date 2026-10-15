import { describe, expect, it } from 'vitest'
import { ApiError } from './api'
import { errorFor, toFormErrors } from './formErrors'

describe('toFormErrors', () => {
  it('maps validation errors onto known fields, normalizing JSON paths and casing', () => {
    const error = new ApiError(400, 'Invalid', {
      errors: {
        Name: ['Enter a name.'],
        '$.services[1].Port': ['Port must be between 1 and 65535.'],
        somethingElse: ['Unexpected.'],
      },
    })

    const result = toFormErrors(error, ['name', 'services'])

    expect(result.fields).toEqual({ name: 'Enter a name.', 'services[1].port': 'Port must be between 1 and 65535.' })
    expect(result.message).toBe('Unexpected.')
  })

  it('uses the problem detail for non-validation failures', () => {
    const conflict = new ApiError(409, 'This item was changed or deleted since you opened it.', { status: 409 })

    expect(toFormErrors(conflict, ['name'])).toEqual({ fields: {}, message: 'This item was changed or deleted since you opened it.' })
  })

  it('finds child errors for a parent field', () => {
    expect(errorFor({ 'tags[2]': 'Too long.' }, 'tags')).toBe('Too long.')
    expect(errorFor({}, 'tags')).toBeUndefined()
  })
})
