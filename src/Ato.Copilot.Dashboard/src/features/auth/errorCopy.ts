import type { ErrorClass } from './types';

/**
 * Feature 051 T081 [US4] — canonical user-facing copy for each
 * `ErrorClass` value. Wording aligned with FR-014 (CAC failures),
 * FR-015 (Entra failures), and FR-016 (action-oriented remediation).
 *
 * Per `contracts/frontend-types.md § 2`, the SPA never translates an
 * `errorCode` string itself — it looks up the canned copy from this
 * map. Every entry MUST have non-empty `title`, `body`, and
 * `suggestion`; `LoginErrorPage.test.tsx` asserts the invariant.
 */
export interface ErrorCopy {
  title: string;
  body: string;
  suggestion: string;
}

export const errorCopy: Record<ErrorClass, ErrorCopy> = {
  NoCardInserted: {
    title: 'No CAC/PIV detected',
    body: 'Insert your CAC and try again.',
    suggestion: 'If your card is inserted, ensure your middleware is running.',
  },
  CertExpired: {
    title: 'Certificate expired',
    body: 'Your CAC certificate has expired.',
    suggestion: 'Contact your RA to renew your CAC.',
  },
  CertNotYetValid: {
    title: 'Certificate not yet valid',
    body: 'Your CAC certificate is not yet within its valid date range.',
    suggestion: 'Verify your system clock and contact your RA if the date is correct.',
  },
  CertRevoked: {
    title: 'Certificate revoked',
    body: 'Your CAC certificate has been revoked.',
    suggestion: 'Contact your RA immediately to issue a new certificate.',
  },
  ClockSkew: {
    title: 'Clock skew detected',
    body: "Your computer's clock differs significantly from the server's.",
    suggestion: 'Sync your system time and try again.',
  },
  NoTenantAssignment: {
    title: 'No tenant assignment',
    body: 'Your account is authenticated but is not assigned to a tenant in this deployment.',
    suggestion: 'Contact your administrator to request access.',
  },
  AccountDisabled: {
    title: 'Account disabled',
    body: 'Your Microsoft account is disabled.',
    suggestion: 'Contact your administrator to re-enable your account.',
  },
  MfaFailure: {
    title: 'Multi-factor authentication failed',
    body: 'Your MFA challenge did not complete.',
    suggestion: 'Restart sign-in and complete the MFA prompt.',
  },
  ConditionalAccessBlock: {
    title: 'Conditional Access policy blocked sign-in',
    body: 'A Conditional Access policy prevented this sign-in.',
    suggestion:
      "Follow the link from your organization's IT page, or contact your administrator.",
  },
  NetworkFailure: {
    title: 'Network failure',
    body: 'Could not reach the identity provider.',
    suggestion: 'Check your internet connection and try again.',
  },
};
