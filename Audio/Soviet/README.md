# 苏联语音素材

按 `Data/Soviet/voice_generation_manifest.tsv` 的 174 条台词生成俄语 WAV，文件名与清单的 `wav` 列一致，格式为 48 kHz、单声道、16 位 PCM。55 个用于字幕台词，119 个用于逐卡指令台词。当前使用 Chatterbox 多语言 V3 和原配音人的声音参考；生成脚本和模型在 `E:\kazi\RussianTTS\chatterbox_v3\`，上一版 XTTS 音频备份在 `E:\kazi\RussianTTS\xtts_backup\`。

播放器已接入这 174 个 WAV。游戏中自动识别苏联后，开场、单位部署与战斗、总部战况和 119 张苏联指令牌会播放对应录音；字幕开关与语音开关独立。F6 可预览五段开场录音，字幕按每段实际时长切换。`Data/Soviet/voice_lines.tsv` 是早期空白模板，实际台词由 `subtitle_lines.tsv` 和 `order_lines.tsv` 管理。
