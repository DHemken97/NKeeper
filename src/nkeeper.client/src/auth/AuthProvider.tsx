import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, session, UNAUTHORIZED_EVENT } from '../lib/api.ts';
import type { AuthDriverType, LoginResponse, UserInfo } from '../lib/types.ts';
import { AuthContext, type AuthContextValue, type SignInResult } from './AuthContext.ts';

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserInfo | null>(null);
  const [loading, setLoading] = useState(() => session.get() !== null);

  const refreshUser = useCallback(async () => {
    setUser(await api<UserInfo>('/api/auth/me'));
  }, []);

  // Restore a session that survived a page reload.
  useEffect(() => {
    if (!session.get()) return;
    api<UserInfo>('/api/auth/me')
      .then(setUser)
      .catch(() => session.clear())
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    const onUnauthorized = () => setUser(null);
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, []);

  const acceptSession = useCallback((response: LoginResponse) => {
    session.set({ accessToken: response.accessToken!, expiresAtUtc: response.expiresAtUtc! });
    setUser(response.user);
  }, []);

  const signIn = useCallback(
    async (username: string, password: string, driver: AuthDriverType): Promise<SignInResult> => {
      const response = await api<LoginResponse>('/api/auth/login', {
        method: 'POST',
        body: { username, password, driver },
      });
      if (response.status === 'MfaRequired') {
        return { status: 'MfaRequired', mfaToken: response.mfaToken!, methods: response.mfaMethods ?? [] };
      }
      acceptSession(response);
      return { status: 'Authenticated' };
    },
    [acceptSession],
  );

  const verifyTotp = useCallback(
    async (mfaToken: string, code: string) => {
      const response = await api<LoginResponse>('/api/auth/mfa/verify-totp', {
        method: 'POST',
        body: { mfaToken, code },
      });
      acceptSession(response);
    },
    [acceptSession],
  );

  const signOut = useCallback(() => {
    session.clear();
    setUser(null);
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({ user, loading, signIn, verifyTotp, signOut, refreshUser }),
    [user, loading, signIn, verifyTotp, signOut, refreshUser],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
