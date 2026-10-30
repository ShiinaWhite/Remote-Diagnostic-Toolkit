# Remote Diagnostic Toolkit · RDT

V0.3 production runtime adapter，默认 **YOLO**。RDT 管理加密凭据、固定 runtime 身份、私有 Profile/Session、stdio、Windows Job 生命周期、清理和安全元数据报告。Agent 的任务边界由操作者提示词定义。

V0.2 qualification 已通过外部 Review，冻结于 `89dcc2c02952de45d5929da8219bedefc0068242`。V0.3 在 `v0.3-production-runtime` 开发，等待外部 Review；首次真实 G 验收的结果单独记录，不以本地假 Provider 通过代替。RDT **LICENSE = UNDECIDED**。

## 构建与发布

需要 Windows x64 和 .NET 10 SDK（见 `global.json`）。客户端发布后无需预装 .NET、Node、Python 或 Git。

```powershell
dotnet restore Rdt.slnx --locked-mode
dotnet build Rdt.slnx -c Release --no-restore
dotnet test Rdt.slnx -c Release --no-build --no-restore
```

将已资格验证的 SEA 放在 `runtime/zcode-windows-x64.exe`，执行：

```powershell
./scripts/publish.ps1
./artifacts/win-x64/rdt.exe help
```

发布脚本验证 executable 和 builtin SHA-256，生成完整独立目录。固定 runtime 不提交 Git、不自动下载、不自动更新；不能通过 CLI 指定其他 executable。请复制整个发布目录，包括 runtime、config、third-party 和 NOTICE。不要为本项目修改系统 ExecutionPolicy。

固定上游 `3c5b227649371507ddc4eb46ac5133afe6db1cab`，CLI `0.17.0`：

```text
runtime SHA-256 = f40f4b6c8709ba2cc89ea000c3a6d97e8680ee4233720f737b6b7a506417ddf8
builtin SHA-256 = e4e8e3527fa131195e3dc009d4fac9e5b403834281e9ecdddaa68850dc2cb7c0
```

更换 runtime 以后需要重新 qualification。现有 qualification 不再扩展。

## 凭据与真实任务

先核对 `config/provider-g.json`（发布后在 `artifacts/win-x64/config`）。它仅含非秘密配置，与凭据文件分开。当前操作者选择国内 BigModel Coding Plan，默认 Base URL 为 `https://open.bigmodel.cn/api/coding/paas/v4`，model ID 为 `glm-5.3-flash`，schema 为 `openai-chat-completions`。国际与国内、Coding Plan 与按量 endpoint 必须匹配 Key 来源。当前官方来源和核对说明见 [生产契约](docs/PRODUCTION_RUNTIME.md)。

在本机交互终端输入 Key 和 PIN；不要通过聊天、参数、环境变量、源码或 .env 提供秘密：

```powershell
./artifacts/win-x64/rdt.exe credential-init ./operator.rdt
./artifacts/win-x64/rdt.exe run ./operator.rdt
```

PIN 输入不回显，8–128 UTF-8 字节；凭据使用 scrypt 和 AES-256-GCM，不覆盖已有文件，父目录必须存在。建议使用 RDT 专用且可单独吊销的 Key。忘记 PIN 无恢复接口。

```text
Remote Diagnostic Toolkit
Operator PIN:
Credential unlocked.
Provider: G
Mode: YOLO
Runtime: READY
Session: TEMP

Enter diagnostic task.
Finish with a line containing only:
.run

RDT >
```

支持多行任务；单独一行 `.run` 提交。任务经 UTF-8 NDJSON `session/send.content` 发送，不进入 RDT 或 ZCode 的 argv/environment。RDT 添加简短 PowerShell UTF-8 运行约定，保留原任务文字。终端显示工具名称、工作状态和最终回答；不会永久记录完整 protocol frame 或模型输出。

默认 YOLO 具有当前用户的正常运维能力。没有命令分类器、逐次人工修改批准或 REPAIR 模式；Job 仅拥有生命周期，仅设置 `KILL_ON_JOB_CLOSE`，没有资源/权限/进程数限制。显式 `session/create.mode=yolo`，不设置 toolAllowlist/toolDenylist。

## Session、退出和报告

所有 Profile、缓存、日志和个人 Provider 明文配置仅位于 `%TEMP%/RDT-<GUID>`。环境由 Session 路径和 Windows system-only PATH 重建，不继承用户 PATH、API keys、NODE_OPTIONS 或 ZCODE overrides。固定 builtin 与 personal 配置同时指定，保留已经验证的控制面/CDN refresh 消除路径。

runtime SHA 不匹配、Job 创建/配置/归属验证失败都会拒绝启动协议。Ctrl+C、超时、EOF、协议错误和模型错误进入相同清理路径：尝试 session/stop，关闭 stdin，等待退出，必要时 Terminate Job，验证成员退出后清理 Session。默认单次 Session 总超时 15 分钟，含任务输入时间。

`Reports/<timestamp>_<random>/summary.md` 与 `session.log` 只保留生命周期元数据，不保存任务、模型输出、业务数据或上游缓存。PIN/Key 不写日志。应用层删除不宣称物理不可恢复；断电/强杀后遗留 Session 可显式清理：

```powershell
./artifacts/win-x64/rdt.exe cleanup-stale
```

清理仅接受超过 24 小时、合法所有权标记、无活动租约且无链接的 Session。加密凭据和 Reports 由操作者另行保管。

## 本地验收

普通 CI 不需要真实 Key，不调用真实模型。原来的 READ ONLY Mock 仍可用于地基验收：

```powershell
./artifacts/win-x64/rdt.exe demo
./artifacts/win-x64/rdt.exe run --mock ./operator.rdt
```

固定 runtime 的生产 Adapter 假 Provider smoke（本地、loopback、不转发）：

```powershell
dotnet restore tools/Rdt.RuntimeAcceptance/Rdt.RuntimeAcceptance.csproj --locked-mode
python scripts/production-smoke.py
```

首次真实验收必须在构建、自动化测试和 secret scan 通过后，由操作者先运行 credential-init。开发验收工具复用生产 Adapter、Vault 和 Job；PIN 同样在本机隐藏输入，自动发送 [限定 synthetic 任务](scripts/first-real-task.txt)，只保存布尔/计数证据：

```powershell
dotnet build tools/Rdt.RuntimeAcceptance/Rdt.RuntimeAcceptance.csproj -c Release --no-restore
dotnet tools/Rdt.RuntimeAcceptance/bin/Release/net10.0/Rdt.RuntimeAcceptance.dll real ./operator.rdt
```

它验证 G → tool call → PowerShell → 中文文件创建/读取/删除 → continuation → 最终回答，并检查真实 Profile、Job 和 Session。真实验收不进入 CI；网络观察的实际范围单独披露。

## 文档与归属

- [生产 runtime 契约](docs/PRODUCTION_RUNTIME.md)
- [V0.2 YOLO 资格记录](docs/RUNTIME_QUALIFICATION_YOLO.md)
- [Job / UTF-8 资格记录](docs/RUNTIME_LIFECYCLE_UTF8_QUALIFICATION.md)
- [历史网络观察与范围](docs/ZCODE_NETWORK_BEHAVIOR.md)
- [历史 V0.1 架构](docs/ARCHITECTURE.md)、[安全模型](docs/SECURITY_MODEL.md)、[验收](docs/ACCEPTANCE.md)
- [ZCode 原始许可证与 notices](third-party/zcode/README.md)、[第三方归属](THIRD-PARTY-NOTICES.md)

本阶段仅 G、Windows x64、终端交互；不实现 D、GUI、服务、计划任务、提权、浏览器自动化或远控服务。
