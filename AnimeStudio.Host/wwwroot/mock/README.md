# mock 数据说明

这个目录里的 `data.json` 和 `images/` 是 Host UI 的演示数据，**不入库**。

- 它们来自本机游戏导出（真实 UI 精灵 + 真实 TextMap 条目），属于游戏本体资源
- 生成脚本：`tools/make-host-mock.ps1`
- 换机器 / 重新 clone 后，先导出一次资源，再跑脚本即可恢复

没有 mock 数据时，页面会提示「演示数据未生成」，其余 UI 结构仍然可见。
