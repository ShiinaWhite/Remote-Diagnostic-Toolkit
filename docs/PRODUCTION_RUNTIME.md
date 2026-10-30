# V0.3 production runtime contract

Qualification is frozen at `89dcc2c02952de45d5929da8219bedefc0068242`.
Development branch: `v0.3-production-runtime`. RDT license: **UNDECIDED**.

`rdt run <credential-file>` unlocks the existing scrypt/AES-256-GCM vault,
creates a private temporary session and starts the fixed Windows x64 runtime.
The default production mode is YOLO. Operator task boundaries come from the task.
The historical READ ONLY mock remains available through `run --mock`.

The runtime owner verifies SHA-256 of the executable and exact upstream builtin,
creates/configures a noninheritable Job with only KILL_ON_JOB_CLOSE, starts ZCode,
assigns it and verifies membership before opening protocol operations. Every
failure is closed. There is no arbitrary executable CLI option.

NDJSON uses UTF-8 without BOM and strict UTF-8 decoding. Tasks exist only in
`session/send.content`, prefixed with the PowerShell UTF-8 execution contract.
`session/create` explicitly sets yolo and disables dynamic workflow, off-peak
tools and title generation. No tool allow/deny lists are supplied. Only the
schema-checked `session/requestRuntimePreferences` reverse request is answered;
unknown methods receive method-not-found, with only a redacted method name shown.

The process environment is rebuilt from private session paths and Windows system
directories. Both builtin/personal provider paths are explicit. No inherited user
PATH, API credentials, NODE_OPTIONS or ZCODE overrides are passed.

Shutdown attempts session/stop, closes stdin, waits for root exit, terminates
remaining Job members when needed, verifies an empty Job and closes it before
session deletion. EOF, malformed frames, provider failure, cancellation and
timeouts follow the same ownership path. Reports retain lifecycle metadata only.

Nonsecret provider configuration is versioned separately from the encrypted key.
The operator selected domestic BigModel Coding Plan. Default model ID is
`glm-5.3-flash`; base is `https://open.bigmodel.cn/api/coding/paas/v4`.
Reasoning options remain at upstream/provider defaults.

Official sources checked 2026-10-30:

- https://docs.bigmodel.cn/cn/coding-plan/tool/others
- https://docs.bigmodel.cn/cn/guide/models/vlm/glm-5.3-flash
- https://docs.z.ai/api-reference/llm/chat-completion

The Flash model page explicitly lists `glm-5.3-flash`; the generic completion
page's model enum is narrower. Actual account availability remains a live-test
check. No live key is needed in CI. The first real run requires the operator's
local interactive credential-init and a bounded synthetic Windows/Unicode task.
