export type InvitationStatus = 'pending' | 'accepted' | 'expired' | 'cancelled';

export interface Invitation {
  id:               string;
  token:            string;
  email:            string;
  firstName:        string;
  lastName:         string;
  role:             string;
  organizationName: string;
  invitedBy:        string;
  createdAt:        Date;
  expiresAt:        Date;
  status:           InvitationStatus;
  acceptedAt?:      Date;
}

export interface SendInvitationRequest {
  email:     string;
  firstName: string;
  lastName:  string;
  role:      string;
  invitedBy: string;
}

export interface InvitationValidationResult {
  valid:       boolean;
  invitation?: Invitation;
  error?:      'expired' | 'invalid' | 'already_accepted' | 'cancelled';
}
