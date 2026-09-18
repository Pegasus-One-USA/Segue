export const environment = {
  production: false,
  // Dev runs backend (ng serve won't build the backend) and frontend on different ports;
  // the backend's "Frontend" CORS policy already allows this origin.
  healthAppBase: 'http://localhost:5500',
  fhirbridgeBase: 'https://localhost:5001',
};
