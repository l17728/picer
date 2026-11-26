# IndexService 缩略图缓存加密（2026-11-26）

## 范围与实现

本阶段覆盖 IndexService `thumbs/*.jpg` 缓存文件的静态加密，不代表 Relay SDD 中所有派生媒体均已加密。

- 每个 IndexService 数据根创建稳定的 256 位缓存密钥；密钥由 ASP.NET Data Protection 封装保存。Unix keyring 目录限制为仅服务用户访问，密钥文件限制为仅服务用户读写；Windows 使用 DPAPI。
- 新缩略图以随机 nonce 和 AEAD 标签加密写入临时文件，再原子替换目标文件。关联数据绑定缓存文件名，避免密文在缓存路径间互换。
- 算法协商优先使用平台 AES-256-GCM；平台不支持时使用 XChaCha20-Poly1305。XChaCha 通过 HChaCha20 派生子密钥，公开测试向量用于核验实现。
- HTTP/本地 Gateway 读取缓存时解密到内存流；旧明文缓存会在首次读取时原子迁移。服务启动后另有后台遍历迁移，迁移错误逐项计数，不阻断 HTTP 启动。
- 当前索引缩略图为可再生缓存。服务响应仍为 JPEG 明文；只有服务器缓存目录中的静态文件受此阶段保护。

## 验证

- `MediaFileCipherTests` 覆盖每空间密钥稳定/隔离、AES-GCM authenticated encryption、上下文绑定、篡改检测、HChaCha20 标准向量、XChaCha suite、缩略图新建加密、旧缓存迁移。
- Gateway HTTP 测试以真实 IndexService handler 验证磁盘密文可被服务端读取为有效 JPEG。
- 定向测试：人物状态回放、媒体加密、Gateway 缩略图共 28/28 通过。
- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误。

## 未闭合范围

照片/视频分析派生图、视频关键帧、GUI/驻留程序本地缓存、Relay 传输端到端加密、租户密钥轮换/恢复策略及 RELAY-01..40 的完整验收不由本阶段覆盖。不得据此宣称完整媒体加密或 Relay 交付完成。

XChaCha20-Poly1305 的 nonce 分解及 HChaCha20 算法按公开的 XChaCha Internet-Draft 参考：[draft-irtf-cfrg-xchacha](https://datatracker.ietf.org/doc/draft-irtf-cfrg-xchacha/01/)。该链接是过期的 Internet-Draft，代码主要依赖 .NET `ChaCha20Poly1305` 实现标准 96-bit AEAD 原语。
