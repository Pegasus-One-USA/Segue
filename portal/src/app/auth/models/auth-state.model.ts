import { User } from './user.model';

export interface Session {
  userId:       string;
  token:        string;
  refreshToken: string;
  expiresAt:    Date;
  rememberMe:   boolean;
  lastActivity: Date;
}

export interface AuthState {
  currentUser:     User | null;
  session:         Session | null;
  isAuthenticated: boolean;
  isLoading:       boolean;
  error:           string | null;
}

export interface JwtPayload {
  sub:         string;
  email:       string;
  role:        string;
  roles:       string[];
  permissions: string[];
  orgId:       string;
  iat:         number;
  exp:         number;
}
