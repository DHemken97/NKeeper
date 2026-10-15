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
  body?: unknown
  signal?: AbortSignal
  /** Do not notify unauthorized listeners on 401 (e.g. a failed login is not an expired session). */
  suppressUnauthorizedEvent?: boolean
}

interface AntiforgeryToken {
  token: string
  headerName: string
}

let antiforgeryToken: Promise<AntiforgeryToken> | null = null
const unauthorizedListeners = new Set<() => void>()

/** Antiforgery tokens are bound to the signed-in user, so call this after login and logout. */
export function resetAntiforgeryToken(): void {
  antiforgeryToken = null
}

/** Subscribe to 401 responses (session expired or revoked). Returns an unsubscribe function. */
export function onUnauthorized(listener: () => void): () => void {
  unauthorizedListeners.add(listener)
  return () => unauthorizedListeners.delete(listener)
}

function getAntiforgeryToken(): Promise<AntiforgeryToken> {
  antiforgeryToken ??= send<AntiforgeryToken>('GET', '/api/auth/antiforgery', {}).catch((error: unknown) => {
    antiforgeryToken = null
    throw error
  })
  return antiforgeryToken
}

async function send<T>(method: HttpMethod, path: string, options: RequestOptions): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) {
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
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
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

export const api = {
  get: <T>(path: string, options?: RequestOptions) => request<T>('GET', path, options),
  post: <T>(path: string, options?: RequestOptions) => request<T>('POST', path, options),
}
