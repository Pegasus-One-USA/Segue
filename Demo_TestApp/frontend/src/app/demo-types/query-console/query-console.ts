import { Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const BACKEND_BASE_URL = environment.healthAppBase;

type Row = Record<string, unknown>;

/** Matches Demo_TestApp's POST /api/query-console/run response. */
interface QueryResult {
  columns: string[];
  rows: Row[];
  rowCount: number;
  truncated: boolean;
  maxRows: number;
}

const DEFAULT_QUERY = 'SELECT TOP 50 * FROM Patients_11';

/** Admin-only screen: type a read-only SQL query, run it against the HealthApp database, render the rows as a
 *  table, and open any row in a popup as a vertical (field / value) form. */
@Component({
  selector: 'app-query-console',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './query-console.html',
  styleUrl: './query-console.scss',
})
export class QueryConsoleComponent {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  readonly sql = signal(DEFAULT_QUERY);
  readonly isRunning = signal(false);
  readonly error = signal('');
  readonly result = signal<QueryResult | null>(null);
  readonly selectedRow = signal<Row | null>(null);

  constructor(private readonly http: HttpClient) {}

  async run(): Promise<void> {
    if (this.isRunning() || !this.sql().trim()) {
      return;
    }
    this.isRunning.set(true);
    this.error.set('');
    this.selectedRow.set(null);
    try {
      this.result.set(
        await firstValueFrom(
          this.http.post<QueryResult>(
            `${BACKEND_BASE_URL}/api/query-console/run`,
            { sql: this.sql() },
            { withCredentials: true }
          )
        )
      );
    } catch (err) {
      this.result.set(null);
      this.error.set(this.toMessage(err));
    } finally {
      this.isRunning.set(false);
    }
  }

  openRow(row: Row): void {
    this.selectedRow.set(row);
  }

  closeRow(): void {
    this.selectedRow.set(null);
  }

  /** Display text for a cell. Null stays visibly null; objects (e.g. JSON columns) are stringified. */
  format(value: unknown): string {
    if (value === null || value === undefined) {
      return 'NULL';
    }
    return typeof value === 'object' ? JSON.stringify(value) : String(value);
  }

  isNull(value: unknown): boolean {
    return value === null || value === undefined;
  }

  private toMessage(err: unknown): string {
    if (err instanceof HttpErrorResponse) {
      if (err.status === 401) return 'Your session has expired. Please log in again.';
      if (err.status === 403) return 'Only Admin accounts can use the Query Console.';
      const body = err.error as { error?: string } | null;
      if (body?.error) return body.error;
    }
    return 'The query could not be run.';
  }
}
