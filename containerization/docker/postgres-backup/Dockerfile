# Stock postgres:16-alpine (for pg_dump, matching the server version) plus azcopy (a single static
# binary — no OS package manager needed, avoiding Alpine/musl compile issues that pip-installed
# azure-cli hits) and a one-shot backup script. Run on a schedule by main.bicep's postgresBackupJob
# (a Container Apps Job, triggerType Schedule) — the fallback backup control for clients who stay on
# the containerized Postgres path (useAzurePostgresql=false) instead of Azure Database for
# PostgreSQL's built-in automated backups. See backup.sh for what each run actually does.
FROM postgres:16-alpine

RUN apk add --no-cache bash curl \
    && curl -fsSL https://aka.ms/downloadazcopy-v10-linux -o /tmp/azcopy.tar.gz \
    && mkdir -p /tmp/azcopy-extract \
    && tar -xzf /tmp/azcopy.tar.gz -C /tmp/azcopy-extract --strip-components=1 \
    && mv /tmp/azcopy-extract/azcopy /usr/local/bin/azcopy \
    && chmod +x /usr/local/bin/azcopy \
    && rm -rf /tmp/azcopy.tar.gz /tmp/azcopy-extract

COPY containerization/docker/postgres-backup/backup.sh /usr/local/bin/fhirbridge-postgres-backup.sh
RUN chmod +x /usr/local/bin/fhirbridge-postgres-backup.sh

ENTRYPOINT ["fhirbridge-postgres-backup.sh"]
