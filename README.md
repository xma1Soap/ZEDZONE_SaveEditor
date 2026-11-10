# ZEDZONE_SaveEditor · 隔离区人物存档修改器

《隔离区 ZED ZONE》的人物存档修改器（Windows 桌面程序）。可以分别修改**单机存档**（自己开的世界）和**联机角色**（去别人房间玩用的角色）里的人物。

按游戏 1.0.12 的存档格式制作。非官方工具，和游戏开发者无关。

## 能改什么

- **人物**：名字；属性点、技能点、天赋点；力量、敏捷、体质；血量、体力、精力、饱食、水分、血液、体温、辐射；身体六个部位的血量；清除状态效果；一键治疗。
- **背包物品**：
  - 改数量、耐久；修好（武器、弹匣、枪械配件、食物）、补满（堆叠、弹匣、枪里的子弹、液体容器）。
  - 改状态：枪况、枪里和弹匣里的子弹、弹匣耐久、近战武器和配件状态、食物还能放几天、液体容器装的是什么液体和装了多少。
  - 几乎不坏（测试中）：耐久和枪况等设成上限的 1000 倍，相当于用不坏（游戏没有"永不损坏"标记，只是不会被压回上限；算效果时最多按满值算，不会更强）。
  - 添加物品（按名字或编号搜索，带物品图标）、复制、删除。
  - 格子图里用鼠标拖动：换位置、拖着时右键或按 R 转向、拖进别的背包。
- **装备栏**：装上、换、卸下、删除（穿衣服、换武器、换背包）。每个槽能放什么、能放几件和游戏一样：
  - 武器 4 格；
  - 上衣最多 3 件，裤子最多 4 件；
  - 背包等槽位各 1 件。

详细用法见 [说明.txt](说明.txt)。

## 安全

- 每次保存前把原文件备份到 `%USERPROFILE%\AppData\LocalLow\LevenLiu\ZEDZONE\SaveEditorBackups\`。
- 只改人物这一块，存档里其他数据原样写回，一个字节都不动。
- 写入前会自检，自检不过就不写。
- 检测到游戏在运行会拒绝保存（游戏会定时存档，把改动覆盖掉）。

## 编译

需要 Windows 和 [.NET SDK](https://dotnet.microsoft.com/download)（6 或更新的版本都行）：

```
dotnet build -c Release
```

生成 `bin\Release\net48\ZedZoneSaveEditor.exe`。程序的目标是 .NET Framework 4.8（Win10/Win11 自带），所以是单个 exe，双击就能用。物品表和图标已经打进 exe 里。

## 自检

```
ZedZoneSaveEditor.exe --selftest 报告.txt
```

把存档复制到临时目录做"读 → 改 → 写 → 再读"的检查（包括背包和装备栏），不碰原存档。

```
ZedZoneSaveEditor.exe --uitest 报告.txt --root 临时目录
```

在存档副本上把界面按钮走一遍再保存核对。指向真实存档目录时会拒绝运行。

## 游戏更新后

游戏加了新物品时，用 `tools/` 里的脚本从游戏资源重新导出物品表和图标，再重新编译：

```
pip install UnityPy TypeTreeGeneratorAPI Pillow
python -I tools/export_items.py "游戏目录" "Il2CppDumper 生成的 DummyDll 目录"
python -I tools/export_icons.py "游戏目录"
```

存档格式要是变了，修改器读不出来或者自检不过时会拒绝保存，不会写坏存档。

## 存档位置

- 单机存档：`%USERPROFILE%\AppData\LocalLow\LevenLiu\ZEDZONE\GameData\GameData\<世界id>_SlotIndex<N>.game`
- 联机角色：`%USERPROFILE%\AppData\LocalLow\LevenLiu\ZEDZONE\GameData\MpCharacters\<角色id>.mpchar`

存档是没有加密的 protobuf（protobuf-net）。

## 协议

源代码使用 [MIT 协议](LICENSE)。

`src/items.tsv`（物品名称和数据）、`src/icons.png`、`src/icons.tsv`（物品图标）是从游戏资源导出的，版权归《隔离区 ZED ZONE》的开发者所有，**不在 MIT 协议范围内**。
