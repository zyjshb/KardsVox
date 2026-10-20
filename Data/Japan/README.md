# 日本卡牌目录

`card_catalog.tsv` 收录当前本机 KARDS 安装包里日本目录的全部 **312 个卡牌资产**，其中 **124 个指令资产**。`card_titles.tsv` 为每个资产列出资源名、类别、当前游戏简体中文名、英文名、日文名和包内路径；`title_status` 区分正常牌、复用牌名的变体和没有独立牌名的内部总部解锁资产。14 个 `card_location_*_unlock1..7` 是内部解锁资产，游戏本地化没有为它们提供独立标题，并非遗漏的指令牌。

清单来自 2026-09-24 对本机 `kards-Windows.pak` 的重新解包，使用卡牌 `.uasset` 与 `Game/zh-Hans`、`Game/en`、`Game/ja` 的本地化文本逐一配对。原先按英文资源名猜测的 98 个指令中文标题已全部改为当前游戏显示名。特别是 `card_event_to_the_last_man` 为“至死方休”（日文“一億総玉砕”）；`card_event_one_roof` 当前简体中文名为“虚妄之梦”，日文名“八紘一宇”，玩家常说的“八纮一宇”指的就是这张牌。

`order_lines.tsv` 和 `order_originals.tsv` 是模组创作的逐牌字幕，不是游戏效果文本或历史录音转写。全部 124 个指令资源均有独立台词，并可通过 `card_titles.tsv` 核对实际牌名。单位卡使用类别语音池，不逐牌配音。

主目录之外的战役 AI、特殊模式和促销／测试资产另见 `Data/Audit/special-nation-candidates.tsv`；这些不计入常规日本卡包的 312 个资源。
