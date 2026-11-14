---
title: 机器人与压测
---


# 机器人与压测（控制台舰队页）

控制台的**机器人**页从面板驱动模拟器舰队与压力驱动器。它有三组面：

- **场景** —— 以 TOML 文档保存的机器人剧本，作为普通记录存进控制面数据库（控制宿主重启后仍在）。启动运行前先解析剧本，坏剧本在请求处回答，不会中途失败。
- **运行** —— 一个场景对着真实链路演完：经主播网关的 gRPC 面挂载，经入口面播完日程，再与数据面对账投递与比分。有偏差的运行自报 `Deviated` 并逐条列出；空列表即为通过。
- **压力运行** —— 压测工具自己的 `IngressDriver`，在控制宿主进程内运行，从面板而非命令行驱动。没有第二套驱动语义：同样的 `RatePacer` 绝对排程、同样的按结果计数（`accepted` / `duplicated` / `unauthorized` / `rateLimited` / `other`）、与[压测](../guide/load-testing.md)相同的签名请求形态。

相关文档：

- [压测](../guide/load-testing.md)
- [可观测性](../reference/observability.md)

## 压力运行

同一时刻只允许一个在跑——已有一个在跑时再启动返回 `409`，因为两个源互相写进去的读数永远不干净。事件 id 从每次运行自己的前缀取号，这次运行的幂等键不会与下次碰撞。

```bash
# 对入口面启动一次运行（操作员/管理员 "operate" 策略）。
curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"baseUrl":"https://platform-gateway:443","platform":"douyin","eventsPerSecond":120,"sessions":8,"producerConnections":4,"durationSeconds":30}' \
  https://control-host:443/api/pressure/runs

# 盯着它；每条记录都带驱动的计数与「实际速率 / 目标速率」。
curl -H "Authorization: Bearer $TOKEN" https://control-host:443/api/pressure/runs/pressure-run-1

# 在窗口走完前停掉它；运行自报 Stopped，而不是 Completed。
curl -X POST -H "Authorization: Bearer $TOKEN" https://control-host:443/api/pressure/runs/pressure-run-1/stop
```

坏请求在请求处回答：相对地址或非 HTTP 的基址、零速率、缺失的根证书文件，都是 `400`，且什么都还没启动。未知的运行号回答 `404`。

要驱动证书链到自有根的 HTTPS 入口面，传 `rootCaPemPath` —— 该运行只信任这一个根（与内部客户端拨号用的同一套自定义根规则），别的一概不信。

## 一键火焰图

给压力运行加上 `profileSeconds`，采样随运行一起走：

```bash
curl -X POST ... -d '{"...":"...","durationSeconds":30,"profileSeconds":30}' \
  https://control-host:443/api/pressure/runs
```

- 采样窗口以运行时长封顶——火焰图不能比它采的那次运行活得更久。
- 默认采样控制宿主自己的进程（驱动器就跑在里面）。用 `profileProcessId` 指定网关的 pid，改采网关。
- 需要控制宿主所在机器装了 `dotnet-trace`：`dotnet tool install -g dotnet-trace`。
- 窗口关闭后 nettrace 转 speedscope；运行记录带两个路径，面板按**自身时间占比**排出前十热点帧——每个采样点最底下的帧拥有该采样的权重，最热的叶子就是墙钟时间花掉的地方。

把 speedscope 路径在 [speedscope.app](https://speedscope.app) 打开即得完整火焰图；面板的排序列表是「哪个函数吃掉了这次运行」的快答。

采样从骨子里是尽力而为：工具缺失、采集失败、转换失败、speedscope 读不出来——都只是控制宿主 stderr 里的一行提示和记录上空的热点栈，绝不会把压力运行本身拖成 Failed。

## 诚实边界

入口与投递两个统计面**只有计数族**——接受、重复、限流等等。面板不带耗时分位数，没有可以指着慢窗口的 P99 直方图；那些活在引擎自己的指标面里，本面板刻意不重复造。BOTS-3 给操作员的是「多少量、什么速率、对着什么目标」（压力计数）与「墙钟时间花在哪些函数上」（火焰图）这一对——把慢窗口与对应采样关联起来，仍是操作员自己的活。
