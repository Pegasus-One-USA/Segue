# One ALB, two HTTPS listeners — var.fhirbridge_app_port (default 80) for fhirbridge-app,
# var.demo_app_port (default 5500) for demo-app (that default matches its established convention
# from deploy/windows/README.md — override to 443 for a more conventional HTTPS port). Listener
# ports are client-configurable independently of the target group/container ports below, which
# stay fixed at 80/5500 to match what's actually baked into the images. TLS terminates at the ALB
# using the self-signed certificate from main.tf (aws_acm_certificate.alb) — traffic from the ALB
# to the containers themselves stays plain HTTP, which is fine since it never leaves the private
# subnets. worker/sqlserver/redis have no target group; they're reached only via Cloud Map inside
# the VPC (see discovery.tf).

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

resource "aws_lb_target_group" "demo_app" {
  name        = "${var.name_prefix}-demo-tg"
  port        = 5500
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

resource "aws_lb_listener" "demo_app" {
  load_balancer_arn = aws_lb.main.arn
  port              = var.demo_app_port
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = aws_acm_certificate.alb.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.demo_app.arn
  }
}

# Opt-in only (var.hapi_terminology_external_access, default false) — sqlserver/redis have no ALB
# presence at all (Cloud Map only); the terminology server gets this instead of that same
# internal-only treatment because there's a real case for reaching it directly from outside (a
# separate terminology admin tool, a third-party integration) that never applies to the database.

resource "aws_lb_target_group" "hapi_terminology" {
  count       = var.hapi_terminology_external_access ? 1 : 0
  name        = "${var.name_prefix}-term-tg"
  port        = 8080
  protocol    = "HTTP"
  vpc_id      = aws_vpc.main.id
  target_type = "ip"

  health_check {
    path                = "/fhir/metadata"
    matcher             = "200"
    interval            = 30
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "hapi_terminology" {
  count             = var.hapi_terminology_external_access ? 1 : 0
  load_balancer_arn = aws_lb.main.arn
  port              = var.hapi_terminology_port
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = aws_acm_certificate.alb.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.hapi_terminology[0].arn
  }
}
