# 《缺氧》同图合作 Mod

房主在原版殖民地中运行完整模拟。客机留在游戏主菜单，通过独立的 24×16 格地图发出命令、查看施工和环境变化。支持房主加最多三名客机。此分支交付开发候选包，实测范围见 [验收记录](docs/evidence/status.md)。

**当前版本是联机技术原型。** 真人通过 Steam 加入后同样看到简化格子地图。玩家版要求所有人看到本机原版殖民地场景，这项功能仍待开发。

## 构建和安装

已有游戏的试玩者可直接使用仓库中的 [0.1.0 开发包](dist/QueyangCoop-0.1.0-dev.zip)。在仓库根目录运行以下命令，可先预览安装目录，再安装同一份已测包。SHA-256 为 `C1F5A7AEB6C039362C371D70C9E7FB9CD29DCFEB583DC578B2598B6CCAD1F92C`。

~~~powershell
.\tools\install-dev.ps1 -Package .\dist\QueyangCoop-0.1.0-dev.zip -WhatIf
.\tools\install-dev.ps1 -Package .\dist\QueyangCoop-0.1.0-dev.zip
~~~

好友只下载 ZIP 时，可退出游戏，将四个包文件解压到个人“文档”下的 `Klei\OxygenNotIncluded\mods\Local\QueyangCoop\`，然后在游戏 Mods 菜单启用。同一台机器选用一种安装方式即可，避免重复加载。

自行编译需要 Windows、Steam 版《缺氧》和 .NET SDK 9。项目按本机 ONI 构建号 744825 编译。若 Steam 不在默认库，先将环境变量 ONI_GAME_DIR 指向包含 OxygenNotIncluded.exe 的目录。两端使用同一个候选包并启用 Vanilla，关闭其他 Mod、DLC 后重启游戏。

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

1. **房主**载入殖民地副本，打开暂停菜单的“多人合作”，创建 Steam 房间。房间建立后点击“邀请 Steam 好友”或复制房间号。回到游戏并解除暂停后，复制人才会执行订单。
2. **客机**在游戏主菜单接受 Steam 邀请，或在“多人合作”中输入房间号。连上后从打印舱附近开始，在自己的视图内平移、选区、挖掘和下蓝图。房主在不同区域移动镜头也能继续施工。
3. 点击“殖民地管理”可查看研究队列、变更排班和工作优先级；用格子检查和建筑设置查看实际目标及允许的操作。工具仅显示当前已接入的动作。房主管理暂停、倍速、保存和关闭房间。
4. 客机离开后可从菜单重新加入，重新取得房主当前视图。房主重读存档时另开房间，旧命令随旧连接失效。

### 使用局域网调试

本机调试可用默认 127.0.0.1:24714。两台电脑处于同一可信局域网时，在房主启动游戏之前把 ONI_COOP_BIND 设为房主的局域网 IPv4 地址及端口（例如 192.168.1.23:24714），确保启动游戏的进程继承该变量，然后用“创建开发局域网房间”。客机填写该地址并点击“局域网加入”。此路径用于受控测试，玩家正式远程连接使用 Steam 好友房。

### 当前显示范围

客机采用显示地图、复制人标记、七种地图叠层和管理面板；低频的打印舱、技能分配及未登记的建筑交互由房主在原生界面完成。地图保留已揭示区域遮罩。显示模型按可见建筑和管线提供施工、拓扑、读数及动作；完整原生角色动画和全部原版 UI 仍有差异。单人、双机与四人测试的不同证据见 [实测记录](docs/evidence/status.md) 和 [双机操作清单](docs/evidence/client.md)。

## 开发入口

[设计总纲](docs/README.md) · [线协议实现](docs/evidence/protocol.md) · [开发验收矩阵](docs/05-verification.md) · [多 agent 委托书](docs/06-codex.md)
