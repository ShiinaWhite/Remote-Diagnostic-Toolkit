# 安全模型

目标是防止普通查看泄露 Key、未持 PIN 者直接使用工具、以及日常运行遗留明文 profile。不防当前用户权限下的恶意进程、管理员/调试器、内存抓取、离线长期暴力破解、恶意改版 runtime 或专业逆向。

## 凭据

版本 1 JSON envelope 包含 Version、Provider、Kdf、N/R/P、Cipher、Salt、Nonce、Ciphertext、Tag。

- scrypt：N=131072、r=8、p=1，32 字节输出，约 128 MiB 主工作内存；BouncyCastle.Cryptography 2.7.0。
- salt 16 字节、nonce 12 字节，均由系统密码随机源生成。
- AES-256-GCM，16 字节 authentication tag；.NET 内置实现。
- 固定规范 AAD 绑定格式版本、Provider、KDF 参数和 cipher。
- 只接受版本 1、G 与固定算法/参数，先检查文件/字段长度，拒绝任意 KDF 成本、超大文件、未知版本、无效编码。
- 错 PIN 与篡改均返回固定错误，不输出解密细节。写入 CreateNew，不覆盖原凭据。

PIN 为 8–128 UTF-8 字节，不记录、不传参数、不放环境变量；创建时二次确认。短 PIN 即使有 KDF 仍可能离线猜解，应优先用较长随机 PIN/口令。无硬件绑定或 DRM。秘密数组在 finally 中尽力清零；不承诺消除 CLR/第三方库内部副本、swap、crash dump 或上游进程内副本。

Windows 新目录/凭据/日志在创建时设置受保护 DACL，仅当前用户 FullControl，子目录继承。不会改变客户现有目录 ACL。无法建立私有权限就失败，不降级为明文或共享权限。当前用户和管理员仍可访问；ACL 不是防管理员的加密。

为兼容上游格式，解密 Key 会短暂存在 session 私有 provider JSON 中。它既不是长期明文配置，也不是内存专用方案。该文件不进入报告，退出后删除；异常强杀后可由后续清理发现。

## 隔离和只读

HOME、USERPROFILE 指向 session/home；ZCODE_DATA_BASE_DIR 指向 session/data。运行配置、个人 provider、DB、日志、mailbox、TEMP/TMP/TMPDIR、APPDATA/LOCALAPPDATA 和 XDG 路径均指向 session。进程环境先清空，只从父进程保留最少 Windows 系统定位变量，不继承 PATH、NODE_OPTIONS、既有 ZCODE 配置或 API 凭据。

这属于 profile/数据隔离，不是 OS 权限沙箱。进程理论上仍有当前账号权限。官方 plan 还允许某些 MCP 与会话操作；denylist 无法约束未知产物。V0.1 因此只启用受控 Mock，真实 runtime fail closed。没有 Yolo、修复、管理员提权、Defender/防火墙/ExecutionPolicy 变更或自研命令执行能力。

后续真实接入必须验证启动入口对 personal/builtin 配置的成对处理、内置配置刷新网络行为、父目录 .env 发现、SEA 解包缓存及所有实际工具。当前不宣称通过环境变量能约束所有未知上游代码。

## Prompt 的 command-line 暴露风险

当前 headless 启动契约通过 `--prompt <text>` 传递任务描述。未来启用真实 runtime 时，prompt 可能出现在 Windows process command line，以及 EDR/Sysmon 等进程创建审计中。即使 RDT 不将 prompt 写入 Reports，这些外部记录仍可能保留客户任务、路径或业务信息；参数转义和 `UseShellExecute=false` 不提供保密性。PIN 和 API Key 仍不得作为命令行参数传递。

启用真实 runtime 前，必须调查并以固定版本官方文档、源码和受控测试确认 `stdin`、`--stdio`、`app-server / agent-server` 以及其他不需要 command-line prompt 的官方入口，核验其协议、只读权限和数据落盘行为。这些是待调查候选，不代表已确认支持；不得凭猜测实现或切换协议。本次不改变 `--prompt` 调用契约，真实 runtime 保持 disabled，也不接真实 API Key。

## 清理

创建失败时使用本次调用持有的精确 marker 回滚；owner.json 已成功而 .lease 创建失败时，回滚不依赖再次创建 .lease。marker 尚未写完时，只移除本次新建根中的已知 marker 文件和空目录，不递归删除未知内容。回滚失败明确抛出固定失败标识，不静默吞掉。

对磁盘上发现的合法 session，缺失 .lease 表示 Orphaned，近期目录保留，超过 24 小时后才可被 FindStale 发现并安全清理；缺失 lease 绝不替代路径、GUID、marker、时间和 reparse point 检查。只有打开已有 .lease 时的 Windows sharing violation 才返回 Active；其他租约 I/O、删除文件被占用或删除阶段 I/O 返回 Failed，并尽量保留 marker/lease 供重试。清理运行数据期间保持取得的排他租约。

清理器只允许指定 temp 根的直接子目录 `RDT-<32位GUID>`；检查标记版本、GUID 一致、时间合理、排他租约。未持有合法标记的目录不删除。删除前和逐级删除时拒绝 reparse point，不跟随 junction/symlink。活动 session 不删除；异常遗留清理需超过 24 小时并显式执行命令。

目录标记不是对恶意同账号攻击者的认证机制。当前方案也没有使用基于句柄的完整竞态防护；同用户恶意替换目录仍不在防御目标内。预检与逐级检查降低误操作风险，不能称为抵抗本地专业攻击。

正常、异常、子进程崩溃与 Ctrl+C 经 finally 尝试清理。断电、任务管理器强杀、文件被占用、磁盘故障会留下残留；V0.1 不具备 wrapper 强杀时的 Job Object 进程生命周期保证。清理失败明确标记，不能报告成功。应用层删除不保证物理不可恢复。

报告保留独立元数据，raw stdout/stderr、客户 prompt、上游 rollout/session、完整 provider 和 Header 不复制、不显示。固定错误码避免异常信息夹带秘密。报告本身含运行时间、session ID 等，应由操作者按客户授权保管。
