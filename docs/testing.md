# 测试与实机验收

测试项目分为 Unit、Integration、Service、UI 和 Support；以下命令均在仓库根目录执行。

CI 仅执行 `[单元]` 用例，不要求安装 Redis，也不会运行实机服务测试。集成与实机结果由本地 Windows Redis 5 环境验证。

## 开发与验证

在解决方案根目录执行：

```powershell
dotnet build FixRedis.sln -c Release
dotnet run --project tests/FixRedis.Tests -c Release --no-build -- --headless
dotnet run --project tests/FixRedis.Tests -c Release --no-build -- --private-desktop --filter WinForms
./scripts/publish.ps1
```

测试项目沿用无第三方测试框架的可执行验收程序，失败返回非零退出码（不要用 `dotnet test` 代替上述命令）。完整验收需要本机 `C:\Program Files\Redis` 中的 Redis 5 工具，以及正常运行的本机 Redis 用于只读核验。破坏性场景仅使用独立端口、工作区 `artifacts/tests/` 中的配置和数据；不停止、重启或修改本机现有服务。`--headless` 不显示窗口；界面烟测使用独立 Windows 桌面且不切换用户桌面。每次运行保存 `test-report.txt`、`test-results.json`，集成用例保留配置、损坏文件备份及操作日志，界面用例保存截图。

覆盖 RDB 截断/CRC、AOF 尾部/中间损坏、事务、RDB 前导区、空文件、全量丢失确认、旧 RDB 隔离、备份失败、文件占用、数据变化、认证、错误 PID、启动错误、超时、日志脱敏、文件权限、跨进程互斥和界面交互。

默认套件的 Windows SCM 查询使用真实本机服务验证；恢复场景使用真实隔离 Redis 子进程。直接针对已安装服务的完整测试必须显式使用下面的实机入口。

### 直接测试本机 Windows 服务 Redis

```powershell
./scripts/test-local-service.ps1
```

此入口使用 `WindowsRedisPlatform`、本机 `redis-server.exe`、配套检查器及正式 `RepairService`，不使用模拟服务或独立端口。需要管理员权限；非管理员运行会触发 UAC，测试进程窗口隐藏。服务会反复停止和启动，测试期间不能依赖此 Redis 提供业务服务。

先保存当前内存数据、停服，完整备份安装目录及实际数据目录，保存原路径、大小、SHA-256 并校验。然后在实际服务配置及数据文件上测试 AOF，逐项记录真实服务启动失败日志、修复结果、保留键值及第二次重启结果。自动化测试为“接受”和“拒绝”分别提供确认回调，界面弹窗仍由独立界面烟测覆盖。

测试无论成功或失败都进入恢复流程：校验备份，恢复测试前配置及 RDB/AOF，启动原服务并验证。恢复仅用于撤销本次测试，不是修复工具的历史备份恢复功能。Redis 日志、测试产生的隔离文件及完整备份保留，以便核查。若进程被强制结束或机器断电，`finally` 无法保证执行，应根据完整备份人工恢复；不要在测试中关闭进程。

报告及完整备份在 `artifacts/service-tests/<时间戳>-<唯一编号>/`：`service-test.log` 为过程证据，`results.json` 同时记录测试结果与原服务恢复结果，`full-backup-manifest.json` 是完整文件清单，`full-backup/` 是目录副本，`repairs/` 保存每个场景的修复前备份。任一场景失败会停止后续注入并恢复原服务，退出码非零。

### AOF 专项测试

新增 34 个单元用例和 17 个真实 Redis 集成用例。可分别执行：

```powershell
dotnet run --project tests/FixRedis.Tests -c Release --no-build -- --filter '[单元]'
dotnet run --project tests/FixRedis.Tests -c Release --no-build -- --filter '[集成]'
```

| 场景 | 验证内容 |
| --- | --- |
| 尾部半条命令、CRLF 缺失、UTF-8 值截断、中间 RESP 损坏、缺少 EXEC | 先证明真实 Redis 无法启动，再验证截断修复、有效前缀字节和键值、再次重启可加载 |
| 有效 RDB 前导区 + 损坏 AOF 尾部 | 保留前导区和完整命令，加载比旧 RDB 更新的数据 |
| 前导区 CRC 错误、无有效前缀 | 拒绝则保留原文件；明确接受后空库启动；备份字节及 SHA-256 不变 |
| RESP 正确但命令名称损坏 | 检查器通过，但真实启动失败；只依据新增加载错误日志请求空库确认 |
| 截断后仍包含未知命令 | 启动失败后拒绝清空，保留已应用的有效前缀和原始备份 |
| 空/缺失 AOF、旧 RDB 损坏、双持久化 | 不回退加载旧 RDB；健康运行实例不重启、不修复 |
| 检查器超时、权限错误、复检失败、输出统计矛盾/溢出 | 不应用未验证的工作副本，不误报成功或自动清空 |
| 备份失败、确认回调异常、空库启动失败、启动后验证失败 | 断言工具调用、启动和确认次数、原文件及备份状态，禁止循环重试 |
| 环境错误、旧日志及无关联跨行关键词 | 不将权限、磁盘、内存、端口或认证问题判定为数据损坏 |

需要证明尾部损坏阻止启动的集成用例显式设置 `aof-load-truncated no`；此设置只作用于临时测试配置。否则 Redis 自身可能接受并截断不完整尾部，无法证明修复前启动失败。

已实测的边界：多余的 EXEC 会被检查器判为异常，但本机 Redis 5 仍能启动，工具应对健康实例报告无需修复；错误参数数量的 SET 可导致本机 Redis 5.0.14.1 崩溃，检查器仍通过，通用崩溃日志不足以安全判断损坏，因此结果是保留数据并报告失败。这两个用例通过表示行为符合预期，不表示所有损坏都能修复。

测试覆盖上述分支，不代表覆盖所有 Redis 故障，也不模拟物理断电、磁盘硬件损坏或每种命令的语义错误。Redis 7 多文件 AOF、集群和副本仍不在支持范围内。

发布位置为 `artifacts/publish/win-x64/`，为 Windows 10/11 x64 自包含单文件程序，目标机不需要安装 .NET。
