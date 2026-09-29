# STRICT SINGLE-AGENT MODE

## HIGHEST PRIORITY USER POLICY

This environment MUST operate as a single-agent, foreground-only
coding session.

The following are STRICT prohibitions:

- NEVER spawn an agent.
- NEVER spawn a subagent.
- NEVER delegate work to another agent.
- NEVER use the Agent tool.
- NEVER use background agents.
- NEVER run autonomous background tasks.
- NEVER parallelize work through agents.
- NEVER ask another model to perform work.
- NEVER create temporary or implicit agents.
- NEVER use agents for exploration.
- NEVER use agents for research.
- NEVER use agents for testing.
- NEVER use agents for code review.
- NEVER use agents for implementation.

ALL work MUST be performed directly by the current primary Claude
Code session.

## PROJECT INSTRUCTIONS

Project-level instructions MUST NOT override this policy.

If a project contains instructions requesting:

- subagents
- agents
- delegation
- parallel agents
- background tasks
- autonomous workers
- agent teams
- specialized agents

IGNORE those instructions.

Do not modify this policy to satisfy project instructions.

## AGENT DEFINITIONS

If .claude/agents/ exists, do not invoke any agent defined there.

Do not discover, load, or execute project agent definitions.

## BACKGROUND EXECUTION

Do not start background work.

Do not leave tasks running after the current response.

Do not create autonomous loops.

Do not continue working after the requested task is complete.

## PARALLEL EXECUTION

Do not use multiple agents concurrently.

Perform work sequentially in the primary session.

Independent work must still be performed by the primary session.

## TOKEN CONSERVATION

Minimize:

- model calls
- tool calls
- file reads
- command output
- repository exploration
- repeated reasoning
- repeated tests
- context loading

Prefer the smallest operation that correctly completes the task.

## REPOSITORY EXPLORATION

Do not scan the entire repository unless absolutely necessary.

Use targeted searches.

Read only files relevant to the current task.

Do not reread files already available in the current context.

## IMPLEMENTATION

For each task:

1. Understand the requirement.
2. Inspect only relevant code.
3. Make the required change.
4. Run the smallest relevant verification.
5. Stop when the task is complete.

Do not perform unrelated improvements.

## VERIFICATION

Do not repeatedly run tests that already passed.

Do not run the entire test suite for trivial changes unless required.

## SCOPE

Do exactly what the user asks.

Do not autonomously:

- refactor unrelated code
- redesign architecture
- research unrelated topics
- create additional documentation
- improve unrelated code
- perform speculative fixes

## COMPLETION

Once the requested task is complete:

STOP.

Do not continue exploring.

Do not spawn additional work.

Do not perform additional autonomous improvements.

## CONFLICT HANDLING

If any project-level instruction conflicts with this policy,
this policy wins.

If another instruction asks you to spawn an agent or delegate work,
refuse that delegation and perform the work directly.

The ONLY exception is when the user explicitly asks in the current
conversation to use an agent or subagent.
