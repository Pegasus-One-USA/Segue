variable "name_prefix" {
  description = "Short name used to build resource names (VPC, cluster, ECR repos, ALB, etc.)."
  type        = string
  default     = "fhirbridge"
}

variable "aws_region" {
  description = "AWS region."
  type        = string
  default     = "us-east-1"
}

variable "vpc_cidr" {
  description = "CIDR block for the dedicated VPC this stack provisions."
  type        = string
  default     = "10.90.0.0/16"
}

variable "image_tag" {
  description = "Tag the 3 custom images were pushed to ECR with (containerization/scripts/build-images.sh|ps1 -Registry <account>.dkr.ecr.<region>.amazonaws.com/<name_prefix> -Tag <this> -Push)."
  type        = string
  default     = "latest"
}

variable "sql_sa_password" {
  description = "SQL Server SA password. Must satisfy SQL Server's complexity policy."
  type        = string
  sensitive   = true
}

variable "jwt_signing_key" {
  description = "FHIRBridge.Api's Authentication:SigningKey (HS256). At least 32 random characters."
  type        = string
  sensitive   = true
}
