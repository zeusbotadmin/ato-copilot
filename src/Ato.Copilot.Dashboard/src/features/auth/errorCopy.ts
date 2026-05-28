import type { ErrorClass } from './types';

/**
 * Feature 051 T048 [US1] — stub map of `ErrorClass` → user-facing copy.
 *
 * US4 (Phase 6) fills the canonical wording; for now we ship safe
 * placeholders so the error page can render every `ErrorClass` value the
 * SPA might receive from the server.
 *
 * Per `contracts/frontend-types.md § 2`, the SPA never translates an
 * `errorCode` string itself — it looks up the canned copy from this map.
 */
export interface ErrorCopy {
  title: string;
  body: string;
  suggestion: string;
}

export const errorCopy: Record<ErrorClass, ErrorCopy> = {
  NoCardInserted: {
    title: 'No CAC/PIV card detected',
    body: 'We could not read a CAC or PIV card from your reader.',
    suggestion: 'Insert your card, enter your PIN, and try again.',
  },
  CertExpired: {
    title: 'Certificate expired',
    body: 'The certificate on your CAC/PIV card has expired.',
    suggestion: 'Visit your local RAPIDS office to update your card.',
  },
  CertNotYetValid: {
    title: 'Certificate not yet valid',
    body: 'The certificate on your card is not valid yet.',
    suggestion: 'Confirm your system clock is correct and try again later.',
  },
  CertRevoked: {
    title: 'Certificate revoked',
    body: 'Your CAC/PIV certificate has been revoked.',
    suggestion: 'Contact your security officer to be reissued a card.',
  },
  ClockSkew: {
    title: 'System clock out of sync',
    body: 'Your computer\'s clock is too far off to authenticate securely.',
    suggestion: 'Sync your system clock and try again.',
  },
  NoTenantAssignment: {
    title: 'No tenant assigned',
    body: 'Your account is authenticated but has no tenant in this deployment.',
    suggestion: 'Contact your administrator to be added to a tenant.',
  },
  AccountDisabled: {
    title: 'Account disabled',
    body: 'Your account is disabled in your organization\'s directory.',
    suggestion: 'Contact your administrator to re-enable your account.',
  },
  MfaFailure: {
    title: 'Multi-factor challenge failed',
    body: 'Multi-factor authentication did not complete.',
    suggestion: 'Try again, or contact your administrator if the issue persists.',
  },
  ConditionalAccessBlock: {
    title: 'Blocked by Conditional Access',
    body: 'A Conditional Access policy blocked this sign-in.',
    suggestion: 'Check that you are on a compliant device and trusted network.',
  },
  NetworkFailure: {
    title: 'Network problem',
    body: 'We could not reach the identity provider.',
    suggestion: 'Check your connection and try again.',
  },
};
