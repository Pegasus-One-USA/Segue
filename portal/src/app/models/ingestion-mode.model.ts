export interface IngestionMode {
  id: string;
  name: string;
  acid?: boolean;
  sub: string;
}

export interface IngestionGate {
  allowed: string[];
  default: string;
}
