/** One identifier the Master Patient Index can match patients on. */
export interface MpiIdentifier {
  /** Stable key persisted in the MPI node's configuration — never rename one once it has shipped. */
  id: string;
  label: string;
  /** What the identifier covers, where the label alone doesn't say. */
  hint?: string;
}

/**
 * The 18 HIPAA Safe Harbor identifiers (45 CFR §164.514(b)(2)(i)(A)–(R)), in the order the rule lists them —
 * the choices the MPI node's identifier picker offers.
 */
export const MPI_IDENTIFIERS: readonly MpiIdentifier[] = [
  { id: 'names', label: 'Names', hint: 'Including initials' },
  { id: 'geographic', label: 'Geographic data smaller than a state', hint: 'Street address, city, county, and ZIP code' },
  {
    id: 'dates',
    label: 'Dates related to an individual (except the year)',
    hint: 'Birth, admission, discharge, and death dates, plus exact ages over 89',
  },
  { id: 'telephone', label: 'Telephone numbers' },
  { id: 'fax', label: 'Fax numbers' },
  { id: 'email', label: 'Email addresses' },
  { id: 'ssn', label: 'Social Security numbers (SSN)' },
  { id: 'mrn', label: 'Medical record numbers' },
  { id: 'health-plan', label: 'Health plan beneficiary numbers' },
  { id: 'account', label: 'Account numbers' },
  { id: 'certificate-license', label: 'Certificate or license numbers' },
  { id: 'vehicle', label: 'Vehicle identifiers and serial numbers', hint: 'Including license plates' },
  { id: 'device', label: 'Device identifiers and serial numbers' },
  { id: 'url', label: 'Web URLs' },
  { id: 'ip-address', label: 'IP (Internet Protocol) addresses' },
  { id: 'biometric', label: 'Biometric identifiers', hint: 'Including finger and voice prints' },
  { id: 'photo', label: 'Full-face photographs and comparable images' },
  { id: 'other-unique', label: 'Any other unique identifying number, characteristic, or code' },
];
