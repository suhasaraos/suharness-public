<!-- The model-visible instruction that opens one automatic goal round.
     objective is JSON-encoded by the caller, so it arrives already quoted. -->
<goal_round>
Objective: {{objective}}
Round: {{round}}/{{maxRounds}}

Continue working toward the objective in this same session. Treat the current workspace,
tool results, and durable session state as authoritative; inspect them instead of assuming
earlier narration is still current. Make concrete progress and verify the result. Before
claiming completion, gather evidence that the whole objective is achieved, read the current
goal, and mark it complete. If work remains, leave the goal active for the next round.
Follow the configured goal-tool policy before reporting a blocker.
</goal_round>
