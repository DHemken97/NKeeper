import { describe, expect, it } from 'vitest'
import { antiforgeryRoute, jsonResponse, mockFetch } from '../test/http'
import { api, ApiError } from './api'

describe('api client', () => {
  it('sends credentials and the antiforgery header on POST requests', async () => {
    const fetchMock = mockFetch({
      ...antiforgeryRoute,
      'POST /api/thing': () => jsonResponse(200, { ok: true }),
    })

    await expect(api.post('/api/thing', { body: { a: 1 } })).resolves.toEqual({ ok: true })

    const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/api/thing'))
    const init = call?.[1] ?? {}
    expect(init.credentials).toBe('include')
    expect((init.headers as Record<string, string>)['X-XSRF-TOKEN']).toBe('csrf-token-1')
  })

  it('refreshes the antiforgery token and retries once when the server rejects it', async () => {
    let attempts = 0
    let tokenRequests = 0
    mockFetch({
      'GET /api/auth/antiforgery': () =>
        jsonResponse(200, { token: `csrf-token-${++tokenRequests}`, headerName: 'X-XSRF-TOKEN' }),
      'POST /api/thing': (_url, init) => {
        attempts++
        const token = (init.headers as Record<string, string>)['X-XSRF-TOKEN']
        return token === 'csrf-token-2'
          ? jsonResponse(204)
          : jsonResponse(400, { status: 400, code: 'csrf_token_invalid' })
      },
    })

    await expect(api.post('/api/thing')).resolves.toBeUndefined()
    expect(attempts).toBe(2)
    expect(tokenRequests).toBe(2)
  })

  it('turns problem details into an ApiError', async () => {
    mockFetch({
      'GET /api/thing': () =>
        jsonResponse(429, { status: 429, detail: 'Slow down.', code: 'rate_limited' }, { 'Retry-After': '30' }),
    })

    const error = await api.get('/api/thing').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 429, message: 'Slow down.', code: 'rate_limited', retryAfterSeconds: 30 })
  })

  it('reports network failures as status 0', async () => {
    mockFetch({
      'GET /api/thing': () => {
        throw new TypeError('Failed to fetch')
      },
    })

    const error = await api.get('/api/thing').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).isNetworkError).toBe(true)
  })
})
