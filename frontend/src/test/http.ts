import { vi } from 'vitest'

export function jsonResponse(status: number, body?: unknown, headers: Record<string, string> = {}): Response {
  const contentType = status >= 400 ? 'application/problem+json' : 'application/json'
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? headers : { 'Content-Type': contentType, ...headers },
  })
}

type Handler = (url: string, init: RequestInit) => Response | Promise<Response>

/** Replaces global fetch with a route table keyed by "METHOD /path". Unmatched calls fail the test. */
export function mockFetch(routes: Record<string, Handler>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = typeof input === 'string' ? input : input.toString()
    const key = `${init.method ?? 'GET'} ${new URL(url, 'http://localhost').pathname}`
    const handler = routes[key]
    if (!handler) {
      throw new Error(`Unexpected request: ${key}`)
    }
    return handler(url, init)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

export const antiforgeryRoute = {
  'GET /api/auth/antiforgery': () => jsonResponse(200, { token: 'csrf-token-1', headerName: 'X-XSRF-TOKEN' }),
}
