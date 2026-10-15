/** Router state carried to /login so the user returns to the page they asked for. */
export interface RedirectState {
  from?: string
}

/** Only allow in-app relative paths, never protocol-relative or absolute URLs. */
export function safeRedirect(path: string | undefined): string {
  return path && path.startsWith('/') && !path.startsWith('//') ? path : '/'
}
