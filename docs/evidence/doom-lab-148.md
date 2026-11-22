# The Doomed Laboratory 常规联机验收

测试使用 Windows Steam 基础版《缺氧》764025。148 周期固定输入为 `artifacts/qa/mature-range/The-Doomed-Laboratory-148.sav`，SHA-256 为 `0BD29D75AEE2F58CA107DC4B64401C2955C1513E4EE20A9FE531665CBB6A6312`。两个 Steam 账号都拥有基础游戏，房主窗口 1280×800，客机小窗 1040×650。[候选档来源与原版建筑覆盖](../08-test-range.md#成熟殖民地候选)

## SYS-FARM 动物生命条

2026-11-23，现场观察到房主显示动物红条，客机同一场景缺条。此前动物视区数据已经带生命百分比，客机用它填实体详情，原版世界生命条仅从复制人视区数据接线。修复在动物实体的可靠视区数据加显示标记，读取房主原版红条的显示状态，并在客机实体上复用原版 `HealthBar`。满血、隐藏标签及对象消失时收起红条，动画仍走原有动物姿态同步。

本轮 `steam-dual-2a4c88a77a4d4977b35f907d53fc0afa` 在首次暂停入房后，由测试夹具在 (127, 196) 创建橙色浮游生物 `LightBugOrange`，使用同一存档 ID 470022 逐步受伤、恢复。两端原版红条颜色均为 `CC3333FF`。截图每行左房主右客机，从上到下为 15%、55%、满血隐藏。[三阶段双端截图](qa/doom-lab-critter-health.jpg)

| 工况 | 房主原版条 | 客机原版条 | 结果 |
|---|---|---|---|
| 受伤至 15% | 可见，填充 15% | 可见，填充 15% | 通过 |
| 恢复至 55% | 可见，填充 55% | 可见，填充 55% | 通过 |
| 恢复满血 | 隐藏 | 隐藏 | 通过 |

首次加入与重连均对齐 `4580/147D63A560DD98D3C684AE86`，受保护输入哈希一致。生命条的三个状态在首次会话中验证，后续补动物移动中跟随、负伤时重连、离屏返回、死亡清理及其他种类。固定包 SHA-256 为 `8572E5559DFC4BC3B481F2227A098FCB37646445A641F26EA6A738972F802D3F`。[动物生命条实测包](../../dist/QueyangCoop-0.3.2-critter-health-preview.zip)

复测时填入两个独立 Steam 账号，运行下面的命令。成功会为每端写入三张原始截图和三条 `DUAL_HEALTH_BAR` 记录，并校验两次入房的建筑摘要。

~~~powershell
$test = @{
    HostSteamId = $hostSteamId
    GuestSteamId = $guestSteamId
    SavePath = '.\artifacts\qa\mature-range\The-Doomed-Laboratory-148.sav'
    PackagePath = '.\dist\QueyangCoop-0.3.2-critter-health-preview.zip'
}
.\tests\Integration\Game\steam-dual.ps1 @test -CheckColonyJoin -CheckCritterHealth
~~~

## MAP-01 短时运行对照

两种工况都使用 1 倍速，依次观察冷藏箱 (127, 196)、燃煤发电机 (95, 180) 与养殖站 (81, 180)，倍率 12，每段约 12 秒。原版单人仅加载观测夹具，未加载联机程序集。双端使用上述红条修复包，首次入房后再解除暂停，两端对齐 4,580 个成品实例。单人帧间隔来自 Unity，双端用独立单调时钟取样。[单人三工位](qa/doom-lab-stock-runtime.jpg) · [双端入房和三个工位](qa/doom-lab-dual-runtime.jpg)

| 工况 | 三段累计模拟秒 / 采样墙钟秒 | 各段帧间隔 p95（ms） |
|---|---:|---|
| 原版单人 `map01-9ea4363fac8043a485a29e3fe3e5a3b1` | 35.664 / 36.010 | 8.68 / 11.68 / 6.84 |
| 双 Steam 房主 `steam-dual-a4fc695732524fb98dd4822de990e60c` | 35.501 / 36.265 | 182.95 / 252.02 / 152.14 |
| 同轮客机，基于同步的时钟 | 35.672 / 36.094 | 18.03 / 18.05 / 11.99 |

主机模拟推进达到约 98% 的墙钟速度，具备继续做原版交互验收的条件。房主画面仍明显慢于单人，客机 p99 也为 110–124 ms。第二和第三工位房主处于后台。采样时系统可用内存约 6 GiB，多数采样点换页读取为零。单机双进程工况保留画面性能待办，正式跨设备的渲染、延迟与长稳分别验收。两轮日志均无此次关注的动物任务异常。双端运行未采录像，1160 周期对照已有主客机录像。[大图诊断](map01-spacerock.md#同一镜头暂停联机更新)

双端复测沿用上方的 `$test` 参数。原版单人需要使用双 Steam 夹具生成的隔离游戏副本，并用同一档跑路线。

~~~powershell
.\tests\Integration\Game\steam-dual.ps1 @test -CheckColonyJoin -CheckColonyRun -Map01DoomedLab
.\tests\Integration\Performance\map-baseline.ps1 -SavePath $test.SavePath -GameCopy $isolatedHostGame -DoomedLab
~~~

## MAP-02 与 MAP-03 联合回归

运行 `steam-dual-647c03323811426c98ddaedc2bc12a9d`，沿用相同存档和动物生命条预览包。一次开房，客机先通过房间号加入暂停中的殖民地。复测动物生命条和独立镜头后退出，等待回到主菜单且临时存档目录清理，再加入正在 1 倍速运行的同一房间。两次入房各自生成并校验新的原版存档，均对齐 `4580/147D63A560DD98D3C684AE86`。客机在首个实际应用的中心视区之后激活原版挖掘工具，确认激活对象，再恢复选择工具。[两次入房截图，左主右客](qa/doom-lab-map02-join.jpg)

| 计时（毫秒） | 首次暂停入房 | 1 倍速重连 |
|---|---:|---:|
| 客机提交房间号至收到 Welcome | 1,363 | 1,316 |
| 房主创建原版快照 | 1,534 | 1,020 |
| 客机接收 NativeBegin 至 NativeEnd | 9,482 | 9,589 |
| 验证存档并进入原版加载入口 | 4 | 3 |
| 原版加载入口至 Native Ready | 9,233 | 4,787 |
| Native Ready 至首个可靠中心视区应用 | 870 | 2,643 |
| 首个视区应用至挖掘工具激活 | 109 | 101 |
| 客机提交房间号至工具激活 | **22,602** | **19,464** |

快照分别为 2,396,098 和 2,401,456 字节。客机读到的 SHA-256 分别为 `0D278E1A74942993C892CF181BC8FD899560F34026CCF0F8D5A9B4E545187729` 与 `14806A092C439C11B036FFF895750F40348385B9112CB763B450D1D87BA6127C`。房主 Mod 的可靠重试队列峰值均为 0 字节。Steam 内部队列、文件头单独签名、磁盘写入与引擎内拷贝未单独测量。时长按各自进程单调时钟计算，总时长直接用客机两次标记相减；房主创建快照与客机握手可重叠。首次测试夹具叠加了两次原版暂停，留下两次均暂停的记录 `steam-dual-dac618a999544f2d870ce1055c89f351`。修正后重新实测，第二次生成快照时明确记录 `paused=False speed=1`。

首次会话里，房主镜头固定在研究区 (115.5, 203.5)，客机走下表路线。核对实际接收并应用的中心视区原点，宽镜头阶段再等一个本次匹配后到达的外围视区。主客镜头都实测保持在各自坐标附近。房主的红条也在同一轮按 15%、55%、满血隐藏复核。[独立镜头三工位，左主右客](qa/doom-lab-map03-camera.jpg) · [同轮红条复测](qa/doom-lab-map03-health.jpg)

| 客机镜头 | 倍率参数 | 实际中心页原点 | 跳转至中心页 | 进入截图条件 |
|---|---:|---|---:|---:|
| 养殖站 (81, 180) | 9 | (69, 172) | 442 ms | 442 ms |
| 天然气发电区 (146, 230) | 9 | (134, 222) | 305 ms | 305 ms |
| 冷藏区 (127, 196) | 20 | (115, 188) | 413 ms | 748 ms，含外围页 |

此次 MAP-03 覆盖暂停时跳转和缩放。运行中的连续拖动、边缘所有画面元素、双方连续换位和跨设备仍待测。

~~~powershell
.\tests\Integration\Game\steam-dual.ps1 @test -CheckColonyJoin -CheckColonyBatch -CheckCritterHealth -InviteWaitSeconds 0
~~~
