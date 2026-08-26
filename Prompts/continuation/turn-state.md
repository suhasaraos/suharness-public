<!-- Wraps every continuation prompt with the turn's objective facts, so a continuation
     re-grounds the model in the repository and the original task rather than in its own narration. -->
{{instruction}}
<turn_state>
Selected repository: {{workspace}}
Original user task:
{{originalPrompt}}
Current evidence: {{mutations}} successful mutations; tests_changed={{testsChanged}}; successful_builds_after_latest_mutation={{builds}}; successful_tests_after_latest_mutation={{tests}}.
All paths are relative to the selected repository. Do not ask where its source, tests, plugins, or named files are located.
</turn_state>
