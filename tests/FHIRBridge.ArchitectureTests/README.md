# FHIRBridge.ArchitectureTests

> Executable guardrails that enforce FHIRBridge's Clean-Architecture / DDD layering rules — dependency direction, naming conventions, and forbidden references.

**Type:** Architecture tests · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
This project turns the solution's architectural intent into automated, fail-the-build assertions. Using **NetArchTest.Rules** to reflect over the compiled assemblies, it verifies that the Clean-Architecture boundaries hold: that `Domain` stays dependency-free, that the dependency arrows always point inward, that the control plane and runtime plane do not leak into one another improperly, and that naming/structural conventions are followed. It is a low-maintenance, high-leverage safety net that catches accidental coupling before it ossifies — something ordinary unit tests cannot see.

## Scope — what it will test
- **Dependency direction (the core rule)**
  - `FHIRBridge.Domain` and `FHIRBridge.Runtime.Domain` depend on **no other** solution layers (no Application, Infrastructure, Api, EF Core, etc.).
  - `FHIRBridge.Application` / `FHIRBridge.ControlPlane.Application` / `FHIRBridge.Runtime.Application` depend only on their respective Domain layers (and shared building blocks), never on Infrastructure or Api.
  - No layer references upward/outward (e.g. Domain must not reference Application; Application must not reference Infrastructure or the web host).
- **Plane isolation (control vs runtime)**
  - The control plane (`ControlPlane.Domain` / `ControlPlane.Application`) and the runtime plane (`Runtime.Domain` / `Runtime.Application`) do not take disallowed direct references on each other — cross-plane interaction goes through the sanctioned contracts/building blocks.
- **Naming & structural conventions**
  - Handlers, validators, command/query types, repositories, and domain events follow the project's suffix conventions (e.g. `*Handler`, `*Validator`, `*Repository`).
  - Interfaces are placed in the layer that owns the abstraction (ports defined in Application/Domain, implementations in Infrastructure).
  - Entities/aggregates live under the expected namespaces; no public type lands in the wrong layer.
- **Forbidden-reference policies**
  - Domain types do not depend on infrastructure concerns (EF Core, HTTP, serialization frameworks, logging implementations).
  - Application code does not reference concrete persistence or external SDKs directly — only abstractions.

## Test stack
- **Frameworks/tools:** xUnit (hosts the rules as test cases), NetArchTest.Rules (assembly-reflection rule engine), FluentAssertions (readable rule failures), coverlet.collector (coverage; incidental here).
- **Projects under test (assemblies inspected):**
  - `src/FHIRBridge.Domain`
  - `src/FHIRBridge.Application`
  - `src/ControlPlane/FHIRBridge.ControlPlane.Domain`
  - `src/ControlPlane/FHIRBridge.ControlPlane.Application`
  - `src/Runtime/FHIRBridge.Runtime.Domain`
  - `src/Runtime/FHIRBridge.Runtime.Application`

## Current state in this skeleton
Only the `.csproj` is present in this skeleton repo. No rule classes exist yet — the suites below are to be authored here (or ported from the reference implementation). The project references the six layer assemblies it polices so they are loadable for reflection, targets `net9.0`, and globally imports `Xunit`; new rule tests run under `dotnet test` once added.

## Roadmap — what it will do in detail
Planned organization groups rules by concern, each rule expressed as an xUnit fact that builds a `Types.InAssembly(...)` query, applies `ShouldNot`/`Should` predicates, and asserts `result.IsSuccessful` with a FluentAssertions message that lists the offending types:

- **`LayeringRules`** — one fact per "X must not depend on Y" pairing covering all inward-pointing arrows for both the control and runtime planes; plus the cornerstone "Domain depends on nothing" checks.
- **`PlaneIsolationRules`** — facts asserting the control plane and runtime plane only interact through approved building blocks/contracts.
- **`NamingConventionRules`** — facts enforcing type-suffix and namespace conventions for handlers, validators, repositories, events, and entities.
- **`ForbiddenDependencyRules`** — facts banning infrastructure/framework references (EF Core, HTTP, logging concretions) from Domain and Application.
- **Conventions:** rules read as English (`Types.InAssembly(domain).Should().NotHaveDependencyOnAny(...)`); each failure names the violating types so fixes are obvious; new layers are added by extending the reference-assembly anchors.
- **CI fit:** Runs in the standard `dotnet test` pass on every PR/merge. It is fast and hermetic (pure reflection, no I/O), and it deliberately fails the build the moment a developer introduces a cross-layer reference — making architectural drift a compile-time-adjacent error rather than a code-review judgment call. Together with the two unit-test suites it completes the test pyramid: behavior (unit) + structure (architecture).
