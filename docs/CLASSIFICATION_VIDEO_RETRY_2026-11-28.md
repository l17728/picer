# 分类瞬时断连与视频关键帧恢复验证

生产分类任务曾将第一次遇到的 IOException 直接记作失败，服务日志同时出现 media_agent_offline。分类工作线程现在依照驻留程序返回的已知错误码处理暂时断连、通道忙和媒体读取并发限制，复用现有最多三次尝试。目录选择错误和预览超过上限继续报告失败。分类运行时回归 48 项通过。

桌面全量测试首轮 179 项中有 3 项失败。远程视频帧显示在缺少共享缓存服务时访问了空引用。另一个用例把远程视频目录请求也阻塞了，无法走到要验证的帧下载。修复后，视频详情先显示已有关键帧，并在下载部分失败时显示可用数量。测试通过正常的远程视频搜索和详情接口模拟延迟下载。5 个相关桌面测试全部通过。

Windows x64 Release 构建为零警告、零错误。D 盘离线期间的恢复构建位于 `C:\Users\a\AppData\Local\Picer\engineering-backups\relay-photo-20261128\verification\src\Picer.App\bin\Release\net8.0-windows\win-x64\Picer.App.exe`。同目录 Picer.App.dll SHA-256 为 `536085edae772a4db884a6558547d4528964150c9ee5326450c9e0412040df58`。
