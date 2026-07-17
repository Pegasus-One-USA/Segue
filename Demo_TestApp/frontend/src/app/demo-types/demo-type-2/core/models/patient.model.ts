export interface PatientContact {
  email: string | null;
  phone: string | null;
}

export interface PatientAddress {
  street: string | null;
  city: string | null;
  state: string | null;
  stateAbbreviation: string | null;
  zip: string | null;
  country: string | null;
  validSince: string | null;
}

export interface PatientLanguagePreference {
  language: string | null;
  mode: string | null;
}

export interface PatientDemographics {
  race: string | null;
  ethnicity: string | null;
}

export interface Patient {
  fullName: string;
  firstName: string;
  lastName: string;
  gender: string;
  legalSex: string;
  sexForClinicalUse: string;
  pronouns: string;
  dateOfBirth: string;
  maritalStatus: string;
  active: boolean;
  deceased: boolean;
  contact: PatientContact;
  address: PatientAddress;
  languagePreference: PatientLanguagePreference;
  demographics: PatientDemographics;
  managingOrganization: string;
}
