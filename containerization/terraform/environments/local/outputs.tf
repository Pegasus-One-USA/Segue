output "fhirbridge_app_url" {
  value = "http://localhost:${var.app_host_port}"
}

# No "external access"/custom-domain toggle here, unlike the aws/azure environments — every
# container's port is already published to the host machine unconditionally (see
# docker_container.postgres's ports block in main.tf), same as every other container in this local
# stack. "Reachable from outside" for a local Docker deployment just means whatever can already
# reach this host machine; there's no cloud load balancer or Container App ingress layer here to
# gate that behind a flag.
