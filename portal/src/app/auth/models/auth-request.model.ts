import { User, UserRole, LoginType, UserStatus } from './user.model';

export interface LoginRequest {
  email:      string;
  password:   string;
  rememberMe?: boolean;
}

export interface LoginResponse {
  accessToken:  string;
  refreshToken: string;
  expiresIn:    number;
  user:         User;
}

export interface RegisterRequest {
  firstName:   string;
  lastName:    string;
  email:       string;
  password:    string;
  role?:       UserRole;
  acceptTerms: boolean;
}

export interface RegisterResponse {
  user:         User;
  accessToken:  string;
  refreshToken: string;
}

export interface ForgotPasswordRequest {
  email: string;
}

export interface ResetPasswordRequest {
  token:           string;
  newPassword:     string;
  confirmPassword: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword:     string;
  confirmPassword: string;
}

export interface InviteUserRequest {
  email:       string;
  firstName:   string;
  lastName:    string;
  role:        UserRole;
  /** Backend role id (guid). When present it is sent verbatim as `roleId`. */
  roleId?:     string;
  department?: string;
}

export interface CreateUserRequest {
  firstName:   string;
  lastName:    string;
  email:       string;
  role:        UserRole;
  department?: string;
  jobTitle?:   string;
  loginType:   LoginType;
  status:      UserStatus;
  sendInvite?: boolean;
}

export interface UpdateUserRequest {
  firstName?:  string;
  lastName?:   string;
  department?: string;
  jobTitle?:   string;
  phone?:      string;
  role?:       UserRole;
  status?:     UserStatus;
}

export interface CreateRoleRequest {
  name:          string;
  description:   string;
  permissionIds: string[];
}

export interface UpdateRoleRequest {
  name:          string;
  description:   string;
  permissionIds: string[];
}
