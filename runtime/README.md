# Fixed local runtime

Place the already-qualified `zcode-windows-x64.exe` here. It is ignored by Git.
Do not replace it with a different build. Exact identity is recorded in
`config/runtime-identity.json` and enforced by the production code.

`zcode-builtin.json` is the unmodified upstream
`config/provider/zcode-builtin.test.json`, SHA-256
`e4e8e3527fa131195e3dc009d4fac9e5b403834281e9ecdddaa68850dc2cb7c0`.
Git must preserve its bytes. The builtin is staged into each private session.

Run `scripts/publish.ps1` for a complete Windows x64 package. A bare dotnet build
does not copy the large executable. Runtime updates require fresh qualification.
