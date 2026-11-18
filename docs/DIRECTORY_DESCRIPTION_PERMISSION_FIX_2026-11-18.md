# 目录描述 AI 权限修复（2026-11-18）

目录描述失败根因是 GUI 设备凭据缺少 `analysis.run`，预取分析输入接口返回 HTTP 403。自动 bootstrap 注册流程曾将 GUI 的 `allowAnalysis` 固定为 false。

修复内容：bootstrap enroll 对 GUI 授予分析权限；当前计算机最近两个活动 GUI grant 已补写 `analysis.run answer.run`，修改前数据库已备份到服务器 accounts-v2 控制目录。IndexService 已重新部署并保持 active。
