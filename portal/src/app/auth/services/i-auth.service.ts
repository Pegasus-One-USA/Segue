import { Observable } from 'rxjs';
import { User, MessageResponse } from '../models/user.model';
import {
  LoginRequest, LoginResponse, LoginResult,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
  MagicLinkRequest, MagicLinkRedeemRequest,
} from '../models/auth-request.model';

export abstract class IAuthService {
  abstract login(req: LoginRequest): Observable<LoginResult>;
  /** Completes a login that returned a challenge (`requiresMfa: true`) via login(). `rememberMe` must
   *  be the same value passed to the original login() call — no session exists yet at that point to
   *  store it against, so the caller (AuthService.completeMfaLogin) is responsible for resending it. */
  abstract verifyMfaLogin(challengeToken: string, code: string, rememberMe?: boolean): Observable<LoginResponse>;
  abstract logout(): Observable<void>;
  abstract register(req: RegisterRequest): Observable<RegisterResponse>;
  abstract forgotPassword(req: ForgotPasswordRequest): Observable<MessageResponse>;
  abstract resetPassword(req: ResetPasswordRequest): Observable<MessageResponse>;
  abstract changePassword(req: ChangePasswordRequest): Observable<MessageResponse>;
  abstract getCurrentUser(): Observable<User>;
  /** HIPAA #7: the refresh token lives in an HttpOnly cookie sent automatically — no parameter needed. */
  abstract refreshToken(): Observable<User>;
  /** Requests a passwordless "sign-in link" email. Always reports success (no user enumeration). */
  abstract requestMagicLink(req: MagicLinkRequest): Observable<MessageResponse>;
  /** Redeems a magic-link token — same MFA-challenge-or-session shape as login(). */
  abstract redeemMagicLink(req: MagicLinkRedeemRequest): Observable<LoginResult>;
}
