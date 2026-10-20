# 苏联卡包（俄语语音与俄中字幕）

苏联卡牌目录来自工作区已解包的 `KardsVox.Analysis/kards/Content/Blueprints/Cards/Soviet`。按 `.uasset` 文件各计一次，共 **319 个卡牌资产**：177 个单位、119 个事件、23 个地点。资源目录包含 **13 个常规卡牌批次**，以及 `Locations`、`specialCards` 两个辅助目录。这里的“批次”是游戏资源文件夹，并非玩家预设卡组。

已使用 `repak` 解密读取本机当前安装的 `kards-Windows.pak` 索引，并与解包目录逐项比对：苏联 `.uasset` 的 319 个名称全部一致，新增和缺失均为 0。当前包中的路径清单保存在 `KardsVox.Analysis/soviet-current-pak-files.txt`。

2026-09-24 重新解包了当前安装包的全部卡牌资产及游戏本地化文本。`card_titles.tsv` 为 **319 个资产逐个**列出当前游戏简体中文名、英文名、俄文名和包内路径；119 个指令资产均找到对应显示名。9 个教程或空白变体复用基础卡名，7 个 `card_location_ai_soviet_unlock1..7` 是没有独立牌名的内部总部解锁资产；不存在未核对的指令牌。

主目录之外的战役 AI、特殊模式和促销／测试资产另见 `Data/Audit/special-nation-candidates.tsv`；这些不计入常规苏联卡包的 319 个资源。

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

`card_catalog.tsv` 按 `internal_name/category` 格式建立，单位细分为 `INFANTRY`（109）、`ARTILLERY`（12）、`CAVALRY`（3）、`TANK`（32）、`AIR`（21）；另有 `ORDER`（119）与 `LOCATION`（23）。榴弹炮、反坦克炮、高射炮和喀秋莎进入炮兵字幕池，不会再显示步兵台词。本次解包的苏联单位没有需要划入 `NAVY` 的舰船卡。`sets.tsv` 保留每个资源文件夹的单位、事件、地点数量。`generate_catalog.py` 可从解包目录重新生成这两个文件。

`subtitle_lines.tsv` 提供指挥官开场、单位出牌、受伤和 HQ 战况的原创俄中配对台词，字段为 `category / trigger / speaker / original / text`。步兵、炮兵、骑兵、坦克、航空各有独立的部署与受伤台词池；部署池分别有 5、6、3、5、5 条，轮完一轮前不会重复。`order_lines.tsv` 按 `resource / original / chinese` 为全部 **119 张苏联事件／指令牌**逐张提供不同的俄语台词及中文译文；启动时会核对卡牌清单并拒绝缺漏或重复台词。全部 174 条台词配有独立俄语 WAV，五段开场录音合计约 23 秒，字幕按实际段长切换。这些是为卡包新写的氛围台词，不是游戏卡牌效果文本，也不是已有俄语录音的转写。字幕沿用日本包的旧纸条覆盖层；程序根据我方总部、起手牌和单张卡资源自动识别国家，不必手动切换。

2026-09-24 根据玩家提供的 `order_lines_中文.xlsx` 复核了逐牌台词：保留红色标记的表达方向，重写黄色意见与蓝色机翻问题，并清除写在中文字幕栏中的修改备注。表内旧牌名按本机当前 `Game/zh-Hans` 本地化统一校正。`order_lines.tsv` 是软件实际读取的版本。

`voice_generation_manifest.tsv` 逐行列出 174 条录音 ID、俄中台词与对应 `Audio/Soviet` 文件名。`voice_plan.tsv` 是早期预留的 134 个事件槽位模板，`voice_lines.tsv` 是未使用的空表；运行时以 `subtitle_lines.tsv`、`order_lines.tsv` 和同名 WAV 为准。启动时会检查全部录音的文件名及 48 kHz、单声道、16 位 PCM 格式。音频开关可独立于字幕关闭；切换国家时不会调用另一国家的录音。
