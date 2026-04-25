## Summary

<!-- What does this PR do? Keep it brief - the PR title should already describe the change. -->

## Changes

<!-- Bullet list of notable changes. Skip trivial items. -->

-

## Testing

<!-- How was this tested? Check all that apply. -->

- [ ] Manual testing on a Windows 10 (19041+) or Windows 11 host
- [ ] Tail of the latest log file under `%LocalAppData%\Earmark\logs\` shows the expected `Applied rule ...` / `Skip Set ...` / `Skip re-apply ...` lines after the change
- [ ] Unit tests added/updated under `tests/Earmark.Core.Tests`
- [ ] Existing tests pass (`dotnet test -p:Platform=x64`)

## Checklist

- [ ] PR title follows [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `chore:`, etc.) - enforced by CI
- [ ] Code builds without warnings (`dotnet build src/Earmark.App/Earmark.App.csproj -c Debug -p:Platform=x64`)
- [ ] No emoji / gitmoji in commit messages or PR title (breaks release-please)
- [ ] Architecture boundary respected: domain logic in `Earmark.Core`, Windows audio interop in `Earmark.Audio`, UI in `Earmark.App`
- [ ] CLAUDE.md / README / CONTRIBUTING updated if behavior, build steps, or schema changed

<!-- If this PR contains multiple logical changes for the changelog, add
     additional conventional commit footers below. See CLAUDE.md for details.

     Example:
     fix(routing): clear dedupe cache on rule change
     test: cover RuleEvaluator shadow logic
-->
