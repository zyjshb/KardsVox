"""Generate the Japan voice pack with the original 日军 GPT-SoVITS v2Pro weights.

Run with the bundled GPT-SoVITS Python runtime. Existing valid WAV files are
skipped, so an interrupted batch can be resumed safely.
"""

import argparse
import csv
import faulthandler
import hashlib
import os
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import resample_poly


HERE = Path(__file__).resolve().parent
PROJECT = Path(r"E:\迅雷下载\GPT-SoVITS-v2pro-20250604")
ROOT = HERE.parents[1]
SOURCE = HERE / "voice_lines.tsv"
OUTPUT = ROOT / "Audio" / "Japan"
GPT = PROJECT / "GPT_weights_v2Pro" / "日军-e10.ckpt"
SOVITS = PROJECT / "SoVITS_weights_v2Pro" / "日军_e8_s240.pth"
REFERENCE = Path(r"E:\kazi\rijun-cleanup\curated\audio\rijun_015_002154197_002324246.wav")
PROMPT = "前線を下げるな！奴らを押し返すんだ！司令部を守れ！"
TARGET_RATE = 48000
TEXT_OVERRIDES = {
    # Spell the sustained cry phonetically so the model holds the final vowel
    # instead of clipping immediately after 「ざ」.
    "JP_INF_Y_CHARGE_01": "天皇陛下、ばんざあああい！",
}
SOURCE_RECORDED_IDS = set()


def model():
    import tempfile

    cache = ROOT.parent / "KardsVox.build" / "tts-cache"
    cache.mkdir(parents=True, exist_ok=True)
    os.environ["NUMBA_CACHE_DIR"] = str(cache)
    os.environ["TMP"] = str(cache)
    os.environ["TEMP"] = str(cache)
    tempfile.tempdir = str(cache)
    sys.dont_write_bytecode = True
    os.chdir(PROJECT)
    sys.path.insert(0, str(PROJECT))
    sys.path.insert(0, str(PROJECT / "GPT_SoVITS"))
    from GPT_SoVITS.TTS_infer_pack.TTS import TTS, TTS_Config

    # Model loading normally rewrites the vendor package's tts_infer.yaml.
    # This batch supplies every setting directly and keeps the installation untouched.
    TTS_Config.save_configs = lambda self, configs_path=None: None

    config = {
        "custom": {
            "device": "cuda",
            "is_half": True,
            "version": "v2Pro",
            "t2s_weights_path": str(GPT),
            "vits_weights_path": str(SOVITS),
            "cnhuhbert_base_path": "GPT_SoVITS/pretrained_models/chinese-hubert-base",
            "bert_base_path": "GPT_SoVITS/pretrained_models/chinese-roberta-wwm-ext-large",
        }
    }
    return TTS(config)


def valid(path: Path) -> bool:
    if not path.exists():
        return False
    try:
        info = sf.info(path)
        return info.samplerate == TARGET_RATE and info.channels == 1 and info.duration >= 0.3 and info.subtype == "PCM_16"
    except Exception:
        return False


def normalize(audio: np.ndarray, source_rate: int) -> np.ndarray:
    samples = np.asarray(audio, dtype=np.float32).reshape(-1)
    if np.issubdtype(np.asarray(audio).dtype, np.integer):
        samples /= np.iinfo(np.asarray(audio).dtype).max
    if not np.isfinite(samples).all() or samples.size / source_rate < 0.3:
        raise ValueError("invalid or too-short generated audio")
    if source_rate != TARGET_RATE:
        from math import gcd
        common = gcd(source_rate, TARGET_RATE)
        samples = resample_poly(samples, TARGET_RATE // common, source_rate // common)
    peak = float(np.max(np.abs(samples)))
    rms = float(np.sqrt(np.mean(samples * samples)))
    if peak < 0.001 or rms < 0.0005:
        raise ValueError("generated audio is silent")
    gain = min(0.125 / rms, 0.89 / peak)
    return np.clip(samples * gain, -1, 1)


def main():
    faulthandler.dump_traceback_later(120, repeat=False)
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, default=0, help="generate only the first N lines for a smoke test")
    parser.add_argument("--id", action="append", dest="ids", help="generate only this voice ID (repeatable)")
    parser.add_argument("--force", action="store_true", help="regenerate selected lines even when valid WAV files exist")
    args = parser.parse_args()
    rows = list(csv.DictReader(SOURCE.open(encoding="utf-8"), delimiter="\t"))
    assert len(rows) == 144 and len({row["id"] for row in rows}) == len(rows)
    assert GPT.is_file() and SOVITS.is_file() and REFERENCE.is_file()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    selected = rows[: args.limit] if args.limit else rows
    if args.ids:
        requested = set(args.ids)
        selected = [row for row in selected if row["id"] in requested]
        missing = requested - {row["id"] for row in selected}
        if missing:
            raise ValueError(f"unknown voice IDs: {sorted(missing)}")
    missing_recorded = [row["id"] for row in selected
                        if row["id"] in SOURCE_RECORDED_IDS and not valid(OUTPUT / f"{row['id']}.wav")]
    if missing_recorded:
        raise FileNotFoundError(f"source-recorded clips are missing: {missing_recorded}")
    pending = [row for row in selected if row["id"] not in SOURCE_RECORDED_IDS and
               (args.force or not valid(OUTPUT / f"{row['id']}.wav"))]
    print(f"selected={len(selected)} pending={len(pending)} output={OUTPUT}", flush=True)
    if not pending:
        return
    tts = model()
    faulthandler.cancel_dump_traceback_later()
    errors = []
    for index, row in enumerate(pending, 1):
        dest = OUTPUT / f"{row['id']}.wav"
        seed = int(hashlib.sha256(row["id"].encode()).hexdigest()[:8], 16) % 2147483647
        speed = {"solemn": 0.94, "firm": 1.0, "urgent": 1.04, "shout": 0.98}[row["take"]]
        if row["id"] == "JP_INF_Y_CHARGE_01":
            speed = 0.90
        text = TEXT_OVERRIDES.get(row["id"], row["ja"])
        request = {
            "text": text,
            "text_lang": "all_ja",
            "ref_audio_path": str(REFERENCE),
            "prompt_text": PROMPT,
            "prompt_lang": "all_ja",
            "text_split_method": "cut5",
            "batch_size": 1,
            "speed_factor": speed,
            "seed": seed,
            "parallel_infer": False,
            "repetition_penalty": 1.35,
            "streaming_mode": False,
            "return_fragment": False,
        }
        try:
            rate, audio = next(tts.run(request))
            samples = normalize(audio, rate)
            if row["id"] == "JP_INF_Y_CHARGE_01":
                # Preserve a short natural release so the shout never sounds
                # hard-cut when the playback device reaches the final sample.
                release = min(len(samples), int(TARGET_RATE * 0.16))
                if release:
                    samples[-release:] *= np.linspace(1.0, 0.0, release, dtype=np.float32)
                samples = np.concatenate((samples, np.zeros(int(TARGET_RATE * 0.32), dtype=np.float32)))
            temp = dest.with_suffix(".partial.wav")
            sf.write(temp, samples, TARGET_RATE, subtype="PCM_16")
            temp.replace(dest)
            print(f"{index}/{len(pending)} {row['id']} {len(samples) / TARGET_RATE:.2f}s", flush=True)
        except Exception as exc:
            errors.append((row["id"], str(exc)))
            print(f"ERROR {row['id']}: {exc}", flush=True)
    if errors:
        raise RuntimeError(f"{len(errors)} voice lines failed: {errors[:5]}")


if __name__ == "__main__":
    main()
