# 跨重启目录增量扫描实现（2026-10-09）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
> 后续衔接：持久扫描已纳入最新统一验证；扫描清单加载、保存及手动清除时，还会在跨进程锁内清理精确命名的崩溃临时文件。已增加 watcher 迟到通知的有界稳定等待测试，仍要求完整扫描且不推进不完整清单。正文提交时“尚未测试”是当时分工记录。
<!-- /historical-status-2026-10-09 -->

## 行为与安全边界

目录扫描增加持久目录清单和只读 NTFS USN 变更水位。首次扫描枚举子树、记录目录及媒体文件引用号；重启后验证卷序列号、根目录引用号、日志 ID 和有效 USN 区间，回放变更后只枚举受影响目录。没有变更时复用目录中的文件名清单，仍检查已知目录和候选文件元数据，因此并非全部处理均为 O(变更数量)。照片和视频均支持。

没有卷读取权限、非 NTFS、网络目录、日志不可用/重建/截断、未知记录版本、损坏清单、强制校验、监控断档均回退完整扫描。不会创建或修改操作系统日志，不要求用户提升权限。日志回放限制 64 MiB、10 秒，超限完整兜底。完整兜底将所有媒体标为需内容校验，避免同大小、同时间戳的内容替换被旧 SHA 隐藏。

扫描前后取水位，回放尾部增量；枚举期间发生相关变化、watcher 变化、异常、取消或根缓存被淘汰时，不发布可用于 missing 判定的完整快照。重新扫描后再决定缺失。持久化水位不代表调用者已经导入数据库：待校验内容路径保持到下一次确认索引签名与重新读取内容一致；已确认 SHA 同时保留，防止另一进程的旧索引错误复用。硬链接在子树外写入也按文件引用号使子树中的对应文件失效。

## 缓存生命周期及彻底清理

默认清单位于应用数据目录的 `scan-manifests`；可用 `PICER_SCAN_CACHE_ROOT` 指定位置。采用校验和、临时文件及原子替换，单清单最多 64 MiB、最多 16 个/总计 256 MiB。清单保存失败不会破坏正常媒体扫描。内存最多四个根，闲置和退出释放 watcher。

`DirectoryMediaChangeTracker.Shared.ForgetFile(path)` 删除覆盖路径的清单并失效内存。缓存目录全局随机 epoch 与跨进程命名互斥锁阻止早先扫描的延迟保存恢复已清理路径。epoch 不保存媒体路径、文件引用号或 SHA；清理失败向调用者返回异常，不能宣称已经彻底清理。清理后其他根清单也因 epoch 不匹配而失效，下次扫描安全回退。

## 验证范围

`PersistentDirectoryScanTests` 覆盖连续水位跨实例复用、仅重枚举变更目录、日志不可用/重建/截断/未知格式/清单损坏回退、同大小同时间戳内容变更、未提交导入的重启恢复、取消不推进水位、硬链接变更解析，以及彻底清理拒绝另一存储实例的延迟写回。

提交实现时尚未在此 agent 执行构建与测试，由主 agent 统一运行；模拟日志测试不等于已经完成不同 Windows 权限、真实 USN 回卷、网络盘和大图库性能验收。

## 协议依据

- [READ_USN_JOURNAL_DATA_V0](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v0)
- [USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)
- [USN_JOURNAL_DATA_V0](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_journal_data_v0)
- [Walking a Buffer of Change Journal Records](https://learn.microsoft.com/en-us/windows/win32/fileio/walking-a-buffer-of-change-journal-records)
