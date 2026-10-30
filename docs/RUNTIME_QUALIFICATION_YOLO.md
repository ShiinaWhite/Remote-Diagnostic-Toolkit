# V0.2 Phase 2 — YOLO Runtime Qualification

STATUS = PARTIAL。完整运维能力取得实证，**生产 Adapter 仍保持 `RUNTIME_NOT_VALIDATED`**。后台脱离子进程清理、普通用户权限和完整落盘归因仍未关闭，不建议解锁生产。

## 产品方向决策

2026-10-30：默认运维目标改为 **YOLO / full Agent capabilities**。RDT 负责 credential protection、runtime identity、profile isolation、session isolation、transport、cleanup 和 reporting。READ ONLY 为 optional future mode；不开发独立 Windows 只读采集层。

Phase 1 的 READ ONLY 实验及脱敏证据保留不变。其“严格只读为默认产品目标 / 新建只读采集层”的建议标记为 **SUPERSEDED BY PRODUCT DIRECTION DECISION**，不再据此阻塞 YOLO 功能资格。

## 本阶段实验协议

- 固定 Phase 1 SEA 的 SHA-256、源码 commit 与 CLI 版本，不使用漂移版本。
- 独立开发 harness 通过 `agent-server --stdio`，以官方 `session/create` 显式 `mode=yolo`；不设置只读 toolAllowlist/toolDenylist，不用 `--prompt`。
- 仅关闭 dynamicWorkflow/offPeak/title generation，正常 Bash、Write、Edit 保留；从实际 loopback Provider 请求记录工具 schema。
- 固定脚本查询真实 Windows 系统信息，实际结果只留本地，公开证据只保留结果类型、行数、退出状态和脱敏摘要。
- 写入只作用于新建 Session workspace 和唯一 HKCU canary；所有 canary 必须最终不存在。
- 不主动提权、不修改 ExecutionPolicy、Defender、安全策略或真实业务配置；记录实际 token 与权限错误。
- 正常退出、tool 成功/失败、模型取消、tool 取消、wrapper 取消及受控脱离子进程分别验证。先记录存活，再清理精确属于测试的残留，人工兜底不能计作 runtime cleanup PASS。
- 受控父 `.env` 与固定本地 builtin 配置分别实测；不接受空 JSON 或绕过 schema。
- NotSigned 与 0.0.0-dev 继续记录，归入 distribution/release quality，不作为本阶段功能资格硬 blocker。

本阶段不解锁生产 Adapter，不 merge main，不使用真实 G/D 模型 Key。

## 固定身份

```text
UPSTREAM_COMMIT = 3c5b227649371507ddc4eb46ac5133afe6db1cab
UPSTREAM_VERSION = root 3.16.2 / CLI 0.17.0
RUNTIME_SOURCE = Phase 1 official fixed source win-x64 SEA, unchanged
RUNTIME_SHA256 = f40f4b6c8709ba2cc89ea000c3a6d97e8680ee4233720f737b6b7a506417ddf8
```

沿用已完成 provenance 的同一文件，不重新构建或替换。构建记录、Node/pnpm/lock、许可证/NOTICE、NotSigned 与 0.0.0-dev 见 [Phase 1](RUNTIME_QUALIFICATION.md)。后两项仅为 distribution/release quality，不阻止本次功能实验；RDT LICENSE = UNDECIDED。

## 开发 harness 与可重放命令

`tools/Rdt.YoloQualification` 和 `scripts/runtime-qualification/{qualify_yolo,yolo_fixture,summarize_yolo,test_yolo}.py` 只用于开发资格测试，不在 Rdt.slnx 或客户发布依赖图中。C# 与 Python 都验证候选完整 SHA-256，C# 再验证本地 builtin hash；模型 endpoint 只接受 `http://127.0.0.1:<port>/v1`。只接受固定用例名，不提供任意可执行文件或用户命令接口。

```powershell
dotnet restore tools/Rdt.YoloQualification/Rdt.YoloQualification.csproj --locked-mode
dotnet build tools/Rdt.YoloQualification/Rdt.YoloQualification.csproj -c Release --no-restore
python -m unittest discover -s scripts/runtime-qualification -p test_yolo.py -v
python scripts/runtime-qualification/qualify_yolo.py --candidate <fixed-SEA> --builtin <fixed-official-builtin-json>
python scripts/runtime-qualification/summarize_yolo.py
```

真实系统输出、完整 Provider requests、protocol frames、PID/启动时间、环境、Session 文件 hash、watcher 事件和异常只保存在忽略的 `artifacts/runtime-qualification/yolo/`。不上传 CI artifact。公开 [脱敏汇总](runtime-qualification/yolo-evidence-summary.json) 保留能力、行数、工具名称、文件差异及清理状态；[schema 投影](runtime-qualification/yolo-tool-schemas.json) 取自实际 Provider 请求，省略说明文字并记录原参数 schema hash。没有使用模型生成或伪造系统输出。

## Stdio 与 YOLO Session

固定 argv 只有 `agent-server --stdio --cwd <session-workspace>`。通过官方 NDJSON 方法依次执行 `session/create`、`session/send`、`session/read`，取消用 `session/stop`，关闭 stdin EOF 后等待退出。任务文本只在 `session/send.content`，不进入 ZCode process command line。

`session/create` 明确 `mode=yolo`，不设置 `toolAllowlist` 或 `toolDenylist`。只设置 `dynamicWorkflowEnabled=false`、`offPeakToolEnabled=false`、`titleGenerationEnabled=false`。私有运行配置只有 `permission.mode=yolo`，没有沿用 Phase 1 的 MCP/Skill/插件/Agent 禁用配置。反向交互审批请求由 harness 明确拒绝；不模拟操作者批准。实际测试未依赖交互审批。

实际 session/read 的 **settings.permission.mode=yolo**，不只检查发送的参数。`session.mode` / `projection.mode` 同时为 build，这是不同的协议状态域，不能将它误认为权限退回 build 或把两者合并。每次实际出现两次 `session/requestRuntimePreferences` 反向请求；harness 返回 MethodNotFound，不提供宿主偏好服务，随后权限仍为 yolo，业务工具成功。该宿主协商行为公开记录，未来生产协议实现须专门 Review。

Phase 1 已验证两个 server 别名；本阶段选 Agent Server 是因为官方方法直接对应 Agent Session，便于隔离生命周期，不是声称它有独立的更强安全边界。协议仅固定当前源码版本，不承诺上游将来兼容；没有实现私有协议或修改生产 Adapter。

任务经 stdin 不表示所有内容都不落盘：model IO、DB、日志可能包含任务和工具结果，仍在 Session 内清理。工具启动的命令本身仍可能进入 Windows 进程审计。

## 实际 YOLO 工具面

工具名称与参数来自**运行中的真实 Provider request**，不是源码常量。完整名称在脱敏 JSON。核心与外围结果：

| 工具 | 实际情况 |
| --- | --- |
| Bash / Read / Write / Edit | 已注册并实际调用成功 |
| Glob / Grep | 未注册；强制请求实际返回 Tool not found。固定源码在 Bash 可用时使用 embedded-search 分支，不手动改工具面来凑齐名称 |
| ApplyPatch | 未注册；强制请求返回 Tool not found，未产生 patch 文件 |
| Agent / Skill | 已注册；本阶段记录 schema，不声称完成子 Agent/Skill 执行资格 |
| CronCreate/Delete/List/Update | 仍注册，不能因 offPeakToolEnabled=false 宣称 Cron 已禁用；未实际创建 Cron |
| EnterPlanMode / ExitPlanMode | 正常 YOLO 工具面存在，不设置 Phase 1 模式限制 |
| WebFetch | 已注册；未执行外部 fetch |
| WebSearch / Workflow 创建工具 | 当前实际请求未注册 |
| mcp__node_repl__js | 已注册；未执行 JS/浏览器 |
| documents/pdf/presentations/spreadsheets plugin MCP tools | 随包插件工具已注册；不是只缓存未启用的 Phase 1 状态，未调用业务文档插件 |
| AskUserQuestion、ReadSessionContext、ReplyToChannel、SendMessage、TaskOutput/TaskStop、TodoRead/TodoWrite | 实际注册；不会因为本阶段没调用就从工具面隐藏 |

Glob/Grep/ApplyPatch 缺失是固定版本的实际工具契约；Windows 运维能力由 Bash/PowerShell 验证，不为得到 PASS 增加自研收集层。

## Windows 运维与受控写入

所有 PowerShell 通过真正的 Bash Tool 调用固定绝对路径 `powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand <synthetic-script>`，不修改 ExecutionPolicy。Provider 的后续 `role=tool` 消息必须含实际输出；不以 ZCode 退出码 0 代替证明。

| 用例 | 结果 / 真实工具证据 |
| --- | --- |
| Get-Service | PASS，5 条，Name/Status/DisplayName |
| Get-Process | PASS，5 条，Id/ProcessName |
| Get-WinEvent -LogName System -MaxEvents 1 | PASS，1 条，事件元数据；未读取 Message |
| Get-CimInstance Win32_OperatingSystem | PASS，1 条，版本/架构字段 |
| Get-NetTCPConnection | PASS，5 条，状态/端口/PID；公开汇总不含实际值 |
| Get-ScheduledTask | PASS，5 条；完整结果只留本地 |
| Get-ItemProperty | PASS，读取本次唯一 HKCU canary sentinel |
| Read | PASS，真实返回合成文件 sentinel |
| Write / Edit | PASS，工具成功返回与文件 SHA-256 同时验证；上游可能将随后冗余 Read 优化为 wasted call，不能因此抹去实际写入 |
| 删除文件 / 创建目录 | PASS，仅 Session workspace；工具结果与文件差异/存在性验证 |
| 普通子进程 | PASS，PowerShell 实际调用 cmd /d /c echo，工具结果包含 sentinel 和真实退出码 0 |
| HKCU canary CRUD | PASS，创建值、读取、修改后读取、删除后不存在；finally 兜底前已确认 absent |

每次 canary 位于 `HKCU\Software\RdtQualification-<session-guid>`，创建前必须不存在。harness 最终再次检查；兜底删除不能计入 Agent CRUD 成功。没有改真实服务、注册表业务配置、计划任务、系统安全或客户文件。

Windows PowerShell 的默认非 ASCII 输出在当前 Bash/Node 解码链中可出现替换字符；本次真实行数、JSON 字段与 ASCII canary 的证据有效，但不能据此声称中文文本传输已合格。后续需单独验证 UTF-8/CLIXML/原生命令编码约定，不能把修复后的文本伪装成原始结果。

### 权限观测范围

本开发执行环境实际继承 **管理员 token**，由 launch 元数据及 Agent 工具中的 WindowsPrincipal 双重确认；不是 RDT/Agent 发起的提权。`fsutil dirty query <SystemDrive>` 只作只读权限观察，不执行 dirty set 或其他状态修改。管理员结果不证明普通用户能力。

尝试使用本账号 linked limited token 的预检发现没有可用 linked token（Windows 1312）；创建本账号降权副本后启动又被 Windows 拒绝（5）。没有为此启用 privilege、修改 UAC/安全策略或取得其他账号 token；未将这个失败的开发启动实验加入客户代码或公开执行入口。**普通用户与标准 UAC 边界 = NOT PROVEN**，需普通用户测试环境补测，不能把当前管理员测试写成普通权限 PASS。

## 父 .env 隔离

受控 `parent/.env → child/RDT-<GUID>/workspace` 放入 `RDT_DOTENV_CANARY=SHOULD_NOT_LOAD` 和 `ZCODE_BASE_URL=<loopback-canary>`；环境清空后启动固定 SEA Agent Server。实际 Bash/PowerShell 返回这两个变量均 null，fixture 无控制面 GET，个人模型请求正常完成。

```text
PROTOCOL_PARENT_DOTENV_ISOLATION = PASS
--prompt headless = vulnerable to ancestor .env discovery (Phase 1 historical experiment)
production SEA protocol server = verified not to load it (this fixed build/entry point)
```

源码只在 development runtime 的 protocol 启动路径加载 CLI dotenv，与实测一致。没有删除或重写 Phase 1 headless 漏洞证据；也不外推到 development Node 启动、其他版本、工具自行加载 dotenv 或用户指定脚本。

## 本地 builtin 与网络

```text
BUILTIN_SOURCE = config/provider/zcode-builtin.test.json @ fixed upstream commit
BUILTIN_GIT_BLOB = 7e3c6dd1f55e751d87984cfdabc44043738214fc
BUILTIN_SHA256 = e4e8e3527fa131195e3dc009d4fac9e5b403834281e9ecdddaa68850dc2cb7c0
BUILTIN_SCHEMA_VERSION = 1
BUILTIN_REVISION = 33
```

这是官方构建默认 test 环境使用、实际随该 SEA 构建准备的同一文件，21 个 provider templates；与 dist/provider/zcode-builtin.json byte-for-byte 相同。不伪装为官方 production config，不使用空 JSON、不修改 schema、不下载第三方重打包配置。完整内容从固定官方 checkout 拷贝入新 Session，执行前验证 hash。

子进程同时指定 `ZCODE_BUILTIN_PROVIDER_CONFIG_FILE=<session-local>` 与 `ZCODE_PERSONAL_PROVIDER_CONFIG_FILE=<session-personal>`，不设置 `ZCODE_BUILTIN_PROVIDER_BUNDLED_CONFIG_FILE`。固定源码入口在显式成对路径时直接返回；provider registry 的 remote synchronizer 依赖 bundled env 的分支不会构造。个人 fixture Provider 正常工作。

实际启动、工具调用及闲置 66 秒（超过源码 60 秒刷新间隔）均未向 loopback control-plane canary 发出 GET；50 ms IPv4 TCP 样本只有 fixture loopback，没有外部目标。结合固定源码链，**该配置下 builtin 控制面/CDN刷新路径 = ELIMINATED**；这不等价于完整网络审计或所有 YOLO 工具离线。扩展、登录、浏览器及主动网络工具未调用，观察限制见 [网络记录](ZCODE_NETWORK_BEHAVIOR.md)。

## Profile / filesystem diff

候选使用 V0.1 全部 HOME/USERPROFILE、APPDATA/LOCALAPPDATA、XDG、TEMP/TMP/TMPDIR、ZCODE data/storage/db/log/mailbox/personal 重定向，再增加成对固定 builtin 与受控 Windows 系统 PATH/PATHEXT/ComSpec。**不是继承用户 PATH 或启动任意客户程序的生产接口**。

先回放不增加系统环境的原始组，保存为 `artifacts/runtime-qualification/yolo-initial-environment/` 和 [原始环境脱敏证据](runtime-qualification/yolo-initial-environment-summary.json)。该组 PowerShell builtin reads 成功，但 native cmd 返回/退出证据无效；取消后的 PowerShell 残留。固定源码取消通过 bare `taskkill`，缺少系统 PATH 会影响它；因果范围仅在受控重跑后判断。

候选环境的全组测试真实 `~/.zcode` 前后元数据不变。私有 DB、rollout/model IO、logs、builtin/personal config、随包 plugins/skills 和 runtime tools 均在 Session 内，随 Session 删除；每项文件 hash/类型计数与 workspace 新增/修改/删除差异见脱敏证据。原始系统输出不复制到 Reports。

真实 USERPROFILE、APPDATA、LOCALAPPDATA、TEMP、ProgramData 仍有 watcher 覆盖，但它不能提供 PID 归因。活跃开发机背景事件、外层测试 PowerShell 的启动缓存等未归因，不删除未知文件。没有证明干净 VM 整机无变化、启用每种扩展/浏览器后的路径、或未来任意命令的副作用。因此 **PROFILE_ISOLATION = PARTIAL**，不能写成环境变量构成强制沙箱。

## 清理资格与阻塞

模型在途取消：确认 fixture 已收到请求后 `session/stop`，实际观察 HTTP 连接中止；不是只看 ACK。工具取消：先等测试 PowerShell 的 PID/精确创建时间 marker，再停止。wrapper 取消另外触发真正的 CancellationToken，并由 wrapper 的 stop/EOF 路径结束。成功/失败工具完成后正常 EOF；只有等待退出超时才记录强杀兜底，兜底不能冒充正常退出。

受控系统环境全组 **26 项：22 PASS、3 UNAVAILABLE（Glob/Grep/ApplyPatch）、1 FAIL（detached-child）**。实际 Provider 工具面共 42 个。前台工具 stop ACK 为 316 ms，wrapper token 取消后的 stop ACK 为 8 ms；两项最终均无测试 PowerShell 存活，没有进程强杀兜底。工具失败实际返回 Exit code 7 和受控错误，ZCode 仍退出 0，印证退出码不能代替工具结果。

所有 Session 最终 `Cleanup=Complete`，GUID HKCU canary 最终 absent；这包含 harness 对已确认测试进程的兜底，**不代表所有 runtime lifecycle 用例 PASS**。

受控脱离实验用 `Start-Process -WindowStyle Hidden` 创建有界等待的 PowerShell 子进程，写本次 Session ID、PID 和精确 StartTime ticks；父工具完成后让 ZCode 正常退出。子进程仍存活时先记录 FAIL，再核验身份回收。已退出的父进程树不能可靠寻址这类后代；`Kill(entireProcessTree)` 不能作为完整保证。

**阻塞：后台脱离进程生命周期尚未受 wrapper 约束。** 下一次设计/外部 Review 应评估 Windows Job Object 等由 wrapper 持有的生命周期约束，并重测正常、stop、工具失败、wrapper 取消/强杀及脱离进程；本阶段不擅自实现或放行生产。普通用户测试、整机 profile 归因与未执行扩展的资格范围也须补齐。NotSigned/dev metadata 另作后续发行质量处理。

回答核心问题：固定 runtime 已证明具备真正的 Windows 运维与受控写入能力，stdio 任务传输、父 dotenv 隔离与固定本地 builtin 可用；**尚不能证明其完整进程生命周期及所有自身 Profile/缓存行为都被 Session 清理覆盖**。STATUS 保持 PARTIAL，提交当前分支等待 Review。

## 回归与发布验证

Foundation 69/69、Phase 1 harness 11/11、Phase 2 evidence evaluator 15/15 PASS。Release solution build 与两个开发 bridge build 均 0 warnings / 0 errors；win-x64 self-contained publish 成功，发布图不含 qualification bridge 或 ZCode 候选。RID locked restore 使用 `dotnet restore src/Rdt.Cli/Rdt.Cli.csproj -p:RuntimeIdentifier=win-x64 --locked-mode`，匹配已提交的 packages.win-x64.lock.json；随后 publish --no-restore。生产源码、原测试与 Phase 1 evidence-summary.json 对 f604a207 未变化。

公开候选文件与 Git 全历史执行本地规则扫描（含 UTF-16）及 Gitleaks，未发现秘密。CI 只运行 locked restore、Release build、foundation 与开发 harness 自测，不下载/执行候选，不请求真实 API、不上传任何 artifact。实际 YOLO 资格实验中的 FAIL 保持 FAIL，不因回归通过而改为总体 PASS。
