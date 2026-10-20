# 苏联卡包（语音待录制）

苏联卡牌目录来自工作区已解包的 `KardsVox.Analysis/kards/Content/Blueprints/Cards/Soviet`。按 `.uasset` 文件各计一次，共 **319 个卡牌资产**：177 个单位、119 个事件、23 个地点。资源目录包含 **13 个常规卡牌批次**，以及 `Locations`、`specialCards` 两个辅助目录。这里的“批次”是游戏资源文件夹，并非玩家预设卡组。

已使用 `repak` 解密读取本机当前安装的 `kards-Windows.pak` 索引，并与解包目录逐项比对：苏联 `.uasset` 的 319 个名称全部一致，新增和缺失均为 0。当前包中的路径清单保存在 `KardsVox.Analysis/soviet-current-pak-files.txt`。

| 批次 | 资产数 |
|---|---:|
| Base | 72 |
| Allegiance | 24 |
| OceaniaStorm | 22 |
| Breakthrough | 19 |
| Homefront | 19 |
| BrothersInArms | 16 |
| BloodIron | 15 |
| CovertOp | 15 |
| Legions | 15 |
| NavalWarfare | 15 |
| WinterWar | 13 |
| WorldAtWar | 11 |
| ToW | 8 |
| Locations（辅助） | 18 |
| specialCards（辅助） | 37 |

`card_catalog.tsv` 按日本包的 `internal_name/category` 格式建立，类别为 `INFANTRY`、`TANK`、`AIR`、`ORDER`、`LOCATION`；本次解包的苏联单位没有需要划入 `NAVY` 的舰船卡。`sets.tsv` 保留每个资源文件夹的单位、事件、地点数量。`generate_catalog.py` 可从解包目录重新生成这两个文件。

`voice_plan.tsv` 以日本语音台本的字段和事件槽位为模板，预留 134 个苏联语音 ID。由于没有苏联舰船卡，未复制日本包的 10 个 `NAV` 槽位。俄语和中文台词仍为空，避免把日语台词误作苏联对白。`voice_lines.tsv` 目前只有表头，`Audio/Soviet` 也没有 WAV。苏联包会在界面显示“待录音”；选择它时不会调用日本语音。完成苏联台本、模型和录音后，仍需接入对应播放器并将卡包设为可用。
