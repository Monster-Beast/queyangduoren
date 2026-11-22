# 建筑状态批量回归

使用 Windows Steam 基础版《缺氧》764025、同机独立 Steam 双账号。需要明确控制供电、研究点和设备库存，因此用第 3 周期隔离副本一次开房执行整组。输入 `artifacts/qa/range-starter/Fixture.sav` 的 SHA-256 为 `F115FE51875668F0AA027A576E3C468291BF5D6B25267227ED9F5FB08A594993`。动物生命条预览包 SHA-256 为 `8572E5559DFC4BC3B481F2227A098FCB37646445A641F26EA6A738972F802D3F`。[隔离工位准备](../08-test-range.md#研究与余量工位)

## 第一次合跑

运行 `steam-dual-6d84c22848f440c893f04ecb67bfa126`。仅限清扫、研究桌供电、研究进度和电池表计均完成双端三个阶段。储液库的准备格 49262 被原版判定为自动化端口重叠，夹具在建造前中止。建筑预览的端口校验以预览物体的位置生成视觉端口，旧夹具选了放置格却没有把预览移过去。复测先过滤有端口冲突的候选，再临时把原版预览移到实际候选格执行原版校验，最后还原预览。改动位于测试夹具。

## 五项联合复测

运行 `steam-dual-d3d4926353bf4ac996ca521dd2f76acb`。一次开房内依次完成下列工位。每行均在两端读取原版实际显示并保存三个阶段的画面，对照图每行左房主右客机。测试使用原版组件的状态输入控制各工位。

| 用例 | 房主与客机的三个阶段 | 证据 |
|---|---|---|
| SYS-BASE 储物箱仅限清扫 | 符号隐藏 → 显示 → 隐藏 | [截图](qa/device-batch-sweep.jpg) |
| SYS-POWER 研究桌供电 | 未接线红图标 → 已接线但缺电红电池 → 通电后两者消失 | [截图](qa/device-batch-power.jpg) |
| SYS-EQP 研究桌 | 3/15（20%）→ 9/15（60%）→ 进度条隐藏、点数仍为 9/15；两端条位置一致 | [截图](qa/device-batch-research.jpg) |
| SYS-POWER 电池 | 2,000 → 7,200 → 0 J，原版表计帧 6 → 21 → 0 | [截图](qa/device-batch-battery.jpg) |
| SYS-LIQ 储液库 | 1,000 → 3,600 → 500 kg，原版表计帧 3 → 10 → 2 | [截图](qa/device-batch-reservoir.jpg) |

工位的管网自然供料、复制人研究差事、操作权限和面板写入仍各自保留测试项。

同轮完成了原版工具下单、复制人实际挖掘、完工后挖掘标记移除及客机重连核对。房主手动保存的副本随后两次进入原版载入，分别在启用与卸载 Mod 的隔离环境核对原有工单、完成工单移除及已保存状态。带 Mod 的回读另外核对主客下单归属。受保护存档和安装输入哈希一致。[挖掘 24 秒双端录像，左主右客](qa/device-batch-dig-motion.mp4)

## 复测命令

~~~powershell
$test = @{
    HostSteamId = $hostSteamId
    GuestSteamId = $guestSteamId
    SavePath = '.\artifacts\qa\range-starter\Fixture.sav'
    PackagePath = '.\dist\QueyangCoop-0.3.2-critter-health-preview.zip'
}
.\tests\Integration\Game\steam-dual.ps1 @test -InviteWaitSeconds 0 -SkipManagement `
    -CheckSweepSymbol -CheckPower -CheckResearch -CheckBattery -CheckReservoir
~~~
