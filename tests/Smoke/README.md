# Browser smoke test (Linux, no IIS)

Runs the real portal, a real agent (sample IIS sites, POSIX paths in Debug builds) and a real Chromium through the whole
deployment flow: upload, review, submit, approve, live execution, health-check failure with automatic rollback, manual rollback.

```
tests/Smoke/run.sh          # needs: dotnet, node + playwright (global), openssl, python3
```
Screenshots and logs go to `$SMOKE_OUT` (default `/tmp/icc-smoke/out`). This is not part of CI (CI runs the Windows end-to-end instead).
