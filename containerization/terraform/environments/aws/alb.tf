# One ALB, one HTTPS listener — var.segue_app_port (default 80) for segue-app. The
# listener port is client-configurable independently of the target group/container port below,
# which stays fixed at 80 to match what's actually baked into the image. TLS terminates at the ALB
# using the self-signed certificate from main.tf (aws_acm_certificate.alb) — traffic from the ALB
# to the container itself stays plain HTTP, which is fine since it never leaves the private
# subnets. worker/postgres/redis have no target group; postgres/redis are reached only via Cloud
# Map inside the VPC (see discovery.tf) — or, when use_rds_postgresql is true, postgres is
# Amazon RDS instead, reached by its own endpoint, not the ALB either way.

resource "aws_lb" "main" {
  name               = "${var.name_prefix}-alb"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb.id]
  subnets            = aws_subnet.public[*].id
}

resource "aws_lb_target_group" "segue_app" {
  name        = "${var.name_prefix}-app-tg"
  port        = 80
  protocol    = "HTTP"
  vpc_id      = aws_vpc.main.id
  target_type = "ip"

  health_check {
    path                = "/health"
    matcher             = "200"
    interval            = 30
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "segue_app" {
  load_balancer_arn = aws_lb.main.arn
  port              = var.segue_app_port
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = aws_acm_certificate.alb.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.segue_app.arn
  }
}

# --- Seq (structured log viewing) — only when var.enable_seq is true. A dedicated listener/port
#     (not a path-based rule on the segue_app listener), since Seq is a completely separate
#     service with its own health check and target — deliberately external, unlike postgres/redis,
#     since the whole point is being able to browse to it and monitor logs. ---

resource "aws_lb_target_group" "seq" {
  count       = var.enable_seq ? 1 : 0
  name        = "${var.name_prefix}-seq-tg"
  port        = 80
  protocol    = "HTTP"
  vpc_id      = aws_vpc.main.id
  target_type = "ip"

  health_check {
    path                = "/"
    matcher             = "200-399"
    interval            = 30
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "seq" {
  count             = var.enable_seq ? 1 : 0
  load_balancer_arn = aws_lb.main.arn
  port              = var.seq_port
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = aws_acm_certificate.alb.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.seq[0].arn
  }
}
