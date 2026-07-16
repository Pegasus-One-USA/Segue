import { Patient } from '../models/patient.model';

export const MOCK_PATIENT: Patient = {
  fullName: 'Omar Optime',
  firstName: 'Omar',
  lastName: 'Optime',
  gender: 'Male',
  legalSex: 'Male',
  sexForClinicalUse: 'Male',
  pronouns: 'He / Him / His',
  dateOfBirth: '1975-11-03',
  maritalStatus: 'Single',
  active: true,
  deceased: false,
  contact: {
    email: 'email@email.com',
    phone: null
  },
  address: {
    street: '1979 Milky Way',
    city: 'Verona',
    state: 'Wisconsin',
    stateAbbreviation: 'WI',
    zip: '53726',
    country: 'United States',
    validSince: '2013-11-15'
  },
  languagePreference: {
    language: 'English',
    mode: 'Spoken'
  },
  demographics: {
    race: 'White',
    ethnicity: 'Unknown'
  },
  managingOrganization: 'Epic Hospital System'
};
