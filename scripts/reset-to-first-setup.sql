-- Reset FHIRBridge to the first-run setup screen.
-- Removes ALL users (and their role assignments) so /auth/setup-status reports
-- requiresSetup = true again. Roles, permissions, and role-permission mappings are
-- kept intact — they are platform reference data (re-provisioned at API startup by
-- RbacBootstrapper anyway), so the new SuperAdmin created via the setup screen still
-- gets the SuperAdmin role with all permissions.
--
-- No API restart needed. After running, open the portal in a fresh/incognito window
-- (so there is no stale JWT) and it will route to the Create-SuperAdmin setup screen.

DELETE FROM [UserRoles];   -- FK -> Users (must go first)
DELETE FROM [Users];

-- Optional deeper wipe (uncomment to also clear run history):
-- DELETE FROM [ConfiguredPipelineRuns];
