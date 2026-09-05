// data/node-permission-visibility.util.ts
//
// Shared gate for "is this source/destination node's permission group something a user could
// actually reach in the app today" — used by the RBAC screens (Role & Permissions' Workflow Nodes
// table, the Assign Roles dialog's Role Allocations preview) so they never display a CREATE/DELETE/
// EDIT/EXECUTE/VIEW row for a vendor the Workflow Builder itself won't let anyone add yet.
//
// The real, single source of truth for "is this node visible" is PhaseConfigService — the exact
// same gate node-library-dialog.component.ts (Rank 0/Rank 7 tile visibility), source-connection-list
// (ehrOptions) and destination-connection-list (destination-type picker) already use. This file adds
// no new visibility rule of its own — it only translates a permission code's resource/prefix segment
// (e.g. 'hl7v2', 'meditechgreenfield', 'sqlserver') into the sources.data.ts/transforms.data.ts id
// PhaseConfigService actually gates, reusing the same vendor-name mapping (EHR_VENDOR_TO_SOURCE_FORM_KEY)
// and the same TRANSFORMS catalog those other screens already read.

import { TRANSFORMS } from './transforms.data';
import { EHR_VENDOR_TO_SOURCE_FORM_KEY } from '../components/node-library/source-form.registry';
import { PhaseConfigService } from '../services/phase-config.service';

/** Lowercased vendor name (matches every {prefix}.action PermissionGroupCode already used by
 *  permission-matrix.config.ts's vendor() rows and by Permission.resource on the raw catalog) ->
 *  the sources.data.ts id PhaseConfigService.isSourceEnabled() actually gates. Built from
 *  EHR_VENDOR_TO_SOURCE_FORM_KEY (source-form.registry.ts) — the same vendor-name -> catalog-id
 *  translation node-library-dialog/source-connection-list already use for this exact vendor set,
 *  never re-hand-maintained here. */
const SOURCE_PREFIX_TO_ID: Readonly<Record<string, string>> = Object.fromEntries(
  Object.entries(EHR_VENDOR_TO_SOURCE_FORM_KEY).map(([vendorName, sourceId]) => [vendorName.toLowerCase(), sourceId]),
);

/** Same idea for destination nodes — built straight from transforms.data.ts's own permissionPrefix
 *  field (already exactly the lowercase prefix these permission codes use), so a new destination
 *  type never needs a second registration here, only in transforms.data.ts. 'sourceconnections' is
 *  the generic fallback prefix shared by every destination type with no dedicated PermissionGroupCode
 *  of its own (see transforms.data.ts's own comment) — excluded here since that fallback permission
 *  is never phase-gated node-by-node the way an individual destination type is. */
const DEST_PREFIX_TO_ID: Readonly<Record<string, string>> = Object.fromEntries(
  TRANSFORMS
    .filter(t => t.permissionPrefix && t.permissionPrefix !== 'sourceconnections')
    .map(t => [t.permissionPrefix!, t.id]),
);

/**
 * True if `prefix` (a permission code's resource segment, e.g. 'hl7v2', 'meditechgreenfield',
 * 'sqlserver' — lowercase, matches Permission.resource and permission-matrix.config.ts's vendor()
 * prefix argument) names a source/destination node currently enabled in the active PhaseConfig — i.e.
 * one a user could actually add to a workflow today. Reuses the exact gate
 * node-library-dialog.component.ts/source-connection-list/destination-connection-list already use
 * (phaseCfg.isSourceEnabled/isTransformEnabled), so an RBAC screen never disagrees with what the
 * workflow canvas itself actually offers.
 *
 * A prefix that isn't a source/destination node at all (role, user, workflow, sourceconnections,
 * mappingprofiles, ...) always returns true — there's nothing phase-gated to check, so nothing here
 * is ever hidden for those.
 */
export function isNodePermissionPrefixVisible(prefix: string, phaseConfig: PhaseConfigService): boolean {
  const sourceId = SOURCE_PREFIX_TO_ID[prefix];
  if (sourceId) return phaseConfig.isSourceEnabled(sourceId);
  const destId = DEST_PREFIX_TO_ID[prefix];
  if (destId) return phaseConfig.isTransformEnabled(destId);
  return true;
}
