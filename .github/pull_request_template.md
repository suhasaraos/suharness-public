## What this changes

<!-- One or two sentences. The diff shows what; explain why. -->

## Why

<!-- The problem being solved, or a link to the issue. -->

Closes #

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Refactor with no behaviour change
- [ ] Documentation
- [ ] Build, CI, or tooling

## Model-facing contract

Tool names, parameter names, tool descriptions, and result formatting are a
contract with the model: changing them changes behaviour for every existing
session.

- [ ] This change does not alter any of them.
- [ ] It does, and the change is described above.

## Security

See [SECURITY.md](../SECURITY.md).

- [ ] This does not weaken workspace path scoping, markdown sanitisation,
      credential storage, the approval gate, or the loopback binding policy.
- [ ] If it touches any of those, adversarial tests are included.

## Checklist

- [ ] `dotnet build Suharness.slnx` succeeds with no new warnings
- [ ] `dotnet test` passes
- [ ] `dotnet format Suharness.slnx` leaves no changes
- [ ] Tests added for new behaviour, or for the bug being fixed
- [ ] `CHANGELOG.md` updated under `## Unreleased` if a user would notice
- [ ] `THIRD-PARTY-NOTICES.md` updated if a dependency was added
