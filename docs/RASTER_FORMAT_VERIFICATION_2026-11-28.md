# 栅格解码格式验收

全量回归在已有原生图像组件初始化的进程中发现，单独依赖 ImageMagick 全局策略时，载入 SVG 的测试未抛出预期异常。读图和读取尺寸现在都在调用解码器前检查文件头，明确指定已有栅格解码器。HTTP 和管道流仅暂存 12 字节文件头，可继续从当前位置读取。

受影响路径包括原图显示、缩略图和分析输入共用的 Picer.Core 图像入口。回归用例覆盖 JPEG、PNG、GIF、BMP、TIFF、WebP、矢量拒绝、流式输入和原图内容保持。本机正式 GUI 构建位于恢复工作区 src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe。