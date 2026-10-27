# 视频归属查询的事务继承修复

状态：开发源码修复，尚未发布部署；数据库 schema77、同步协议 9 不变。

## 根因与修复

`GetVideoOwnerKey` 和 `RequireOwnedVideoFace` 的可选事务参数默认是 null。数据库命令原本会继承连接上已有的事务，但这两个查询无条件把命令事务设为该参数，导致继承的事务被清除。在调用方已经开启事务、又没有显式传参时，SQLite 抛出“连接存在待处理事务，但命令未设置事务”的异常。

改为仅在调用方提供非空事务时覆盖命令事务；默认调用保留继承的事务。授权归属判断和来源隔离规则不变，也不会自行提交或回滚调用方事务。

## 验证

新增 `OwnershipReadsParticipateInTheExistingTransaction`：开启事务后使用默认参数读取视频归属并检查人脸归属，回滚后再读取。修复前 `video-owner-ambient-transaction-repro-schema77.trx` 0/1，准确复现 `GetVideoOwnerKey` 的事务异常。

修复后严格整仓构建零警告、零错误。`video-owner-transaction-and-catalog-schema77.trx` 61/61，覆盖视频人脸归属、因果字段、目录删除队列及服务端接收；`catalog-video-sync-ui-schema77.trx` 26/26，覆盖真实 WPF 同步冲突窗口、视频详情和本地账号同步。这是专项回归，不代表最新源码整套非 RuntimeComponent 测试已完成。

这是代码路径的可复现缺陷；没有据此认定用户先前的所有“服务不可用”均由它引起。生产程序尚未替换。
