## What changes for the user

<!-- One or two sentences. Link the issue: Fixes #123 -->

## How it works

<!-- The approach, and anything a reviewer should look at first. -->

## Checklist

- [ ] One change; unrelated changes are in their own pull request
- [ ] New outbound connections have a row in `PRIVACY.md` and can be turned off
- [ ] New paths reachable over Wi-Fi (`Lan.cs` allow-list) or new ways to control the computer are explained above
- [ ] Secrets stay out of `ToPublic()` and logs; processes start with `ArgumentList`, no shell
- [ ] Tests: `dotnet test agent/PhoneBox.Agent.Tests`, `npx playwright test` (in `tools/ui-tests`), `python tools/check-release.py`

## How I tested it

<!-- Commands you ran, what you tried by hand, on which OS / phone / Android version. Screenshots for UI changes. -->
