# ZCode runtime 调查（V0.1）

> 下文保留 V0.1 的历史调查。2026-10-30 已重新查询上游并完成 V0.2 第一阶段的固定候选实验，见 [RUNTIME_QUALIFICATION.md](RUNTIME_QUALIFICATION.md) 和 [网络观察](ZCODE_NETWORK_BEHAVIOR.md)。生产 runtime 仍 fail closed。

## V0.2 Phase 2：当前产品方向与实测

**SUPERSEDED BY PRODUCT DIRECTION DECISION：** 下述 V0.1 / Phase 1 的“只读默认产品目标、危险工具必须拒绝、独立只读 Windows 采集层”放行建议已被取代；历史实验保留。当前默认目标为 **YOLO / full Agent capabilities**，READ ONLY 为未来可选模式。RDT 负责加密凭据、固定身份、Profile/Session、传输、清理和报告，不另建 Windows 只读采集器。

Phase 2 使用同一固定 hash SEA、官方 `agent-server --stdio`、`session/create(mode=yolo)`，无只读 allowlist/denylist。任务只经 stdin frame，不再以 `--prompt` 作为生产设计。实际 Provider schema 包含 Bash/Read/Write/Edit、Agent/Skill/Cron 和随包插件工具；Glob/Grep/ApplyPatch 不在当前工具面，强制调用返回 Tool not found。PowerShell 真实系统查询、workspace 写改删及一次性 HKCU CRUD 已取得工具结果与副作用证据。

固定生产 SEA protocol server **实测不加载父 `.env`**；Phase 1 `--prompt` headless 向上发现 `.env` 的证据仍有效，两者不能混为一个启动路径。成对设置 session-local builtin/personal 文件后，固定官方 builtin 可用，控制面 canary 在启动及超过 60 秒刷新周期后均无请求；源码 remote synchronizer 分支也不构造。这个结论只适用于记录的固定配置，不声称所有 YOLO 工具不联网。

加入固定 Windows 系统 PATH/PATHEXT/ComSpec 的候选环境后，native cmd 和前台取消正常；与冻结生产环境分开记录。后台 Start-Process 子进程在 ZCode 正常退出后仍存活，必须作测试兜底回收；普通用户权限和完整整机落盘归因仍未证明。**STATUS=PARTIAL，生产 `ZcodeRuntime => RUNTIME_NOT_VALIDATED` 保持，等待外部 Review。** NotSigned 与 0.0.0-dev 是后续发行质量项，不是本阶段功能阻塞。详情与可重放用例见 [YOLO 资格记录](RUNTIME_QUALIFICATION_YOLO.md)。

## V0.2 实测更新

当前官方 HEAD 仍为 `3c5b227649371507ddc4eb46ac5133afe6db1cab`；最新 tag v3.15.1，GitHub Release v3.14.3 仍只有桌面资产。没有沿用未复查的版本。选定该固定官方源码构建 Windows SEA，SHA-256 为 `f40f4b6c8709ba2cc89ea000c3a6d97e8680ee4233720f737b6b7a506417ddf8`。

实测 CLI 版本 0.17.0，根 manifest 3.16.2，PE/Node 版本 24.14.0，builtin cache metadata 为 0.0.0-dev；这些版本域不能互相替代。构建中遇到的依赖资产与 Windows 空格路径问题、手动执行同版本 postject 的精确步骤及 provenance 全部在资格记录中披露，没有改上游源码。

当前 RDT headless 参数契约实测接受；实际 Provider 工具面包含 Read/Glob/Grep 与五个会话状态工具，20 个危险/扩展工具调用被拒绝。纯 stdin 文本不能执行 headless 请求；官方 app-server/agent-server 的 stdio 请求、读取、EOF 退出和在途取消均取得实证。生产协议本次没有切换。

结论为 **PARTIAL**：严格 builtin reads 无法覆盖 Windows 运维 API；headless `.env` 向上发现已确认；`/expert` 特殊命令存在静态 yolo 切换路径（依本阶段禁令未执行）；整机写入归因、完整网络与协议稳定性仍有缺口。现有 `ZcodeRuntime => RUNTIME_NOT_VALIDATED` 保持不变，不建议放行，不需以真实 Key 绕过阻塞。

## V0.1 历史记录

调查日期：2026-10-29。官方仓库：[zai-org/ZCode](https://github.com/zai-org/ZCode)。
本次通过 GitHub 连接读取默认分支、提交、文档、配置编解码、权限服务和打包源码；没有执行上游安装脚本或二进制。

基线：`3c5b227649371507ddc4eb46ac5133afe6db1cab`，提交时间 2026-10-20 06:23:05 UTC；README 最新版本标记为 3.16.2。源码版本标记与公开发行版本不能混为一谈。

## 核实结果

| 项目 | 当前官方实现 | RDT V0.1 采用方案 |
| --- | --- | --- |
| Windows | 文档提供 PowerShell 调用示例；SEA targets 含 win-x64、win-arm64 | C#/.NET 10 Windows 优先；本次仅验证当前 x64 主机 |
| 启动 | 无参数 TUI；`--prompt <text> --cwd <path> --mode plan --json --no-color` 可非交互运行 | 固定参数构造器；不开放任意额外参数 |
| 权限 | plan/build/edit/yolo/guarded；`--prompt` 默认 yolo | 永远显式 plan；REPAIR 无实现 |
| plan | 拒绝普通写入、命令；允许 readOnly 工具、非 destructive MCP、部分 session 能力 | 不称为 OS 沙箱；禁扩展，移除执行/编辑/模式切换工具；真实入口关闭待验证 |
| provider | 个人模型在数据基目录 `.zcode/v2/provider_config.json`；运行配置在 homedir `.zcode/cli/config.json` | 两份配置分别生成，个人配置 schemaVersion=1 |
| Key | `config.providerConfigRules.providerRules[].config.access`，type=api-key、apiKey；API 为 `api.type`、`api.baseUrl` | 解密后只写当前隔离目录，不用命令行或 Key 环境变量 |
| 用户目录 | file-config adapter 使用 node:os homedir；不是所有模块都认 DATA_BASE_DIR | 子进程同时覆盖 HOME、USERPROFILE、ZCODE_DATA_BASE_DIR |
| 额外路径 | 支持 PERSONAL_PROVIDER_CONFIG_FILE、STORAGE_DIR、SESSION_DB_PATH、LOG_DIR 等 | 显式指向 session；环境先清空再按白名单构造 |
| 配置发现 | 项目配置与向上发现的 .env 也可能参与 | 只用新建空 workspace；后续真实接入还须实测父目录发现行为 |
| 本地数据 | config、v2 credentials/provider、cli db、log、rollout、plugins/workflows、memory、mailbox、SEA 解包资源等 | home/data/workspace/runtime 全部置于 session；内部日志不导出 |
| 发行 | 根 distribution 为 tar.gz、依赖 Node；CLI SEA 可嵌 Node | 不安装 Node/pnpm、不运行上游构建；RDT 单独支持自包含目录发布 |
| 单文件 Windows | SEA 脚本支持 `zcode-windows-x64.exe` / arm64；可构建不代表已验证 | 公开 Releases API 本次仅返回 v3.14.3 桌面 dmg/exe，没有独立 CLI 资产 |

## 与最初假设的差异

1. 假设：旧式 provider 配置可直接使用。实际：版本化规则结构已替代旧 model/provider；旧字段只一次性迁移。影响：不能复用旧教程。方案：实现当前 schema，使用个人 provider `rdt-g`。
2. 假设：plan 就是严格只读。实际：权限服务对某些 MCP/session 能力放行，没有 OS 沙箱。影响：单靠模式或提示词无法承诺客户系统只读。方案：RDT 只暴露 READ ONLY，禁用扩展与危险工具；真实进程启动 fail closed，等待固定产物的拒绝测试。
3. 假设：仅设置 ZCODE_DATA_BASE_DIR 足够。实际：运行配置通过 homedir 定位。影响：可能读取客户配置。方案：覆盖所有相关用户/存储路径，清空继承的 ZCODE/NODE 等环境，仅允许最少系统变量。
4. 假设：可直接获得 Windows 独立发行版。实际：源码有 SEA 工具链，公开 Release 的 Windows exe 为桌面产品。影响：本次不具备可信独立 CLI 实测环境。方案：真实 Adapter 契约 + 真子进程 Mock；不伪造真实 API 接入成功。

## 已阅读的官方材料（固定提交链接）

- [README / distribution](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/README.en.md)
- [CLI README](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/README.md)
- [快速开始 / Windows / SEA / 隔离](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/docs/usage/01-quickstart.md)
- [启动参数](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/docs/usage/02-cli-reference.md)
- [配置与 Key](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/docs/usage/03-config.md)
- [工具面](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/docs/usage/04-tools.md)
- [权限文档](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/docs/usage/05-permissions.md)
- [权限服务源码](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/core/src/permission/service.ts)
- [CLI 参数解析](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/cli/src/arguments.ts)
- [用户配置路径](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/adapters/src/config/file-config.adapter.ts)
- [共享凭据路径](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/adapters/src/auth/shared-credentials.ts)
- [Provider 路径](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/packages/provider-node/src/runtime-paths.ts)
- [Provider 文件 codec](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/packages/provider-node/src/provider-config-file-codec.ts)
- [Provider 规则 schema](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/packages/provider/src/config/rule-data-schema.ts)
- [Bootstrap 配置约束](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/bootstrap/src/app/app-config-options.ts)
- [工具过滤](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/core/src/runtime/helpers/tool-allowlist.ts)
- [SEA targets](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/cli/scripts/sea-targets.mjs)
- [SEA build](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/cli/scripts/build-sea.mjs)
- [数据、日志、无默认沙箱声明](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/NOTICE.md)
- [公开 Releases](https://api.github.com/repos/zai-org/ZCode/releases)

## 真实接入的放行条件

### 待调查：不通过命令行传递 prompt 的官方入口

当前 headless 设计使用 `--prompt <text>` 传递任务描述。未来真实接入时，prompt 可能暴露在 Windows process command line、EDR/Sysmon 等进程创建审计中；RDT 不导出 prompt 到 Reports 不能消除这类外部审计记录。任务描述可能包含客户信息，因此这是启用真实 runtime 前必须处理的保密风险。

在固定版本的官方文档和源码中调查 `stdin`、`--stdio`、`app-server / agent-server`，以及其他不需要 command-line prompt 的官方入口，并用受控、无真实 Key 的测试核验输入协议、权限约束和日志/落盘行为。上述入口只是调查清单，尚未确认它们可直接替代当前 headless 契约；不得凭名称或猜测实现。本次不改变 ZCode 调用协议，不启用真实 runtime，不连接真实 API。

### 其他既有放行条件

额外实现细节：`provider-node/runtime-paths.ts` 的底层 resolver 要求 builtin 与 personal 两条路径成对出现；但 CLI `provider-runtime-env.ts` 入口接受只指定 personal，并准备/缓存随包 builtin，随后把两条路径交给 runtime。RDT 只设置 personal 路径，不能把空 JSON 冒充 builtin 配置。真实接入还须核验 builtin 刷新与缓存的网络行为及临时落盘位置。

取得可信、固定 hash/版本及许可证材料的 Windows CLI（无需主账号登录）；在临时目录实测 --help、doctor、provider schema 与工具清单；使用本地受控 provider（无真实 Key）验证 Read 成功、Write/Edit/Bash/模式切换/子任务/MCP/插件失败；验证配置与 .env 不越界，进程树停止后无残留 profile。完成后才实现启用逻辑，不能用一个用户开关跳过。
