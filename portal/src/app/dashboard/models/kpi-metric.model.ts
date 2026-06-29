export type KpiAccent = 'blue' | 'green' | 'amber' | 'red';

export interface KpiTrend {
  direction: 'up' | 'down' | 'flat';
  value:     string;
}

export interface KpiMetric {
  id:     string;
  label:  string;
  value:  number | string;
  unit?:  string;
  trend?: KpiTrend;
  icon:   string;
  accent: KpiAccent;
}
