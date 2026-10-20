"""Build the Soviet card index from unpacked KARDS .uasset files.

Usage: python Data/Soviet/generate_catalog.py [unpacked Soviet directory]
"""

from collections import Counter, defaultdict
from pathlib import Path
import sys


HERE = Path(__file__).resolve().parent
DEFAULT_SOURCE = (
    HERE.parents[2]
    / "KardsVox.Analysis/kards/Content/Blueprints/Cards/Soviet"
)

AIRCRAFT = (
    "beriev_", "i_15_", "i_16_", "ilyushin_", "katalina", "la_3",
    "mig_", "p_39_", "p_40b_", "petlyakov_", "polikarpov_",
    "sturmovik", "tupolev_", "yak_",
)
ARMOR = (
    "a20_", "armored_train", "bt_", "iosef_stalin_", "is_i", "isu_",
    "kv_", "su_", "t_28_", "t_34", "t_35_", "t_60", "t_70", "t_80",
)
ARTILLERY = {
    "152mm_howitzer_gun", "45mm_anti_tank_gun", "52_k", "57mm_m1943",
    "76mm_howitzer_1939", "85mm_d44", "b4_203mm_howitzer",
    "bm_13n_us6", "katyusha", "katyusha_cam1",
    "light_anti_air_battery", "stz_5_katyusha",
}
CAVALRY = {"36th_cavalry", "60th_cavalry_regiment", "cossack_cavalry"}


def category(name: str) -> str:
    if name.startswith("card_event_"):
        return "ORDER"
    if name.startswith("card_location_"):
        return "LOCATION"
    if not name.startswith("card_unit_"):
        raise ValueError(f"Unknown card asset: {name}")
    unit = name.removeprefix("card_unit_")
    if unit in ARTILLERY:
        return "ARTILLERY"
    if unit in CAVALRY:
        return "CAVALRY"
    if unit.startswith(AIRCRAFT):
        return "AIR"
    if unit.startswith(ARMOR):
        return "TANK"
    return "INFANTRY"


def main() -> None:
    source = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else DEFAULT_SOURCE
    if not source.is_dir():
        raise SystemExit(f"Unpacked Soviet card directory not found: {source}")
    cards: dict[str, str] = {}
    sets: dict[str, Counter[str]] = defaultdict(Counter)
    for asset in source.rglob("*.uasset"):
        name = asset.stem.lower()
        if name in cards:
            raise ValueError(f"Duplicate Soviet card name: {name}")
        kind = category(name)
        cards[name] = kind
        sets[asset.relative_to(source).parts[0]][kind] += 1

    if len(cards) != 319 or len(sets) != 15:
        raise ValueError(f"Unexpected Soviet resource count: {len(cards)} cards, {len(sets)} folders")
    (HERE / "card_catalog.tsv").write_text(
        "internal_name\tcategory\n"
        + "".join(f"{name}\t{cards[name]}\n" for name in sorted(cards)),
        encoding="utf-8",
    )
    (HERE / "sets.tsv").write_text(
        "set\tunit\torder\tlocation\ttotal\n"
        + "".join(
            f"{set_name}\t{sum(count[k] for k in ('INFANTRY', 'ARTILLERY', 'CAVALRY', 'TANK', 'AIR'))}"
            f"\t{count['ORDER']}\t{count['LOCATION']}\t{sum(count.values())}\n"
            for set_name, count in sorted(sets.items())
        ),
        encoding="utf-8",
    )
    print(f"Indexed {len(cards)} Soviet card assets in {len(sets)} resource folders.")


if __name__ == "__main__":
    main()
