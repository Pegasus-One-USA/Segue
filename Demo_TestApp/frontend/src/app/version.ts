// Overwritten at Docker build time with the image tag (see the frontend-build stage in
// containerization/docker/demo-app/Dockerfile and the APP_VERSION build-arg in
// containerization/scripts/build-images.ps1 / .sh) so the footer shows which build is deployed.
export const APP_VERSION = 'local';
