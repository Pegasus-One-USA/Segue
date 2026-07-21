/**
 * Centralized, status-driven fallback text for HTTP errors that carry no safe backend-supplied
 * message. Used by httpErrorSanitizerInterceptor so every part of the app reads friendly text
 * instead of Angular's synthetic "Http failure response for <url>: <status> <text>" string.
 */
export const HTTP_STATUS_FALLBACK_MESSAGE: Readonly<Record<number, string>> = {
  0: 'Unable to connect to the server. Check your connection and try again.',
  400: 'Please check the information you entered.',
  401: 'Your session has expired. Please sign in again.',
  403: "You don't have permission to perform this action.",
  404: 'The requested resource could not be found.',
  405: 'This action is not supported.',
  408: 'The request timed out. Please try again.',
  409: 'This record already exists.',
  412: 'This item changed since you loaded it. Please refresh and try again.',
  413: 'The uploaded file exceeds the maximum allowed size.',
  415: 'This file type is not supported.',
  422: 'Please correct the highlighted fields.',
  423: 'Your account has been locked. Please contact your administrator.',
  429: 'Too many requests. Please wait a moment and try again.',
  500: 'Something went wrong. Please try again later.',
  501: "This feature isn't available yet.",
  502: 'The service is temporarily unavailable. Please try again shortly.',
  503: 'The service is temporarily unavailable. Please try again shortly.',
  504: 'The request timed out. Please try again.',
};

export const GENERIC_ERROR_MESSAGE = 'Something went wrong. Please try again later.';

export function resolveFallbackMessage(status: number): string {
  return HTTP_STATUS_FALLBACK_MESSAGE[status] ?? GENERIC_ERROR_MESSAGE;
}
