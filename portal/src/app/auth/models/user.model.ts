export type UserRole = 'admin' | 'analyst' | 'pipeline-editor' | 'viewer';

export interface User {
  id:     string;
  email:  string;
  name:   string;
  role:   UserRole;
  orgId?: string;
}
