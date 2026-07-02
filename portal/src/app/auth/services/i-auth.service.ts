import { Observable } from 'rxjs';
import { User, MessageResponse, TokenPair } from '../models/user.model';
import {
  LoginRequest, LoginResponse,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
} from '../models/auth-request.model';

export abstract class IAuthService {
  abstract login(req: LoginRequest): Observable<LoginResponse>;
  abstract logout(): Observable<void>;
  abstract register(req: RegisterRequest): Observable<RegisterResponse>;
  abstract forgotPassword(req: ForgotPasswordRequest): Observable<MessageResponse>;
  abstract resetPassword(req: ResetPasswordRequest): Observable<MessageResponse>;
  abstract changePassword(req: ChangePasswordRequest): Observable<MessageResponse>;
  abstract getCurrentUser(): Observable<User>;
  abstract refreshToken(refreshToken: string): Observable<TokenPair>;
}
