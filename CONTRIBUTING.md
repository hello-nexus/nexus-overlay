# Contributing

## License

This repository is licensed under AGPL-3.0 (see [`LICENSE`](LICENSE)). By
contributing, you agree your contributions are licensed under AGPL-3.0 and the
terms in [`CLA.md`](CLA.md).

## Tested on a real Windows machine, or not merged

This process hosts WebView2 windows, captures monitors and drives touch panels.
Its unit tests cover the pure math; a change that passes them has not been
tested until the published exe has run on a Windows machine.

Every pull request that changes what ships (anything but docs and tests) must
have been published, run and tested by the contributor, on the contributor's
own Windows machine, driven by a running nexus-service. There is no lab that
does this for you. A pull request without that is closed, whatever its size.

1. Publish with the command in `README.md` (native AOT, single file) and drop
   the exe next to the service.
2. Exercise the change on the window it touches: dashboard, overlay per
   monitor, or the panel kiosk on a physical touch panel.
3. Fill in the Validation section of the pull request template. Write down
   what you observed, not what you expect.

If you cannot test a change on the hardware, do not send it. Open an issue and
describe what you found.

## If an AI agent writes the change

The same rules apply, and the person who opens the pull request answers for
them. An agent cannot run the result on your machine, so the validation
section describes what you ran, on your machine, in your words. A pull request
whose validation text does not match what was run is closed.

## Standards

- One topic per pull request.
- `dotnet test` passes locally. New behaviour comes with tests where the code
  is testable (region, plan and parsing math).
- The build stays warning-free.
- Native AOT, no `Microsoft.Web.WebView2.Core`: COM bindings stay hand-rolled
  in `src/WebView2/`, no reflection, no dynamic code.
- Match the surrounding code. Do not reformat, rename or reorganize anything
  the change does not need.
- Keep `README.md` true. If the change alters how the process is built, run
  or laid out, update the README in the same pull request.
- Read every line you submit, generated or not, and be able to say why it is
  there.

## Workflow

1. Fork and branch from `main`.
2. Open the pull request against `main` and complete every section of the
   template.
3. Confirm in the pull request that you have read and agree to
   [`CLA.md`](CLA.md).
4. Answer review with new commits. After any change, test again and update
   the validation section.
