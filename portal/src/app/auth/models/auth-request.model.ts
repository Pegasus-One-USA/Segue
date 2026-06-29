export interface LoginRequest {
  email:    string;
  password: string;
}

export interface ForgotPasswordRequest {
  email: string;
}

export interface ResetPasswordRequest {
  token:           string;
  newPassword:     string;
  confirmPassword: string;
}
