import { Component, Type, forwardRef, output } from '@angular/core';
import { Observable, of } from 'rxjs';
import {
  CONNECTION_KIND_HOST,
  ConnectionActionId,
  ConnectionKind,
  ConnectionKindCard,
  ConnectionKindHost,
  ConnectionRow,
  ConnectionRowAction,
} from '../connection-row.model';

/** Test-only: what a fake kind answers. Specs set it per kind before creating the page. */
export interface FakeKindConfig {
  canList: boolean;
  list: () => Observable<ConnectionRow[]>;
  cards: ConnectionKindCard[];
  actions: ConnectionRowAction[];
  listCalls: number;
  ran: { id: ConnectionActionId; row: ConnectionRow }[];
  created: ConnectionKindCard[];
  shown: ConnectionRow[][];
}

export const fakeKinds: Partial<Record<ConnectionKind, FakeKindConfig>> = {};

export function configureFakeKind(kind: ConnectionKind, rows: ConnectionRow[], overrides: Partial<FakeKindConfig> = {}): FakeKindConfig {
  const config: FakeKindConfig = {
    canList: true, list: () => of(rows), cards: [], actions: [], listCalls: 0, ran: [], created: [], shown: [], ...overrides,
  };
  fakeKinds[kind] = config;
  return config;
}

export function fakeRow(kind: ConnectionKind, id: string, name: string, extra: Partial<ConnectionRow> = {}): ConnectionRow {
  return {
    kind, key: `${kind}:${id}`, id, name, typeLabel: kind, filterKeys: [`kind:${kind}`], audience: null, address: null,
    clientId: null, writeApis: null, isEnabled: true, actionBy: null, actionOn: null, raw: null, ...extra,
  };
}

/** A stand-in kind component with the real one's selector: same host contract, answers from fakeKinds. */
export function fakeKindComponent(kind: ConnectionKind, selector: string): Type<ConnectionKindHost & { changed: { emit(): void } }> {
  @Component({
    selector,
    standalone: true,
    template: '',
    providers: [{ provide: CONNECTION_KIND_HOST, useExisting: forwardRef(() => FakeKindComponent) }],
  })
  class FakeKindComponent implements ConnectionKindHost {
    readonly kind = kind;
    readonly loadErrorMessage = `${kind} failed`;
    readonly changed = output<void>();
    private get config(): FakeKindConfig {
      return fakeKinds[kind]!;
    }
    canList(): boolean {
      return this.config.canList;
    }
    list(): Observable<ConnectionRow[]> {
      this.config.listCalls++;
      return this.config.list();
    }
    createCards(): ConnectionKindCard[] {
      return this.config.cards;
    }
    create(card: ConnectionKindCard): boolean {
      this.config.created.push(card);
      return true;
    }
    rowActions(): ConnectionRowAction[] {
      return this.config.actions;
    }
    runAction(id: ConnectionActionId, row: ConnectionRow): void {
      this.config.ran.push({ id, row });
    }
    rowsShown(rows: ConnectionRow[]): void {
      this.config.shown.push(rows);
    }
  }
  return FakeKindComponent;
}
