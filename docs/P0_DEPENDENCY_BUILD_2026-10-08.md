# P0 依赖与严格构建处理（2026-10-08）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 当前结论

P0 已完成。用户确认没有 Six Labors 许可证后，已将 ImageSharp 完整替换为 Magick.NET-Q8-AnyCPU 14.17.2。严格构建、普通回归和离线图像模型验证均通过。未禁用 NuGet 审计、未压制告警、未绕过许可证校验。

官方安全公告将 TIFF CCITT T6 问题的修复版本列为 4.1.2：
https://github.com/advisories/GHSA-jjfr-hcj7-qf5w

ImageSharp 4 的直接依赖需要构建许可证：
https://docs.sixlabors.com/articles/imagesharp/index.html

替换组件的官方包与第三方声明：
https://www.nuget.org/packages/Magick.NET-Q8-AnyCPU/14.17.2

## 最终实现

- Core 固定 Magick.NET-Q8-AnyCPU 14.17.2，移除 ImageSharp 依赖和 CI 许可证配置。
- 增加 `RasterImage` 封装，迁移索引、元数据尺寸读取、缩略图、人脸裁剪、感知哈希、视频拼图、关键帧验证、云端图片清理和 CLIP/Florence 预处理。
- 原图使用只读文件流打开；裁剪与缩放在内存中完成，上传前移除元数据。解码限定常见栅格格式，禁用 delegate、filter、间接路径与其他解码器；`XC` 仅用于纯色画布创建。设置原生内存、磁盘、图像尺寸和线程上限。
- 确认 RGB 通道顺序、裁剪、黑边填充、拼图位置、取消操作和元数据清理；新增常见格式原图不变和微型 Group 4 TIFF 回归。
- CLIP 预处理版本增加 `/magick-catrom-v1`，检索按版本过滤，既有旧版本向量需通过“建立/继续语义索引”重新生成；不修改源图和人工分类。
- 修复索引差异哈希的 56 位缓冲区写入 64 位导致越界问题，并补充真实图片索引回归；释放独立感知哈希计算中的临时图像。
- 完整第三方声明通过 NuGet 包的 `Notice.txt` 随构建/发布输出至 `licenses/Magick.NET-Notice.txt`。

## 验证与边界

- `dotnet restore picer.sln` 成功。
- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误；日志 `artifacts/p0-magick-build.log`。
- 非 RuntimeComponent 单元全套：382/382 通过，报告 `artifacts/p0-magick-tests/magick-unit.trx`；随后新增的 64 位索引哈希专项单独验证。
- 图像策略、颜色、元数据、格式和模型版本专项：17/17 通过，报告 `artifacts/p0-magick-tests/imaging-policy.trx`。
- 非 RuntimeComponent 桌面全套：60/60 通过，报告 `artifacts/p0-magick-tests/magick-e2e.trx`。
- 离线 CLIP 两项及索引哈希专项：3/3 通过，报告 `artifacts/p0-magick-tests/magick-models-hash.trx`。
- Florence CPU 阶段验证、CPU 图像生成和 DirectML 兼容探测：3/3 通过，报告 `artifacts/p0-magick-tests/magick-florence.trx`。
- Linux IndexService Release 发布通过，目录 `artifacts/p0-magick-linux` 包含原生 `Magick.Native-Q8-x64.dll.so` 和完整第三方声明，无 ImageSharp 依赖。
- 全量含传递依赖审计：生产项目未发现已知易受攻击包；两个测试项目仍通过旧 xUnit 依赖链包含 `System.Net.Http 4.3.0`、`System.Text.RegularExpressions 4.3.0` 历史漏洞包。此项未在本轮升级测试框架。
- 本轮交付为本地修复与发布目录验证；完整安装包和线上服务升级属于后续交付事项。

## 使用影响

无需 Six Labors 许可证或 secret。新库包含随包提供的原生组件，发布时必须携带对应平台的 native 库与第三方声明。

CLIP 旧索引不会与新预处理向量混用，用户需要重新建立语义索引后检索这些图片。图像重采样实现更换，生成的缓存图和近似哈希不承诺与旧库逐字节一致；历史图片、人物决定和数据库 schema 不变。
