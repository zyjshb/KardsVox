# 芬兰语音生成记录

生成与核对：2026-09-26。交付 80 个 WAV，共 250.60 秒；5 段开场合计 21.08 秒。原句、中文、用途、文件名及实际时长见配对台词表和声音素材清单。

## 素材和模型

- 用户录音：`C:\Users\djnio\Desktop\芬兰.wav`，91.208 秒，44.1 kHz 立体声。SHA-256：`acb54d011a50a8ee630fdc2105b558eb3b863ee86ea1173301ab400dc5653cd2`。
- 采用其中 14.20–23.94 秒，转单声道并加 8 ms 端点淡化，得到 `E:\kazi\RussianTTS\finland_v1_7_25\reference_finland_alt.wav`，9.74 秒。未调整参考音高或语速；裁剪信息见 `reference_finland_alt.json`。
- 芬兰语发音：[Meta MMS Finnish](https://huggingface.co/facebook/mms-tts-fin)，本机目录 `E:\kazi\RussianTTS\finland_v1_7_25\mms_model`。权重 SHA-256：`aa0b11a12cd64a46d7c8bf4c4ba21f7ec1b7da58003c6a1d026e6e4084ed5a4a`。
- 音色转换：本机 Chatterbox S3Gen，权重 `E:\kazi\RussianTTS\chatterbox_v3\model\s3gen.pt`，用上面的用户片段作目标音色。合成及转换都在本机完成，没有上传用户录音。
- 最初的 Chatterbox Multilingual V3 直接芬兰语生成有较多转写偏差，未采用那批样音。现采用芬兰语模型生成发音，再做音色转换；录音不是从其他国家复制的。

## 处理与质量记录

按固定 ID 派生随机种子，异常条目独立返修。每条生成参数、所选批次、源文件和最终文件哈希见 `generation.jsonl`。转换输入前后增加 250/200 ms 缓冲，输出根据波形裁去多余空白，保留约 60 ms 头部、150 ms 尾部余量和 5 ms 边缘淡化；没有用静音凑开场时长。

交付统一为 48 kHz、单声道、PCM16。后处理只做重采样、首尾空白处理及线性电平校准，不移调、不变速。目标 RMS 约 0.13，峰值最多 0.94；实际增益以逐条记录为准。日、苏、德原有录音未作音量处理。

已逐条检查空文件、WAV 数据完整性、采样格式、有限波形、非静音、削波、哈希重复和自动转写。使用本机 faster-whisper-large-v3-turbo，指定芬兰语 `fi`。ASR 的长短音、双辅音和句首辅音偏差需要人工试听判断；`建议复听清单.tsv` 记录较大转写偏差，不宣称机器检查能证明母语口音或音色完全正确。

## 重跑和单条返修

本机 Python：`E:\kazi\RussianTTS\.venv\Scripts\python.exe`，transformers 5.2.0、CUDA、soundfile、scipy。源生成脚本和原始各批次保存在 `E:\kazi\RussianTTS\finland_v1_7_25`；脚本副本在制作目录的 `tools/`，权重和用户原录音不进入便携包。

```powershell
& 'E:\kazi\RussianTTS\.venv\Scripts\python.exe' 'E:\kazi\RussianTTS\finland_v1_7_25\generate_native.py' --all --output-dir 'E:\kazi\RussianTTS\finland_v1_7_25\rerun'
```

单条返修把 `--all` 换成 `--ids FI_SUB_CMD_INTRO_1` 等清单 ID，可加 `--seed-offset 123 --noise-scale 0.4 --duration-noise 0.4`。更换工作目录可设置 `FINLAND_VOICE_WORKSPACE`；用 `--package` 指定包含 `Data/Finland/voice_generation_manifest.tsv` 的根目录。先用 `build_voice_manifest.py` 从台词表更新清单，再生成、复查、替换对应 WAV；不要复用旧 WAV 冒充改过的原句。

完整模型来源及许可标记见程序根目录 `THIRD_PARTY_NOTICES.md`。MMS 模型标记为 CC-BY-NC 4.0，Chatterbox 为 MIT；模型权重没有随程序分发。

## 1.7.26 开场重写

用户明确指出台词需要重写。本次五段改为敌军入侵、拒绝投降、森林和道路阻击、反击与前进军令，芬兰语原句和中文逐段对应。五段录音重新生成，共 21.08 秒；其余 75 条录音与 1.7.25 哈希相同。生成模型、参考音频与音色转换方法沿用 1.7.25，未调整音高和语速。

本批次：`E:\kazi\RussianTTS\finland_intro_v1_7_26`。使用原生成器 `E:\kazi\RussianTTS\finland_v1_7_25\generate_native.py`，传入 `--package E:\kazi\KardsVox.build\outputs\芬兰-v1.7.26`，并指定 `--ids FI_SUB_CMD_INTRO_1 FI_SUB_CMD_INTRO_2 FI_SUB_CMD_INTRO_3 FI_SUB_CMD_INTRO_4 FI_SUB_CMD_INTRO_5`。采样参数沿用默认值，电平校准脚本在本版本制作目录 tools 中。
