# Remote Diagnostic Toolkit · RDT

```text
V0.1 foundation baseline (frozen)
STATUS = PARTIAL
Mock foundation PASS
Real ZCode diagnostic runtime NOT ENABLED
```

Windows 运维 wrapper/runtime 的 V0.1 工程地基。只提供 **READ ONLY** 模式，当前使用明确标识的 Mock 子进程验收。**真实诊断暂不可用**，没有模型 API 请求，也不使用客户的现有登录态。

已完成：CLI、PIN 加密凭据、隔离 session、G adapter、runtime 接口和启动契约、Mock 子进程、审计与报告、清理和自动化测试。D 为预留接口。真实 runtime 入口默认且强制关闭；无开关绕过。

当前公开版本冻结为 V0.1 foundation baseline，不具备真实 AI 排障能力。本仓库只发布源码；不包含编译产物、本机运行报告、测试结果、凭据或临时 profile。测试源码中的固定 Key、PIN 和密码样例均为虚构、不可用于任何服务的 fixture 数据。

## 构建与测试

开发需要 .NET 10 SDK（当前固定 10.0.401；允许同 feature band 的更新补丁），首次 restore 访问 nuget.org。客户端发布后无需 SDK、Python、Git 或 Node。

在项目目录使用 PowerShell 5.1 或 7：

```powershell
dotnet restore Rdt.slnx --locked-mode
dotnet build Rdt.slnx -c Release --no-restore
dotnet test Rdt.slnx -c Release --logger 'trx;LogFileName=rdt-tests.trx' --results-directory TestResults
```

也可执行 `scripts/build.ps1` / `scripts/test.ps1`，按本机现有脚本策略运行；不要为本项目修改 ExecutionPolicy。

## 启动与演示

```powershell
.\src\Rdt.Cli\bin\Release\net10.0\rdt.exe help
.\src\Rdt.Cli\bin\Release\net10.0\rdt.exe demo
```

`demo` 要求输入本次测试 PIN（8–128 UTF-8 字节）。输入不回显；只用内置的无效测试 Key 临时建立加密凭据，再解锁、启动 Mock、导出报告并删除临时材料。它不是诊断，也不验证 Provider 的联网能力。退出码 0 表示 Mock 生命周期验收成功，1 表示失败，130 表示取消。

终端示例：

```text
Remote Diagnostic Toolkit
RDT 0.1.0
Operator PIN:
Credential unlocked.
Provider: G
Mode: READ ONLY
Session: TEMP
Runtime: MOCK
Cleanup: complete
Report: ...\Reports\...
Result: None
```

PIN 仅从交互终端读取。输入重定向会被拒绝，不能通过参数或环境变量提供 PIN/Key。自动化测试通过 `ISecretInput` 注入假凭据，并单独运行实际子进程。

## 加密凭据命令

```powershell
.\src\Rdt.Cli\bin\Release\net10.0\rdt.exe credential-init .\config\operator.rdt
.\src\Rdt.Cli\bin\Release\net10.0\rdt.exe run --mock .\config\operator.rdt
```

`credential-init` 是显式创建长期**加密**文件的命令，需要 PIN、确认 PIN 和 G Key；不会覆盖已有文件，父目录必须存在。本阶段无需使用真实 Key。该加密文件由操作者保管，session cleanup 不会删除它。忘记 PIN 无恢复接口；可用长口令提高抵抗离线猜测的能力。

`run --mock` 接受已有加密文件并执行工程验收。Mock 使用 `example.invalid` 与测试模型，完全不联网。`config/provider-g.example.json` 是未来真实接入的无秘密示例，本版 CLI 不加载该文件，也不承诺示例模型可调用。

## 临时数据与报告

```text
%TEMP%\RDT-<GUID>\
  owner.json / .lease
  home\       独立用户目录与运行配置
  data\       个人 provider 与其他数据
  workspace\  全新空工作目录
  runtime\    临时文件和内部日志

<启动工作目录>\Reports\YYYY-MM-DD_HH-mm-ss_<短随机值>\
  summary.md
  session.log
```

报告加随机后缀以避免并发覆盖。只导出生命周期元数据；不导出模型输出、原始业务数据或上游 session/cache。

正常退出、子进程失败、Ctrl+C、超时都会进入 finally 清理。启动时提醒可识别的旧 session；显式运行以下命令，只清理超过 24 小时、合法标记、无活动租约的目录：

```powershell
.\src\Rdt.Cli\bin\Release\net10.0\rdt.exe cleanup-stale
```

不符合所有权/路径检查或包含链接的目录保持原状；cleanup 失败会在报告中标记。断电、强杀和系统故障无法保证立即清理。删除为应用层删除，不宣称物理不可恢复。Reports 和显式加密凭据需要操作者另行保管、导出或删除。

## 发布

```powershell
dotnet publish src/Rdt.Cli/Rdt.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
.\artifacts\win-x64\rdt.exe help
.\artifacts\win-x64\rdt.exe demo
```

也可运行 `scripts/publish.ps1`。发布的是独立运行目录，请复制整个目录。仅 RDT 无需预装 .NET；当前并未捆绑上游 Agent。单文件、签名、更多 Windows/Server/ARM64 兼容性留待后续实测。

## 工程结构

```text
src/
  Rdt.Cli/                 终端命令、取消处理、Mock child
  Rdt.Core/
    Auth/ Credentials/     隐藏输入与版本化凭据
    Session/ Cleanup/      隔离目录、租约、受限清理
    Providers/ Zcode/      G/D 接口与上游契约
    Runtime/               生命周期编排
    Reporting/ Common/     元数据审计与私有文件工具
tests/Rdt.Tests/
config/ scripts/ docs/ Reports/
```

详细设计与实测结果：

- [架构](docs/ARCHITECTURE.md)
- [官方 runtime 调查](docs/ZCODE_RUNTIME_INVESTIGATION.md)
- [安全模型](docs/SECURITY_MODEL.md)
- [工程决策](docs/DECISIONS.md)
- [V0.1 验收](docs/ACCEPTANCE.md)
- [V0.1 公开发布冻结记录](docs/PUBLICATION_REVIEW.md)

## 后续真实接入

官方源码有 Windows SEA 构建支持，但本次未取得和实测可信独立 CLI 发行产物。计划模式也不等价于系统级只读沙箱。必须先固定产物、核实配置及工具面、完成真实拒绝测试与隔离验证，才可实现真实启动。详见 runtime 调查末尾的放行条件。
