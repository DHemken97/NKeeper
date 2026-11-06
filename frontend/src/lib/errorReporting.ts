import { api } from './api'

const MAX_REPORTS_PER_PAGE_LOAD = 20
const MAX_MESSAGE = 2000
const MAX_STACK = 8000

const reported = new Set<string>()

function report(error: unknown, fallback: string) {
  const isError = error instanceof Error || error instanceof DOMException
  const message = (isError ? `${error.name}: ${error.message}` : fallback).slice(0, MAX_MESSAGE)
  // A cancelled request isn't an error, and this browser message is noise rather than a bug.
  if (reported.has(message) || reported.size >= MAX_REPORTS_PER_PAGE_LOAD || (isError && error.name === 'AbortError') || message.includes('ResizeObserver loop')) {
    return
  }
  reported.add(message)
  // Signed out, or the server gone: there's nothing to do about it, and this mustn't sign anyone out or report itself.
  void api
    .post('/api/diagnostics/browser-errors', {
      body: { message, stack: isError ? error.stack?.slice(0, MAX_STACK) : undefined, page: window.location.pathname.slice(0, 500) },
      suppressUnauthorizedEvent: true,
    })
    .catch(() => {})
}

/** Sends errors nothing handled to the server's diagnostics file (Settings ▸ Diagnostics). React passes on what crashes a render as well. */
export function installErrorReporting() {
  window.addEventListener('error', (event) => report(event.error, event.message))
  window.addEventListener('unhandledrejection', (event) => report(event.reason, 'A promise was rejected.'))
}
