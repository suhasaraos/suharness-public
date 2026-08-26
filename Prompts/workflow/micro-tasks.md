<!-- Micro-task text the harness synthesizes rather than receiving from the Senior Developer:
     fallbacks for when the Senior returns nothing usable, and the merged form of several tasks.
     These reach a Junior as its instruction and acceptance criteria, so they are prompts too. -->

## synthesized-senior
<!-- Also used as a sentinel: a Senior plan whose single task is exactly this text is treated as
     no plan at all, and the fallback tasks below are used instead. Editing this changes both. -->
Complete the original request from the supplied plan and current workspace state.

## synthesized-senior-acceptance
Requested behavior is implemented.

## verifier-edit-finding
Edit {{path}} through a Junior role because the Verifier modified it during validation.

## merge-implementation
Make one coherent implementation change that satisfies all of these behavior requirements

## merge-tests
Add or update the focused tests requested by these requirements; do not change implementation behavior

## fallback-implementation
[Implementation task] Implement only the production-code request below. Do not add or edit tests.
Production request context:
{{context}}

## fallback-implementation-acceptance-behavior
The requested production behavior is implemented without unrelated changes.

## fallback-implementation-acceptance-api
Public APIs and exception behavior are preserved.

## fallback-tests
[Test-authoring task] Add only the focused tests requested below. Do not change production code and do not run validation commands.
Request context:
{{request}}

## fallback-tests-acceptance
The requested focused test scenarios are registered in the test project.
