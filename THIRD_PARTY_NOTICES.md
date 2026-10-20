# Third party audio notices

## Japanese male hurt and grunt effects

Files `Audio/Sfx/Hurt/JP_HURT_01.wav` through `JP_HURT_10.wav` are selected and renamed from **RPG Male Vocal** by **konakuma**. They use the pack's short grunts plus the two Japanese `Itai` recordings. No audio from the previous generic hurt pack is included.

- Source: https://konakuma.itch.io/rpg-male-vocal
- Author permission on the source page: the full pack is free to use and the sound effects may be modified; credit is appreciated.
- Downloaded: 2026-09-22
- Original archive SHA-256: `EFF8CFD0F8934854B024EFC777717A5856963C6F79E6844F5F5A9DE9B4B16604`

The source recordings remain the work of konakuma. These source files are retained in the workspace for attribution, but the portable release no longer includes or plays them.

## Finnish voice generation

Finland audio uses the user-supplied Finland reference recording for voice conditioning. Pronunciation was synthesized locally with Meta's Finnish MMS TTS checkpoint, then converted with the locally installed Chatterbox S3Gen model. Model weights and the user's original reference recording are not included in the portable release.

- Meta MMS Finnish model: https://huggingface.co/facebook/mms-tts-fin (model license: CC-BY-NC 4.0). Model card and attribution: Meta / Massively Multilingual Speech.
- Chatterbox: https://github.com/resemble-ai/chatterbox (MIT).
- Pipeline, reference hashes and per-file generation records: Data/Finland/generation_report.md and generation.jsonl.
