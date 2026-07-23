# Persistent storage for SQL Server + Redis (Fargate tasks are otherwise stateless / ephemeral
# storage only). One filesystem, two access points so each service gets an isolated root directory.

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

  tags = { Name = "${var.name_prefix}-data" }
}

resource "aws_efs_mount_target" "main" {
  count           = 2
  file_system_id  = aws_efs_file_system.main.id
  subnet_id       = aws_subnet.public[count.index].id
  security_groups = [aws_security_group.efs.id]
}

resource "aws_efs_access_point" "sql_data" {
  file_system_id = aws_efs_file_system.main.id

  posix_user {
    uid = 0
    gid = 0
  }

  root_directory {
    path = "/sql-data"
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
