# 《缺氧》同图合作 Mod

每位玩家都运行自己的 Windows Steam 版《缺氧》。房主加载殖民地并推进物理、复制人 AI 和任务；好友收到房主当前的临时存档副本，在自己的原版殖民地场景中移动镜头、打开侧栏和下单。一个房间最多 4 人。本分支源码面向基础游戏（Vanilla）Klei 764025 / Steam build 26798728。

0.3.1-dev 已在同机两个独立 Steam 账号上实测好友邀请、双方下单及重连。两端画面和对照数据见[双账号验收记录](docs/evidence/steam-dual-764025.md)。0.3.0-dev 的一小时三端长稳和单份授权测试见[单份授权报告](docs/evidence/single-license-764025.md)。

## 构建和安装

双方想试联机建筑进度、供电提示、区域挖掘或黄警，可使用已通过同机双账号验收的 [0.3.2 建筑与工单预览包](dist/QueyangCoop-0.3.2-building-preview.zip)。测试档、取图和各项实际覆盖见[联机靶场](docs/08-test-range.md)。

研究台机身工作循环、电池充放电表计和倍速按钮的修复已通过同机双账号验收。双方用同一份预览包。[建筑动画预览包](dist/QueyangCoop-0.3.2-building-motion-preview.zip) · [联机靶场](docs/08-test-range.md#建筑工作动画)

需要验证受伤复制人的血条或人工光照时，双方可安装 [0.3.2 血条与照明预览包](dist/QueyangCoop-0.3.2-health-preview.zip)。同机双账号已实测新人的 15%、55% 和满血隐藏，以及开灯、关灯。该轮客机录像短于目标时长，后置独立读档未执行；详细结果见[双端记录](docs/evidence/steam-dual-764025.md)。

当前基础游戏版本可使用 [0.3.2-dev 双账号实测包](dist/QueyangCoop-0.3.2-dev.zip)，SHA-256 为 `BB0293C32F6B614EF924CBA8261B6F3257CA54FFFDD10FE2281887607651432F`。该包验证了挖掘工具、掩埋标记、掉落物和小动物运动。仍有局部照明色差，源码后续迭代的功能需查看各自验收记录。在仓库根目录先预览安装目标，第二行安装同一个 ZIP。安装器会为已有的不同版本备份，并打印回滚路径。

~~~powershell
.\tools\install-dev.ps1 -Package .\dist\QueyangCoop-0.3.2-dev.zip -WhatIf
.\tools\install-dev.ps1 -Package .\dist\QueyangCoop-0.3.2-dev.zip
~~~

仓库保留的 [0.2.0 历史包](dist/QueyangCoop-0.2.0-dev.zip) 适用于 Klei 744825。历史包哈希见[当时的验收记录](docs/evidence/native.md)。

好友只下载 ZIP 时，可退出游戏，将四个包文件解压到个人“文档”下的 `Klei\OxygenNotIncluded\mods\Local\QueyangCoop\`，然后在游戏 Mods 菜单启用。同一台机器选用一种安装方式即可，避免重复加载。

自行编译需要 Windows、Steam 版《缺氧》和 .NET SDK 9。项目按本机 ONI 构建号 764025 编译。若 Steam 不在默认库，先将环境变量 ONI_GAME_DIR 指向包含 OxygenNotIncluded.exe 的目录。两端使用同一个候选包并启用 Vanilla，关闭其他 Mod、DLC 后重启游戏。

1. 在仓库根目录运行环境检查和核心测试。预期 doctor 打印游戏库、程序集哈希和 SDK，测试全部通过。

~~~powershell
.\tools\doctor.ps1
dotnet test .\tests\Core.Tests\Core.Tests.csproj -c Release
~~~

2. 编译并打包。产物为 artifacts/QueyangCoop-dev.zip，两端解压后应得到两份 DLL 和两份 YAML。打包工具只收录本项目文件。

~~~powershell
dotnet build .\QueyangDuoren.sln -c Release
.\tools\package.ps1
~~~

3. 退出游戏后安装。先预览目标目录，确认后执行第二行。安装脚本会在替换旧开发包前保存备份并打印路径；可用 tools/restore-dev.ps1 -BackupPath 指定备份回滚。新装可在游戏 Mods 菜单禁用。

~~~powershell
.\tools\install-dev.ps1 -WhatIf
.\tools\install-dev.ps1
~~~

在 Mods 菜单启用 Queyang Co-op，按游戏提示重启。若安装后未出现入口，先检查重复 Mod、版本与 Player.log 中的 QueyangCoop 记录。首次试玩使用单人存档的副本。

## 两人进入同一局

1. **房主**载入殖民地副本。第一次用原单人档试玩时，游戏可能提示“Mod 差异”（MOD DIFFERENCES），核对新增项为 Queyang Co-op 后继续。打开暂停菜单的“多人合作”并创建 Steam 房间，点击“邀请 Steam 好友”或复制房间号。回到游戏并解除暂停后，复制人才会执行订单。
2. **客机**在游戏主菜单接受 Steam 邀请，或在“多人合作”中输入房间号。连接后等待临时存档传输、校验和原版加载画面完成，再用自己的镜头选区、挖掘和下蓝图。房主在不同区域移动镜头也能继续施工。
3. 使用游戏原有的研究、排班、个人优先级窗口和已接入的建筑侧栏。房主管理暂停、倍速、保存和关闭房间。
4. 客机离开后可从菜单重新加入，重新取得房主当时的殖民地副本与最新视图。房主重读存档时另开房间，旧命令随旧连接失效。

地图上的彩色编号指明谁创建了工单。房主是蓝色 P1，后续玩家依次获得橙色 P2、绿色 P3 和粉色 P4。把鼠标移到编号或目标格即可看到玩家昵称和命令类型。挖掘、拖地、建造（含管线）、拆除、清扫和自动收获支持归属显示。更改优先级会保留原创建者，旧存档里来源未知的工单没有徽标。研究和排班等即时操作没有地图工单。

### 使用局域网调试

本机调试可用默认 127.0.0.1:24714。两台电脑处于同一可信局域网时，在房主启动游戏之前把 ONI_COOP_BIND 设为房主的局域网 IPv4 地址及端口（例如 192.168.1.23:24714），确保启动游戏的进程继承该变量，然后用“创建开发局域网房间”。客机填写该地址并点击“局域网加入”。此路径用于受控测试，玩家正式远程连接使用 Steam 好友房。

### 当前显示范围

连接好友房会传送并由本机游戏读取完整殖民地存档。与自己信任的好友开房，在副本上试玩。资源、环境和建筑状态按视口分区更新。开发中的 0.3.2 已接入客机学习技能、职业帽、排班表编辑与个人工作优先级。打印舱选择、部分特殊建筑面板、对象重命名与游戏时钟仍是操作覆盖缺口。不同版本的实测结果见[双账号验收](docs/evidence/steam-dual-764025.md)。

## 开发入口

[设计总纲](docs/README.md) · [线协议实现](docs/evidence/protocol.md) · [开发验收矩阵](docs/05-verification.md) · [多 agent 委托书](docs/06-codex.md)
