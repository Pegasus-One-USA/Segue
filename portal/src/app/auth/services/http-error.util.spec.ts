import { HttpErrorResponse } from '@angular/common/http';
import { authErrorMessage } from './http-error.util';

describe('authErrorMessage', () => {
  it('returns the backend\'s "error" field when present (the actual shape UseExceptionHandler sends)', () => {
    const err = new HttpErrorResponse({ status: 400, error: { error: 'Invalid email or password.' } });
    expect(authErrorMessage(err, 'fallback')).toBe('Invalid email or password.');
  });

  it('returns the "message" field when present (the password-change-required gate\'s shape)', () => {
    const err = new HttpErrorResponse({ status: 403, error: { message: 'Password change is required before using FHIRBridge.' } });
    expect(authErrorMessage(err, 'fallback')).toBe('Password change is required before using FHIRBridge.');
  });

  it('returns the "title" field when present (ProblemDetails-style, as a last resort)', () => {
    const err = new HttpErrorResponse({ status: 400, error: { title: 'Invalid email or password.' } });
    expect(authErrorMessage(err, 'fallback')).toBe('Invalid email or password.');
  });

  it('returns a network-specific message for status 0 (server unreachable / CORS / protocol mismatch)', () => {
    const err = new HttpErrorResponse({ status: 0 });
    expect(authErrorMessage(err, 'fallback')).toBe('Unable to reach the server. Check your connection and try again.');
  });

  it('falls back when the error body has none of the known fields', () => {
    const err = new HttpErrorResponse({ status: 500, error: {} });
    expect(authErrorMessage(err, 'fallback')).toBe('fallback');
  });

  it('falls back for a non-HttpErrorResponse value', () => {
    expect(authErrorMessage(new Error('boom'), 'fallback')).toBe('fallback');
    expect(authErrorMessage(null, 'fallback')).toBe('fallback');
  });
});
