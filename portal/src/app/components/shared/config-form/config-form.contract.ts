import { Type } from '@angular/core';

/**
 * Contract every per-source-type config component implements. Generalizes the pattern
 * GenericFhirSourceFormComponent already used (a viewChild + getFields()) so the parent hosting a
 * dynamically-loaded component (via NgComponentOutlet) can read the form back out without knowing
 * which concrete component is loaded — it only needs this shape.
 */
export interface SourceConfigFormComponent {
  /** Validates the form and returns the flat `fields` bag to store on the node/entity, or null when
   *  validation fails — the caller is responsible for surfacing that as an inline error, same as
   *  today's `genericFhirForm()?.getFields()` call site. */
  getFields(): Record<string, string> | null;
}

/** Contract every per-destination-type config component implements — the destination equivalent of
 *  SourceConfigFormComponent above. */
export interface DestinationConfigFormComponent {
  /** Validates the form and returns the non-secret connection fields plus an optional raw secret
   *  (connection string / password / API key) to provision, or null when validation fails. */
  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null;
}

/** A registry entry pairs the component type with the props every instance is created with, so the
 *  parent's `*ngComponentOutlet` binding stays a one-line lookup regardless of which concrete
 *  component the registry resolves to. */
export type SourceFormRegistry = Partial<Record<string, Type<SourceConfigFormComponent>>>;
export type DestinationFormRegistry<TDestinationType extends string> =
  Partial<Record<TDestinationType, Type<DestinationConfigFormComponent>>>;
