# One ALB, one HTTPS listener — var.fhirbridge_app_port (default 80) for fhirbridge-app. The
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

resource "aws_lb_target_group" "fhirbridge_app" {
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

resource "aws_lb_listener" "fhirbridge_app" {
  load_balancer_arn = aws_lb.main.arn
  port              = var.fhirbridge_app_port
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = aws_acm_certificate.alb.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.fhirbridge_app.arn
  }
}
