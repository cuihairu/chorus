import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { whoAmI, type WhoAmIReply } from './api';

// The operator session (FIX-WEB-04). The credential is the bearer token issued through
// the control plane's operator store; the console keeps it in sessionStorage, resolves it
// against who-am-i, and stays signed in until the control plane refuses it — a disabled
// or rotated credential falls back to the login page on the next probe, never on a
// cached identity.
const tokenStorageKey = 'chorus.operator-token';

interface AuthState {
  token: string | null;
  operator: WhoAmIReply | null;
  /** True while a stored token is being re-resolved against the control plane. */
  restoring: boolean;
  login: (token: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

function readStoredToken(): string | null {
  try {
    return sessionStorage.getItem(tokenStorageKey);
  } catch {
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(readStoredToken);
  const [operator, setOperator] = useState<WhoAmIReply | null>(null);
  const [restoring, setRestoring] = useState(token !== null);
  const restored = useRef(false);

  // Restore a stored session by re-probing who-am-i once: the token is only as valid
  // as the control plane's current answer about it. (StrictMode double-invokes effects;
  // the ref is reset in cleanup so the retried run still probes.)
  useEffect(() => {
    if (token === null || restored.current || operator !== null) return;
    restored.current = true;
    let cancelled = false;
    whoAmI(token)
      .then((identity) => {
        if (!cancelled) {
          setOperator(identity);
          setRestoring(false);
        }
      })
      .catch(() => {
        if (!cancelled) {
          sessionStorage.removeItem(tokenStorageKey);
          setToken(null);
          setRestoring(false);
        }
      });
    return () => {
      cancelled = true;
      restored.current = false;
    };
  }, [token, operator]);

  const login = useCallback(async (candidate: string) => {
    const identity = await whoAmI(candidate);
    sessionStorage.setItem(tokenStorageKey, candidate);
    setToken(candidate);
    setOperator(identity);
  }, []);

  const logout = useCallback(() => {
    sessionStorage.removeItem(tokenStorageKey);
    setToken(null);
    setOperator(null);
  }, []);

  const value = useMemo(
    () => ({ token, operator, restoring, login, logout }),
    [token, operator, restoring, login, logout],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const state = useContext(AuthContext);
  if (state === null) {
    throw new Error('useAuth must be used inside AuthProvider');
  }
  return state;
}
