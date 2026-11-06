/**
 * Centralized HTTP client for the NKeeper API.
 *
 * - Authentication uses an HttpOnly cookie managed by the server; nothing secret is stored in JS.
 * - Unsafe requests carry an antiforgery token in a header. The token is kept in memory only,
 *   fetched lazily, and refreshed once automatically if the server reports it as invalid.
 * - Every failure is surfaced as an {@link ApiError}, including network failures (status 0).
 */

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/+$/, '')
const CSRF_ERROR_CODE = 'csrf_token_invalid'

export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  code?: string
  errors?: Record<string, string[]>
  traceId?: string
}

export class ApiError extends Error {
  readonly status: number
  readonly problem?: ProblemDetails
  readonly retryAfterSeconds?: number

  constructor(status: number, message: string, problem?: ProblemDetails, retryAfterSeconds?: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
    this.retryAfterSeconds = retryAfterSeconds
  }

  get code(): string | undefined {
    return this.problem?.code
  }

  get isNetworkError(): boolean {
    return this.status === 0
  }
}

type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

export interface RequestOptions {
  /** JSON-serialized, except FormData (sent as multipart, e.g. file uploads). */
  body?: unknown
  /** 'blob' returns the response body as a Blob (file downloads), and 'stream' the Response itself to read as it arrives, instead of parsing JSON. */
  responseType?: 'json' | 'blob' | 'stream'
  signal?: AbortSignal
  /** Do not notify unauthorized listeners on 401 (e.g. a failed login is not an expired session). */
  suppressUnauthorizedEvent?: boolean
}

interface AntiforgeryToken {
  token: string
  headerName: string
}

let antiforgeryToken: Promise<AntiforgeryToken> | null = null
/** The token once it has arrived, for requests that can't wait for it (as the page closes). */
let currentAntiforgeryToken: AntiforgeryToken | null = null
const unauthorizedListeners = new Set<() => void>()

/** Antiforgery tokens are bound to the signed-in user, so call this after login and logout. */
export function resetAntiforgeryToken(): void {
  antiforgeryToken = null
  currentAntiforgeryToken = null
}

/** Subscribe to 401 responses (session expired or revoked). Returns an unsubscribe function. */
export function onUnauthorized(listener: () => void): () => void {
  unauthorizedListeners.add(listener)
  return () => unauthorizedListeners.delete(listener)
}

function getAntiforgeryToken(): Promise<AntiforgeryToken> {
  antiforgeryToken ??= send<AntiforgeryToken>('GET', '/api/auth/antiforgery', {}).then(
    (token) => {
      currentAntiforgeryToken = token
      return token
    },
    (error: unknown) => {
      antiforgeryToken = null
      throw error
    },
  )
  return antiforgeryToken
}

async function send<T>(method: HttpMethod, path: string, options: RequestOptions): Promise<T> {
  const headers: Record<string, string> = { Accept: options.responseType === 'blob' ? '*/*' : 'application/json' }
  const form = options.body instanceof FormData
  if (options.body !== undefined && !form) {
    headers['Content-Type'] = 'application/json'
  }
  if (method !== 'GET') {
    const { token, headerName } = await getAntiforgeryToken()
    headers[headerName] = token
  }

  let response: Response
  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      method,
      headers,
      // The browser sets the multipart Content-Type (with its boundary) for FormData.
      body: options.body === undefined ? undefined : form ? (options.body as FormData) : JSON.stringify(options.body),
      credentials: 'include',
      signal: options.signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }
    throw new ApiError(0, 'Unable to reach the server. Check your connection and try again.')
  }

  if (response.ok) {
    if (options.responseType === 'stream') {
      return response as T
    }
    if (options.responseType === 'blob') {
      return (await response.blob()) as T
    }
    return (response.status === 204 ? undefined : await response.json()) as T
  }

  const problem = await readProblem(response)
  if (response.status === 401 && !options.suppressUnauthorizedEvent) {
    unauthorizedListeners.forEach((listener) => listener())
  }

  const retryAfter = Number.parseInt(response.headers.get('Retry-After') ?? '', 10)
  throw new ApiError(
    response.status,
    problem?.detail ?? problem?.title ?? `Request failed with status ${response.status}.`,
    problem,
    Number.isFinite(retryAfter) ? retryAfter : undefined,
  )
}

async function readProblem(response: Response): Promise<ProblemDetails | undefined> {
  const contentType = response.headers.get('Content-Type') ?? ''
  if (!contentType.includes('json')) {
    return undefined
  }
  try {
    return (await response.json()) as ProblemDetails
  } catch {
    return undefined
  }
}

async function request<T>(method: HttpMethod, path: string, options: RequestOptions = {}): Promise<T> {
  try {
    return await send<T>(method, path, options)
  } catch (error) {
    // A stale antiforgery token (e.g. after the session changed in another tab): refresh and retry once.
    if (method !== 'GET' && error instanceof ApiError && error.status === 400 && error.code === CSRF_ERROR_CODE) {
      resetAntiforgeryToken()
      return send<T>(method, path, options)
    }
    throw error
  }
}

/** Hands over each line of JSON in a response as it arrives. */
export async function readJsonLines<T>(response: Response, onLine: (line: T) => void): Promise<void> {
  const reader = response.body!.pipeThrough(new TextDecoderStream()).getReader()
  let text = ''
  for (let next = await reader.read(); !next.done; next = await reader.read()) {
    const lines = (text + next.value).split('\n')
    text = lines.pop()!
    lines.filter(Boolean).forEach((line) => onLine(JSON.parse(line) as T))
  }
}

/** URL for an API path the browser navigates to (rather than fetches), on the same base as requests. */
export function apiUrl(path: string): string {
  return `${API_BASE_URL}${path}`
}

/** Absolute ws(s):// URL for an API path, on the same base as HTTP requests. */
export function apiWebSocketUrl(path: string): string {
  const url = new URL(`${API_BASE_URL}${path}`, window.location.origin)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return url.toString()
}

/**
 * Sends a DELETE while the page is closing: started at once and kept going after the page is gone
 * (fetch keepalive), with the antiforgery token already held. Nothing to wait for or handle.
 */
export function deleteOnPageExit(path: string): void {
  const headers: Record<string, string> = {}
  if (currentAntiforgeryToken) {
    headers[currentAntiforgeryToken.headerName] = currentAntiforgeryToken.token
  }
  try {
    void fetch(`${API_BASE_URL}${path}`, { method: 'DELETE', headers, credentials: 'include', keepalive: true }).catch(() => {})
  } catch {
    // The page is going away; the server closes what it doesn't hear from.
  }
}

export const api = {
  get: <T>(path: string, options?: RequestOptions) => request<T>('GET', path, options),
  post: <T>(path: string, options?: RequestOptions) => request<T>('POST', path, options),
  put: <T>(path: string, options?: RequestOptions) => request<T>('PUT', path, options),
  delete: <T = void>(path: string, options?: RequestOptions) => request<T>('DELETE', path, options),
}
