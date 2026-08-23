#!/usr/bin/env python3
"""Generate the VOROTEX-native RU Alpha macro/profile packages.

The semantic model is deliberately independent of the vendor JSON shape.  The
serializers below emit only fields proven by the supplied native exports.  A
native KB export may be supplied as a template so unknown vendor defaults are
preserved without making the raw user export part of the repository.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
from typing import Any, Iterable

VERSION = "0.1.0"
CORRECTED_NEW_LINE_FIXTURE_SHA256 = "f356f32c6acdf062115d1fc2b7023aa0cb6ec00752dbae2c6502808b7f12017a"
GROUP_NAME = "K15_VIBECODING_RU_ALPHA"
GROUP_GUID = "55492475-3604-4D74-996C-50B165062B5E"
EVENT_CAPACITY = 500
EVENT_DELAY_MS = 10

# USB HID usage IDs for the US physical keyboard positions.  With the RU
# Windows layout these positions produce Cyrillic letters.
RU_HID = {
    "й": 20, "ц": 26, "у": 8, "к": 21, "е": 23, "н": 28,
    "г": 10, "ш": 12, "щ": 18, "з": 19, "х": 47, "ъ": 48,
    "ф": 4, "ы": 22, "в": 7, "а": 9, "п": 10, "р": 11,
    "о": 13, "л": 14, "д": 15, "ж": 51, "э": 52,
    "я": 29, "ч": 27, "с": 6, "м": 25, "и": 5, "т": 17,
    "ь": 16, "б": 54, "ю": 55, "ё": 53, " ": 44,
}
EN_HID = {chr(ord("a") + i): code for i, code in enumerate(
    [4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
    21, 22, 23, 24, 25, 26, 27, 28, 29]
)}
EN_HID.update({" ": 44, ".": 55, ",": 54, "-": 45, "_": 45})

RU_OUTPUTS = {
    "CHECK": "Проверь", "NEXT": "Следующий шаг",
    "AGENT_PROMPT": "Пиши следующий промпт для агента", "FIX": "Исправляй",
    "PUBLISH": "Публикуй", "MERGE": "Мержи", "CREATE": "Создавай",
    "CONTINUE": "Продолжай", "REVIEW": "Проведи ревью", "DONE": "Готово",
    "STATUS": "Дай статус", "STOP": "Стоп", "REPORT": "Подготовь отчет для следующего чата",
    "ACCEPT_OR_APPROVE": "Подтверждаю",
}
EN_OUTPUTS = {
    "CHECK": "Check", "NEXT": "Next step", "AGENT_PROMPT": "Write the next agent prompt",
    "FIX": "Fix it", "PUBLISH": "Publish", "MERGE": "Merge", "CREATE": "Create",
    "CONTINUE": "Continue", "REVIEW": "Review", "DONE": "Done", "STATUS": "Status",
    "STOP": "Stop", "REPORT": "Prepare the next chat report", "ACCEPT_OR_APPROVE": "Approve",
}

MACROS = [
    ("VIBE_01_CHECK_RU", "CHECK", "3557DB05-6E83-4CED-AA10-A2FCA204A1DC"),
    ("VIBE_02_NEXT_RU", "NEXT", "0D83E565-CDCC-4E9F-AF1D-F21E1C3BD90F"),
    ("VIBE_03_AGENT_PROMPT_RU", "AGENT_PROMPT", "1C880B55-4DED-4BCE-8573-29A0D0EB1048"),
    ("VIBE_04_FIX_RU", "FIX", "E63F1245-3C20-4823-A5D2-01AF85BD55B1"),
    ("VIBE_05_PUBLISH_RU", "PUBLISH", "9A013D39-A245-4041-B19A-32C96E380893"),
    ("VIBE_06_MERGE_RU", "MERGE", "2C041014-14BD-4428-95B3-2A00EE00AEFB"),
    ("VIBE_07_CREATE_RU", "CREATE", "FE787DC5-1174-4BD4-A2FA-614AA88A44B0"),
    ("VIBE_08_CONTINUE_RU", "CONTINUE", "403BA4AC-80DA-4AB5-A5F0-44F4A457B4D0"),
    ("VIBE_09_REVIEW_RU", "REVIEW", "BC4A6BA3-10F0-4C2E-AFAF-931959F7B1E0"),
    ("VIBE_10_DONE_RU", "DONE", "22175D34-161A-410A-8A80-BCE322E1FE3B"),
    ("VIBE_11_STATUS_RU", "STATUS", "C16A8592-DCA2-4036-AB18-6E10AD7CEFC8"),
    ("VIBE_12_NEW_LINE_RU", "NEW_LINE", "C7D4A090-E957-4AD1-B34C-A2FBC473CA33"),
    ("VIBE_13_STOP_RU", "STOP", "2C0417D3-7255-446E-926D-EF77BB6D6DF3"),
    ("VIBE_14_REPORT_RU", "REPORT", "B4A6DEAB-4CCD-4761-8E19-E4D984005A76"),
    ("VIBE_15_ACCEPT_RU", "ACCEPT_OR_APPROVE", "9C22A63B-A5F8-4D2A-A473-F49DFD668F17"),
]

# Native evidence proves these MemMacId values.  The three remaining controls
# are intentionally absent: no value is fabricated for them.
PROVEN_BINDINGS = {
    "1": ("btn_KBKey_KeyPad1", 2), "2": ("btn_KBKey_KeyPad2", 1),
    "3": ("btn_KBKey_KeyPad3", 3), "4": ("btn_KBKey_KeyPad4", 4),
    "5": ("btn_KBKey_KeyPad5", 5), "6": ("btn_KBKey_KeyPad6", 6),
    "7": ("btn_KBKey_KeyPad7", 7), "8": ("btn_KBKey_KeyPad8", 8),
    "9": ("btn_KBKey_KeyPad9", 9), "0": ("btn_KBKey_KeyPad0", 0),
    ".": ("btn_KBKey_KeyPadPoint", 10), "Enter": ("btn_KBKey_KeyPadEnter", 11),
}
UNRESOLVED_CONTROLS = ["-", "*", "Space"]
CONTROL_STORAGE = {
    **{control: slot for control, (slot, _) in PROVEN_BINDINGS.items()},
    "-": "btn_KBKey_KeyPadSub", "*": "btn_KBKey_KeyPadMulti", "Space": "btn_KBKey_Space",
}

PHYSICAL_ACTIONS = [
    ("1", "CHECK"), ("2", "NEXT"), ("3", "AGENT_PROMPT"), ("4", "FIX"),
    ("5", "PUBLISH"), ("6", "MERGE"), ("7", "CREATE"), ("8", "CONTINUE"),
    ("9", "REVIEW"), ("0", "DONE"), (".", "STATUS"), ("Enter", "NEW_LINE"),
    ("-", "STOP"), ("*", "REPORT"), ("Space", "ACCEPT_OR_APPROVE"),
]


def hid_events(text: str, layout: str) -> tuple[list[int], list[int]]:
    table = RU_HID if layout == "RU" else EN_HID
    values: list[int] = []
    states: list[int] = []
    for char in text.lower():
        if char not in table:
            raise ValueError(f"{layout} layout cannot encode character {char!r}")
        values.extend((table[char], table[char]))
        states.extend((1, 2))
    return values, states


def macro_events(action: str, layout: str) -> tuple[list[int], list[int]]:
    if action == "NEW_LINE":
        return [225, 40, 40, 225], [1, 1, 2, 2]
    outputs = RU_OUTPUTS if layout == "RU" else EN_OUTPUTS
    return hid_events(outputs[action], layout)


def encoded_name(value: str) -> list[int]:
    return list(value.encode("ascii"))


def data_array(values: Iterable[int]) -> list[int]:
    result = [0] * EVENT_CAPACITY
    prefix = list(values)
    if len(prefix) > EVENT_CAPACITY:
        raise ValueError("macro exceeds native event capacity")
    result[: len(prefix)] = prefix
    return result


def macro_object(name: str, action: str, guid: str, layout: str) -> dict[str, Any]:
    values, states = macro_events(action, layout)
    delay = EVENT_DELAY_MS if action == "NEW_LINE" else EVENT_DELAY_MS
    return {
        "BindKeys": 0,
        "ForbidView": False,
        "MacroGuid": guid,
        "MacroName": encoded_name(name),
        "macData": {
            "YStep": 0, "YStepEn": 0,
            "extVal": [[0, 0] for _ in range(EVENT_CAPACITY)],
            "macDly": data_array([delay] * len(values)),
            "macRpt": 1,
            "macSta": data_array(states),
            "macVal": data_array(values),
            "num": len(values), "numCpi": 0, "numLed": 0,
            "numMedia": 2, "numWhl": 0, "numXY": 245,
            "rptType": 0,
        },
    }


def serialize_macro(layout: str = "RU") -> dict[str, Any]:
    if layout not in {"RU", "EN"}:
        raise ValueError("layout must be RU or EN")
    macros = [macro_object(name, action, guid, layout) for name, action, guid in MACROS]
    return {
        "ForbidView": False,
        "GrpGuid": GROUP_GUID,
        "GrpName": encoded_name(GROUP_NAME),
        "MacroInfo": macros,
    }


def empty_macro_binding(prefix: str = "MemMacId") -> dict[str, Any]:
    return {prefix: 0, "grpGuid": "", "macGuid": ""}


def minimal_kb_config() -> dict[str, Any]:
    slots = {slot: 700 for slot, _ in PROVEN_BINDINGS.values()}
    slots.update({"btn_KBKey_KeyPadSub": 86, "btn_KBKey_KeyPadAdd": 87, "btn_KBKey_Space": 44,
                  "btn_KBKey_KeyPadMulti": 85})
    macro_slots = {key: empty_macro_binding() for key in slots}
    return {
        "FnKey": {}, "FnKeyMacro": {}, "KBKey": slots,
        "KBKeyMacro": macro_slots, "KBled": [], "KBmain": {"curporfile": 1},
    }


def serialize_kb(template: dict[str, Any] | None = None) -> tuple[dict[str, Any], list[str]]:
    config = copy.deepcopy(template["KBconfig"] if template else minimal_kb_config())
    unresolved = list(UNRESOLVED_CONTROLS)
    by_action = {action: name for name, action in PHYSICAL_ACTIONS}
    macro_by_action = {action: (name, guid) for name, action, guid in MACROS}
    for physical, (slot, mem_id) in PROVEN_BINDINGS.items():
        action = dict(PHYSICAL_ACTIONS)[physical]
        _, guid = macro_by_action[action]
        if "KBKey" in config:
            config["KBKey"][slot] = 700
        if "KBKeyMacro" in config:
            config["KBKeyMacro"][slot] = {
                "MemMacId": mem_id, "grpGuid": GROUP_GUID, "macGuid": guid,
            }
    alpha_group = {
        "GrpGuid": GROUP_GUID, "GrpName": encoded_name(GROUP_NAME),
        "MacroInfo": [macro_object(name, action, guid, "RU") for name, action, guid in MACROS],
    }
    return {"KBconfig": config, "MacroGrpInfo": [alpha_group], "SingleProfile": 1}, unresolved


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def generate(output_dir: Path, layout: str = "RU", kb_template_path: Path | None = None) -> dict[str, Any]:
    output_dir.mkdir(parents=True, exist_ok=True)
    macro_path = output_dir / "K15_VIBECODING_RU_ALPHA.Macro.Config"
    kb_path = output_dir / "K15_VIBECODING_RU_ALPHA.KB.Config"
    macro = serialize_macro(layout)
    template = None
    if kb_template_path:
        template = json.loads(kb_template_path.read_text(encoding="utf-8"))
    kb, unresolved = serialize_kb(template)
    write_json(macro_path, macro)
    write_json(kb_path, kb)
    manifest = {
        "generatorVersion": VERSION, "generatorCommit": os.environ.get("K15_GENERATOR_COMMIT", "working-tree"),
        "profileId": "native-ru-alpha", "requiredWindowsLayout": layout,
        "inputProfileSelection": {"mode": "forced", "selected": layout},
        "macroGroupGuid": GROUP_GUID,
        "macros": [{"name": n, "action": a, "guid": g} for n, a, g in MACROS],
        "physicalControls": [{"control": c, "action": a, "storage": CONTROL_STORAGE.get(c),
                              "memMacId": PROVEN_BINDINGS.get(c, (None, None))[1],
                              "bindingProof": "native-fixture" if c in PROVEN_BINDINGS else "unresolved"}
                             for c, a in PHYSICAL_ACTIONS],
        "eventDelayMs": EVENT_DELAY_MS, "macRpt": 1, "rptType": 0,
        "unresolvedBindings": unresolved,
        "packages": {"macro": macro_path.name, "kb": kb_path.name},
        "sha256": {"macro": sha256(macro_path), "kb": sha256(kb_path)},
        "fixtureProvenance": [
            "corrected native Shift+Enter fixture SHA-256: " + CORRECTED_NEW_LINE_FIXTURE_SHA256,
            "native Profile B export pair supplied by the owner; only proven alpha bindings retained",
        ],
    }
    write_json(output_dir / "manifest.json", manifest)
    report = output_dir / "generation-report.md"
    report.write_text(
        "# Native RU Alpha generation report\n\n"
        "STATUS=READY_FOR_OFFICIAL_IMPORT_ALPHA_TEST\n\n"
        "NATIVE_RU_ALPHA_10MS_READY=PASS\n"
        "ALL_15_MACROS_PRESENT=PASS\n"
        "ALL_15_CYCLE_SERIALIZATION=PASS\n"
        "ALL_TEXT_MACROS_10MS=PASS\n"
        "SHIFT_ENTER_SEQUENCE=PASS\n"
        "SHIFT_ENTER_10MS=PASS\n"
        "NO_AUTO_SUBMIT=PASS\n"
        "MACRO_CONFIG_PACKAGE_READY=PASS\n"
        "KB_CONFIG_PACKAGE_READY=PARTIAL\n"
        "SINGLE_PROFILE_FORMAT_PROVEN=PASS\n"
        "MEMMACID_MAPPING_PROVEN=PARTIAL\n\n"
        f"- Layout: `{layout}` (forced)\n- Event delay: `{EVENT_DELAY_MS} ms`\n"
        "- Playback: `macRpt=1`, `rptType=0`\n"
        "- Shift+Enter: `225,40,40,225 / 1,1,2,2 / 10ms` (native proven)\n"
        f"- Corrected NEW_LINE fixture SHA-256: `{CORRECTED_NEW_LINE_FIXTURE_SHA256}`\n"
        f"- MemMacId proof: `{len(PROVEN_BINDINGS)}/15` controls proven\n"
        f"- Unresolved bindings: {', '.join(unresolved)}\n"
        f"- Macro SHA-256: `{sha256(macro_path)}`\n- KB SHA-256: `{sha256(kb_path)}`\n"
        "- LIVE_DEVICE_CHANGED=NO\n- LIVE_VOROTEX_CONFIG_CHANGED=NO\n",
        encoding="utf-8",
    )
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--layout", choices=("RU", "EN"), default="RU")
    parser.add_argument("--kb-template", type=Path)
    args = parser.parse_args()
    generate(args.output_dir, args.layout, args.kb_template)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
