/**
 * Build-time version of the portal bundle.
 *
 * This committed value is the LOCAL-DEV default only. The real one is written over this file during
 * the container build: containerization/docker/segue-app/Dockerfile (and demo-app's) runs
 *   echo "export const APP_VERSION = '${APP_VERSION}';" > src/app/version.ts
 * with APP_VERSION set from the image tag by containerization/scripts/build-images.ps1|sh.
 *
 * So: never hand-edit this to a release number — it would only ever be wrong. The product version
 * lives in the repo-root VERSION file, and every artifact derives from there.
 *
 * 'local' is what a developer running `npm start` sees, and is deliberately not a version-shaped
 * string so an unversioned build can never be mistaken for a released one.
 *
 * NOTE: this is the PORTAL BUNDLE's version, which is not the same question as the API's version
 * (GET /api/v1/version, see VersionService). They differ during a partial upgrade or when a browser
 * holds a cached bundle, which is exactly why the footer shows this one and the build-details panel
 * shows both.
 */
export const APP_VERSION = 'local';
