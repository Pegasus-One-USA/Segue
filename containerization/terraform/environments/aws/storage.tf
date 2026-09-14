# Persistent storage for the containerized Postgres path + Redis (Fargate tasks are otherwise
# stateless / ephemeral storage only). One filesystem, one access point per stateful service so
# each gets an isolated root directory.

resource "aws_security_group" "efs" {
  name        = "${var.name_prefix}-efs-sg"
  description = "EFS mount targets — NFS from ECS tasks only."
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "NFS from ECS tasks"
    from_port       = 2049
    to_port         = 2049
    protocol        = "tcp"
    security_groups = [aws_security_group.ecs_tasks.id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_efs_file_system" "main" {
  creation_token = "${var.name_prefix}-data"
  # Encryption at rest — not on by default for EFS. This is PHI-bearing storage (Postgres +
  # Redis data), so it stays on regardless of environment.
  encrypted = true

  tags = { Name = "${var.name_prefix}-data" }
}

resource "aws_efs_mount_target" "main" {
  count           = 2
  file_system_id  = aws_efs_file_system.main.id
  subnet_id       = aws_subnet.private[count.index].id
  security_groups = [aws_security_group.efs.id]
}

# Only needed for the containerized Postgres path — Amazon RDS for PostgreSQL is a managed service
# with no EFS volume of its own. Gated the same as the postgres ECS task definition/service.
resource "aws_efs_access_point" "postgres_data" {
  count          = var.use_rds_postgresql ? 0 : 1
  file_system_id = aws_efs_file_system.main.id

  posix_user {
    uid = 0
    gid = 0
  }

  root_directory {
    path = "/postgres-data"
    creation_info {
      owner_uid   = 0
      owner_gid   = 0
      permissions = "0755"
    }
  }
}

resource "aws_efs_access_point" "redis_data" {
  file_system_id = aws_efs_file_system.main.id

  posix_user {
    uid = 0
    gid = 0
  }

  root_directory {
    path = "/redis-data"
    creation_info {
      owner_uid   = 0
      owner_gid   = 0
      permissions = "0755"
    }
  }
}

# Only needed when var.enable_seq is true. Unlike Azure Files (SMB, which can't satisfy Postgres's
# chmod-based startup check — see the Azure environment's postgres resource comments), EFS is real
# POSIX-permission NFS storage, so Seq's data directory isn't expected to hit an equivalent wall.
resource "aws_efs_access_point" "seq_data" {
  count          = var.enable_seq ? 1 : 0
  file_system_id = aws_efs_file_system.main.id

  posix_user {
    uid = 0
    gid = 0
  }

  root_directory {
    path = "/seq-data"
    creation_info {
      owner_uid   = 0
      owner_gid   = 0
      permissions = "0755"
    }
  }
}
