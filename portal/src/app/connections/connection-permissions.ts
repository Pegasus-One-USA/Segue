import { SourceConnectionModel } from '../source-connections/models/source-connection.model';
import { ConnectionKindCard } from './connection-row.model';
import { WriteVendorCard, writeVendorCard, writeVendorCards } from './ehr-write-vendors';

/** Delete requires BOTH the generic sourceconnections.delete AND the row's own vendor-specific `{vendor}.delete` —
 *  SourceConnectionsController.cs's DELETE action checks both. A connection that can write (Write, or Read & Write)
 *  is an EHR write connection too, so the server also requires ehrwriteback.delete for it. Shared by the EHR read
 *  and EHR write kinds, so the server's rule lives in one place. */
export function sourceConnectionDeleteCodes(c: Pick<SourceConnectionModel, 'sourceSystemType' | 'access'>): string[] {
  const codes = ['sourceconnections.delete', `${c.sourceSystemType.toLowerCase()}.delete`];
  if (c.access === 'Write' || c.access === 'ReadWrite') codes.push('ehrwriteback.delete');
  return codes;
}

/** `{vendor}.edit` for a row, e.g. `epic.edit` — the code the backend authorizes PUT /source-connections/{id}
 *  against, never the generic `sourceconnections.edit` fallback. */
export function sourceConnectionVendorEditCode(c: Pick<SourceConnectionModel, 'sourceSystemType'>): string {
  return `${c.sourceSystemType.toLowerCase()}.edit`;
}

/** Creating an EHR write connection needs ehrwriteback.create AND the vendor's own `{prefix}.create` (the server checks
 *  both). The one place this rule lives: the Destination Connections list, the on-the-go create component and the
 *  write-back destination's connection picker all ask here. Listing the new connection afterwards is a separate right
 *  that each caller checks for its own list. */
export function writeConnectionCreateCodes(vendor: string): string[] {
  const prefix = writeVendorCard(vendor)?.permissionPrefix ?? vendor.toLowerCase();
  return ['ehrwriteback.create', `${prefix}.create`];
}

/** The write vendors the role may create a connection for, in card order; only `vendor`'s card when one is given. */
export function creatableWriteVendorCards(
  hasPermission: (code: string) => boolean,
  vendor: string | null = null,
): WriteVendorCard[] {
  return writeVendorCards()
    .filter(card => vendor === null || card.value === vendor)
    .filter(card => writeConnectionCreateCodes(card.value).every(code => hasPermission(code)));
}

/** A write vendor as a card in a "New" picker. */
export function toConnectionKindCard(card: WriteVendorCard): ConnectionKindCard {
  return { kind: 'ehr-write', value: card.value, label: card.label, sub: card.sub, abbr: card.abbr, color: card.color };
}
