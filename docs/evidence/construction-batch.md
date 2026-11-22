# 施工、地板、区域挖掘和紧急优先级

测试使用 Windows Steam 基础版《缺氧》764025。两端使用独立 Steam 账号，房主打开第 3 周期隔离副本 `artifacts/qa/range-starter/Fixture.sav`。存档 SHA-256 为 `F115FE51875668F0AA027A576E3C468291BF5D6B25267227ED9F5FB08A594993`。四个工位合在同一次开房内依次执行，原版复制人完成实际施工、拆除和挖掘。

## 首轮与紧急标志复现

运行 `steam-dual-d178b729b2994f3c8eafe19fb0de23d0`，使用包 `8572E5559DFC4BC3B481F2227A098FCB37646445A641F26EA6A738972F802D3F`。储物箱施工、地板和全屏黄警的旧断言通过。区域挖掘的旧夹具确认六张工单同时出现，同一格的进度条增长并在完工后消失；验收结束时只有 2/6 格挖完。新断言等待六格全部挖完、工单和进度条全部清除，并补拍完工画面。

格 49016 的紧急挖掘令两端都进入黄警，首轮截图房主在该格下方有红色双感叹号，客机停留于优先级叠层。检查源码确认优先级 10 已经同步，客机的挖掘显示层此前只画锹形工单，建筑状态图标只遍历建筑锚点。修复在挖掘格单独读取对应工单优先级，以原版 `EmergencyPriority` 图标在该工单渲染位置显示。与建筑在同一格的挖掘单使用独立的工单优先级。施工蓝图继续使用现有建筑状态渲染路径并单独验收。[首轮截图，左主右客](qa/construction-batch-dig-icon-before.jpg)

中间回归在客机记录到黄警为真、挖掘格图标仍为假。原版 `StatusItemRenderer` 会剔除不可选择的对象，并按当前叠层筛选图标。挖掘显示对象调整为紧急时支持原版状态渲染。验收夹具现在在原版优先级工具下单后返回普通视图，确认双方均使用普通视图再读取渲染网格。

## 修复包的施工、地板和紧急优先级

运行 `steam-dual-84826c1098e44d518cda2e256d2e5b3b`，两端固定包 SHA-256 为 `78A97A364231BF87EB5B7B24171CE9C8D23851DC945DBB9799D133657215B298`。断言读取实际原版渲染器中对应对象的 `EmergencyPriority` 网格，以及各工位原有的进度、工单和贴图检查。截图每行左房主右客机。[实测预览包](../../dist/QueyangCoop-0.3.2-emergency-icon-preview.zip)

| 用例 | 两端实测 | 证据 |
|---|---|---|
| CMD-03、MGR-07 挖掘紧急优先级 | 客机用原版工具把格 49016 从普通 5 改为紧急，再恢复普通 5。该格的红色双感叹号依次隐藏、显示、隐藏，黄警画面同步进入和退出。 | [同一格放大](qa/construction-batch-dig-icon-after.jpg) · [三阶段](qa/construction-batch-alert.jpg) |
| BUILD-02 紧急施工 | 储物箱蓝图 2376 由 0% 到 28.2%，实际已送达 400 kg，原版条填充一致。蓝图有双感叹号，成品 2394 出现后原蓝图、进度条和红标都消失。 | [施工红标放大](qa/construction-batch-building-icon.jpg) · [三阶段](qa/construction-batch-range.jpg) |
| BUILD-02、BUILD-03 普通地板 | 蓝图 2360 有白框和红色紧急标志。完工为 2372，白框与红标移除，成品贴图、接缝和饰边一致；拆除后成品、接缝和饰边移除。 | [三阶段](qa/construction-batch-floor.jpg) |

普通地板处于蓝图阶段时，邻砖的底层连接标志为主机 `True`、客机 `False`。两端已建阶段都为 `True`，拆除后都为 `False`。蓝图阶段的连接显示保留专项核对。紧急声音和优先级叠层内的数字显示另测。

这轮的区域挖掘在两端都确认了六份归属一致的工单，并观测两次原版进度值及最终 5/6 完成。最后一格在房主仍为实心，夹具在六格完工前超时。[房主现场](qa/construction-batch-area-5-of-6.jpg)

独立诊断 `steam-dual-3f0543c1da354d13aa15ced30f59cef1` 使用同一固定包与存档，优先挑原版导航可达格数最多的天然矿区。该区域起点仍为 48758。主机原版 `Navigator.CanReach` 在挖除其余五格后多次确认 49014 不可达。同高度延伸两格梯子仍未打通。另一次试验 `steam-dual-63f3f6da11b14654b15b8a5b8b47216b` 也未能在原矿区建起有效平台。最终夹具优先选择下方有连续实心支撑的天然矿区，并保留导航诊断和六格完工断言。

## 区域挖掘全完工

独立复测 `steam-dual-f9eb192ba2a1413a861d2f6124cf4961` 改选基格 47476 的天然 3×2 矿区。保留矿区原有地形与支撑。客机只拖框一次，房主实际收到两行各三张工单。两端六格均识别为同一位客机下单。读取格 47733 的原版进度条，采到 2.3% 与 13.1%，两端一致。六格最终都由原版复制人挖完，主客机分别确认 6/6 格已无实心、工单或进度条，并保存完工截图。[下单、工作、完工](qa/construction-batch-area.jpg)

同轮后续的普通挖掘、退出重连、带 Mod 与无 Mod 的原版存档独立读回也通过。受保护的原始存档和安装输入哈希一致。[首次入房与重连](qa/construction-batch-entry.jpg)

## 复测命令

~~~powershell
$test = @{
    HostSteamId = $hostSteamId
    GuestSteamId = $guestSteamId
    PackagePath = '.\dist\QueyangCoop-0.3.2-emergency-icon-preview.zip'
    SavePath = '.\artifacts\qa\range-starter\Fixture.sav'
}
.\tests\Integration\Game\steam-dual.ps1 @test -InviteWaitSeconds 0 -SkipManagement `
    -CheckConstruction -CheckFloor -CheckAreaDig -CheckAlert
~~~
