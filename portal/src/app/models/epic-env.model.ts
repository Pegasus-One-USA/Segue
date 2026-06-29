export type EnvKey = 'sandbox' | 'production';

export interface EpicEnvironment {
  label: string;
  base: string;
  smartConfig: string;
  token: string;
  authorize: string;
}
