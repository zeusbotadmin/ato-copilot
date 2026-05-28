import { useEffect, type ReactNode } from 'react';
import { useMsal, useIsAuthenticated } from '@azure/msal-react';
import { DEFAULT_API_SCOPES } from './msalInstance';

/**
 * Feature 051 T051 [US1] — gate component for protected routes. When
 * the principal is not authenticated, immediately triggers
 * `instance.loginRedirect({ state: <current path + search> })` to
 * preserve the deep link through Entra and back to
 * `/login/callback`, then renders nothing while the browser navigates
 * away.
 */
export default function RequireAuth({ children }: { children: ReactNode }) {
  const isAuthenticated = useIsAuthenticated();
  const { instance } = useMsal();

  useEffect(() => {
    if (!isAuthenticated) {
      const deepLink =
        window.location.pathname + window.location.search + window.location.hash;
      void instance.loginRedirect({
        scopes: DEFAULT_API_SCOPES,
        state: deepLink,
      });
    }
  }, [isAuthenticated, instance]);

  if (!isAuthenticated) {
    return null;
  }
  return <>{children}</>;
}
