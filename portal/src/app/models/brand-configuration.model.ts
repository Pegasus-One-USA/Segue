export type BrandThemeMode = 'light' | 'dark' | 'system';

/** Uploaded/linked image assets. Empty string = fall back to the built-in default look. */
export interface BrandAssets {
  logoUrl:              string;
  darkLogoUrl:           string;
  faviconUrl:            string;
  loginBackgroundUrl:    string;
  loginIllustrationUrl:  string;
  emailLogoUrl:          string;
}

export interface BrandConfiguration {
  /** 'default' = FHIRBridge's own branding (no tenant configured). */
  tenantId:         string;
  companyName:      string;
  primaryColor:     string;
  secondaryColor:   string;
  accentColor:      string;
  backgroundColor:  string;
  /** Optional — falls back to the platform's Inter stack when empty. */
  fontFamily:       string;
  footerText:       string;
  supportEmail:     string;
  supportPhone:     string;
  website:          string;
  emailFooterText:  string;
  defaultThemeMode: BrandThemeMode;
  assets:           BrandAssets;
  updatedAt:        string;
}

export const DEFAULT_BRANDING: BrandConfiguration = {
  tenantId:         'default',
  companyName:      'Segue',
  primaryColor:     '#00A89D',
  secondaryColor:   '#0076A8',
  accentColor:      '#007A72',
  backgroundColor:  '#F5F7FA',
  fontFamily:       '',
  footerText:       'Segue Platform',
  supportEmail:     'support@fhirbridge.com',
  supportPhone:     '',
  website:          'https://fhirbridge.com',
  emailFooterText:  'Segue Healthcare Integration Platform',
  defaultThemeMode: 'light',
  assets: {
    logoUrl:             '',
    darkLogoUrl:          '',
    faviconUrl:           'favicon.ico',
    loginBackgroundUrl:   '',
    loginIllustrationUrl: '',
    emailLogoUrl:         '',
  },
  updatedAt: '',
};
