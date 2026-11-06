import { waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../test/http'
import { installErrorReporting } from './errorReporting'

installErrorReporting()

function throwInPage(error: unknown, message = 'failed') {
  window.dispatchEvent(new ErrorEvent('error', { error, message }))
}

describe('error reporting', () => {
  it('sends an unhandled error once, with where it happened', async () => {
    const bodies: Record<string, unknown>[] = []
    mockFetch({
      ...antiforgeryRoute,
      'POST /api/diagnostics/browser-errors': (_url, init) => {
        bodies.push(JSON.parse(String(init.body)))
        return jsonResponse(204)
      },
    })

    throwInPage(new TypeError('x is undefined'))
    throwInPage(new TypeError('x is undefined'))
    window.dispatchEvent(Object.assign(new Event('unhandledrejection'), { reason: new RangeError('too far') }))

    await waitFor(() => expect(bodies).toHaveLength(2))
    expect(bodies[0]).toMatchObject({ message: 'TypeError: x is undefined', page: window.location.pathname })
    expect(bodies[0].stack).toContain('x is undefined')
    expect(bodies[1]).toMatchObject({ message: 'RangeError: too far' })
  })

  it('ignores cancelled requests and stays quiet when the report fails', async () => {
    const fetchMock = mockFetch({ ...antiforgeryRoute, 'POST /api/diagnostics/browser-errors': () => jsonResponse(401) })
    const abort = new DOMException('stopped', 'AbortError')

    throwInPage(abort)
    expect(fetchMock).not.toHaveBeenCalled()

    throwInPage(new Error('reported but refused'))
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/browser-errors'))).toBe(true))
  })
})
