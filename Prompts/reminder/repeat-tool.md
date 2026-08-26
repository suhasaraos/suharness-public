<!-- The loop guard's model-facing text. Escalates: a nudge, then a detailed nudge, then a
     synthetic result that stands in for the call the guard refused to execute.
     Each line here is one line of the message the model reads. -->

## gentle
<!-- First threshold: short, and it names the problem without prescribing a fix. -->
This tool call repeats your previous one exactly, arguments included. Re-read the result you already have before trying again. If it did not finish the job, change the arguments or take a different route rather than issuing the same call once more.

## detailed
<!-- Later thresholds: state the evidence, then rule out the call outright. -->
Repeat detected:
- tool: {{toolName}}
- consecutive_calls: {{count}}
- arguments: {{arguments}}
This sequence has stopped making progress. Stop issuing this call with these arguments. Read what the last attempt returned, then either pick a different action, supply different arguments, or treat the task as done if you already have the evidence you need.

## no-progress-warning
No-progress search warning: {{count}} {{family}} calls have returned the same information despite different queries. Stop varying the search text. Read a known match, try a different discovery tool, make the smallest grounded edit, or report a concrete blocker.

## discovery-budget-warning
Discovery budget warning: {{count}} search calls have run without a workspace change. Stop searching now. Read one known match only if necessary, then make the smallest grounded edit, run validation, or report a concrete blocker. Further search calls will be blocked until the workspace changes.

## empty-result
<!-- Stands in for a blocked call's previous output when that output was empty. -->
(empty tool result)

## blocked-repeat-label-cycle
Runaway tool cycle blocked.

## blocked-repeat-label-duplicate
Duplicate tool call blocked.

## blocked-repeat
<!-- cycle is supplied already prefixed with its own newline, or empty. -->
{{label}}

Tool: {{toolName}}
Arguments: {{arguments}}
Previously executed: {{executions}} times
Previous result hash: {{resultHash}}{{cycle}}
Previous result:
{{previousResult}}

No relevant workspace state has changed since that operation. Do not repeat it. Use the existing result, change the arguments, inspect a different resource, or proceed to the next step.

## blocked-no-progress
No-progress search blocked.

Tool family: {{family}}
Current tool: {{toolName}}
Arguments: {{arguments}}
Equivalent results: {{count}}
Last result:
{{previousResult}}

Changing the query wording has not produced new information. Use a different discovery route, inspect a known file, make a grounded edit, run validation, or report a concrete blocker.

## blocked-discovery-budget
Discovery budget exhausted.

Tool: {{toolName}}
Arguments: {{arguments}}
Discovery calls without a workspace change: {{count}}

Enough discovery has been performed. Search tools are blocked until the workspace changes. Use the evidence already collected: read one known file if essential, make the smallest grounded edit, run validation, or report a concrete blocker.
