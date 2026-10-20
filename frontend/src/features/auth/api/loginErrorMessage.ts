import { ApiError } from '../../../lib/api'

/** Maps a failed login request to a message that is safe and useful to show the user. */
export function loginErrorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return 'Something went wrong. Please try again.'
  }

  if (error.isNetworkError) {
    return 'Unable to reach the server. Check your connection and try again.'
  }

  if (error.code === 'email_unavailable') {
    return 'Your account needs a sign-in code sent by email, and it couldn’t be sent. Ask an administrator to check the email settings.'
  }

  switch (error.status) {
    case 400:
      return 'Please check the details you entered and try again.'
    case 401:
      return 'Incorrect username or password.'
    case 429:
      return error.retryAfterSeconds
        ? `Too many sign-in attempts. Try again in ${error.retryAfterSeconds} seconds.`
        : 'Too many sign-in attempts. Please wait a moment and try again.'
    default:
      return error.status >= 500
        ? 'The server encountered a problem. Please try again shortly.'
        : 'Sign-in failed. Please try again.'
  }
}
