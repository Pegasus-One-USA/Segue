# Stock postgres:16-alpine plus a custom entrypoint that keeps PGDATA on the container's own local
# (ephemeral) disk instead of directly on a mounted Azure Files share.
#
# Why this image exists at all: Postgres's own startup check (check_data_dir(), inside the compiled
# server binary — not something an entrypoint script or image config can bypass) requires PGDATA's
# reported permissions to be exactly 0700, checked via stat() on every single startup, not just at
# initdb. Azure Files, as mounted by Container Apps' azureFile storage integration, is SMB — which
# has no concept of POSIX permission bits at all, so that check can never pass there, regardless of
# image, entrypoint, or PGDATA subdirectory tricks. Confirmed live: initdb's own chmod(dir, 0700)
# call fails outright with "Operation not permitted" on that mount.
#
# The only way around it without leaving Container Apps entirely (e.g. for a real block-storage
# option like AKS + Azure Disk) is to not put PGDATA on the network share at all: run Postgres on
# local disk (real POSIX permissions, initdb/startup checks pass normally), and use the Azure Files
# mount purely as an at-rest backup target — restored into local storage on container start, saved
# back out on graceful shutdown. See entrypoint.sh for exactly how, and its own comments for the
# durability tradeoff this implies (data written since the last graceful shutdown is lost on any
# non-graceful restart — a real cost, accepted here for a demo/test environment specifically).
FROM postgres:16-alpine

COPY containerization/docker/postgres-local/entrypoint.sh /usr/local/bin/fhirbridge-postgres-entrypoint.sh
RUN chmod +x /usr/local/bin/fhirbridge-postgres-entrypoint.sh

ENTRYPOINT ["fhirbridge-postgres-entrypoint.sh"]
CMD ["postgres"]
