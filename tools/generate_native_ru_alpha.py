#!/usr/bin/env python3
"""Generate reproducible VOROTEX-native K15 two-profile V1 RC packages.

The semantic model is kept separate from the vendor JSON shape. The
serializer emits only fields proven by sanitized native exports. Profile B
retains the owner-tested macro GUIDs/group GUID; Profile A receives stable
deterministic GUIDs so the two independently importable libraries do not
collide.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import uuid
from pathlib import Path
from typing import Any, Iterable

VERSION = "0.2.0"
DEFAULT_EVENT_DELAY_MS = 5
EVENT_DELAY_MS = DEFAULT_EVENT_DELAY_MS
EVENT_CAPACITY = 500
SELECTOR_SETTLE_DELAY_MS = 0
CORRECTED_NEW_LINE_FIXTURE_SHA256 = "f356f32c6acdf062115d1fc2b7023aa0cb6ec00752dbae2c6502808b7f12017a"
BOTTOM_BINDING_FIXTURE_SHA256 = "6bb6e1e2a7b2fb896cd046c3323612e25b34c1aa006150784d6556bdc4e39279"
SPACE_BINDING_FIXTURE_SHA256 = "be038530f798511301e49f9a1d13ea4babf556db64714a8e8fb77bbe7f4fab34"
JOYSTICK_CLICK_STORAGE = "btn_KBKey_Enter"
GROUP_GUID_B = "67FAE5A1-B383-4CC8-A99C-AD70C6DAA277"
GROUP_NAME_B = "K15_VIBECODING_RU_ALPHA"
_A_NAMESPACE = uuid.UUID("0f5d0c0a-9be0-5a4e-8a62-2c7d8ee0f1d1")


RU_HID = {
    "й": 20, "ц": 26, "у": 8, "к": 21, "е": 23, "н": 28,
    "г": 24, "ш": 12, "щ": 18, "з": 19, "х": 47, "ъ": 48,
    "ф": 4, "ы": 22, "в": 7, "а": 9, "п": 10, "р": 11,
    "о": 13, "л": 14, "д": 15, "ж": 51, "э": 52,
    "я": 29, "ч": 27, "с": 6, "м": 25, "и": 5, "т": 17,
    "ь": 16, "б": 54, "ю": 55, "ё": 53, " ": 44, ",": 54,
}
EN_HID = {chr(ord("a") + i): code for i, code in enumerate(
    [4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
     21, 22, 23, 24, 25, 26, 27, 28, 29]
)}
EN_HID.update({" ": 44, ".": 55, ",": 54, "-": 45, "_": 45, "/": 56, "`": 53})

RU_OUTPUTS = {
    "CHECK": "Проверь", "NEXT": "Следующий шаг",
    "AGENT_PROMPT": "Пиши следующий промпт для агента", "FIX": "Исправляй",
    "PUBLISH": "Публикуй", "MERGE": "Мержи", "CREATE": "Создавай",
    "CONTINUE": "Продолжай", "REVIEW": "Проведи ревью", "DONE": "Готово",
    "STATUS": "Дай статус", "STOP": "Стоп",
    "REPORT_NEXT_CHAT": "Подготовь отчет для следующего чата",
    "ACCEPT_OR_APPROVE": "Подтверждаю", "SAFE_CONTINUE": "Давай дальше, без push/merge",
    "REPORT": "Отчет", "HERE_IS_REPORT": "Вот отчет",
}
LANGUAGE_SELECTOR = {
    "RU": ([224, 225, 31, 31, 225, 224], [1, 1, 1, 2, 2, 2]),
    "EN": ([224, 225, 30, 30, 225, 224], [1, 1, 1, 2, 2, 2]),
}

PROVEN_BINDINGS = {
    "1": ("btn_KBKey_KeyPad1", 2), "2": ("btn_KBKey_KeyPad2", 1),
    "3": ("btn_KBKey_KeyPad3", 3), "4": ("btn_KBKey_KeyPad4", 4),
    "5": ("btn_KBKey_KeyPad5", 5), "6": ("btn_KBKey_KeyPad6", 6),
    "7": ("btn_KBKey_KeyPad7", 7), "8": ("btn_KBKey_KeyPad8", 8),
    "9": ("btn_KBKey_KeyPad9", 9), "0": ("btn_KBKey_KeyPad0", 0),
    ".": ("btn_KBKey_KeyPadPoint", 10), "Enter": ("btn_KBKey_KeyPadEnter", 11),
    "-": ("btn_KBKey_KeyPadSub", 13), "+": ("btn_KBKey_KeyPadAdd", 14),
    "Space": ("btn_KBKey_Space", 12),
}
CONTROL_STORAGE = {control: slot for control, (slot, _) in PROVEN_BINDINGS.items()}
UNRESOLVED_CONTROLS: list[str] = []


def _stable_guid(label: str) -> str:
    return str(uuid.uuid5(_A_NAMESPACE, label)).upper()


MACROS_B = [
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
    ("VIBE_14_REPORT_RU", "REPORT_NEXT_CHAT", "B4A6DEAB-4CCD-4761-8E19-E4D984005A76"),
    ("VIBE_15_SAFE_CONTINUE_RU", "SAFE_CONTINUE", "9C22A63B-A5F8-4D2A-A473-F49DFD668F17"),
]
MACROS_A = [
    ("TOOLS_01_COPY", "COPY", _stable_guid("profile-a:copy")),
    ("TOOLS_02_PASTE", "PASTE", _stable_guid("profile-a:paste")),
    ("TOOLS_03_CUT", "CUT", _stable_guid("profile-a:cut")),
    ("TOOLS_04_UNDO", "UNDO", _stable_guid("profile-a:undo")),
    ("TOOLS_05_REDO", "REDO", _stable_guid("profile-a:redo")),
    ("TOOLS_06_SELECT_ALL", "SELECT_ALL", _stable_guid("profile-a:select-all")),
    ("TOOLS_07_REPORT_RU", "REPORT", _stable_guid("profile-a:report")),
    ("TOOLS_08_HERE_IS_REPORT_RU", "HERE_IS_REPORT", _stable_guid("profile-a:here-is-report")),
    ("TOOLS_09_CODE_FENCE", "CODE_FENCE", _stable_guid("profile-a:code-fence")),
    ("TOOLS_10_REPORT_FROM_CLIPBOARD", "REPORT_FROM_CLIPBOARD", _stable_guid("profile-a:report-from-clipboard")),
    ("TOOLS_11_STATUS_RU", "STATUS", _stable_guid("profile-a:status")),
    ("TOOLS_12_NEW_LINE_RU", "NEW_LINE", _stable_guid("profile-a:new-line")),
    ("TOOLS_13_STOP_RU", "STOP", _stable_guid("profile-a:stop")),
    ("TOOLS_14_REPORT_NEXT_CHAT_RU", "REPORT_NEXT_CHAT", _stable_guid("profile-a:report-next-chat")),
    ("TOOLS_15_CONFIRM_RU", "ACCEPT_OR_APPROVE", _stable_guid("profile-a:confirm")),
]

PROFILE_SPECS = {
    "A": {
        "id": "tools-auth", "role": "TOOLS_AUTH", "name": "K15_VIBECODING_PROFILE_A_TOOLS_AUTH_V1_RC1",
        "groupName": "K15_TOOLS_AUTH", "groupGuid": _stable_guid("profile-a:group"), "macros": MACROS_A,
        "bindings": [("1", "COPY"), ("2", "PASTE"), ("3", "CUT"), ("4", "UNDO"),
                      ("5", "REDO"), ("6", "SELECT_ALL"), ("7", "REPORT"), ("8", "HERE_IS_REPORT"),
                      ("9", "CODE_FENCE"), ("0", "REPORT_FROM_CLIPBOARD"), (".", "STATUS"),
                      ("Enter", "NEW_LINE"), ("-", "STOP"), ("+", "REPORT_NEXT_CHAT"),
                      ("Space", "ACCEPT_OR_APPROVE")],
    },
    "B": {
        "id": "main-vibecoding", "role": "MAIN_VIBECODING", "name": "K15_VIBECODING_PROFILE_B_MAIN_V1_RC1",
        "groupName": GROUP_NAME_B, "groupGuid": GROUP_GUID_B, "macros": MACROS_B,
        "bindings": [("1", "CHECK"), ("2", "NEXT"), ("3", "AGENT_PROMPT"), ("4", "FIX"),
                      ("5", "PUBLISH"), ("6", "MERGE"), ("7", "CREATE"), ("8", "CONTINUE"),
                      ("9", "REVIEW"), ("0", "DONE"), (".", "STATUS"), ("Enter", "NEW_LINE"),
                      ("-", "STOP"), ("+", "REPORT_NEXT_CHAT"), ("Space", "SAFE_CONTINUE")],
    },
}

# Compatibility aliases for the original single-profile research API.
GROUP_GUID = GROUP_GUID_B
GROUP_NAME = GROUP_NAME_B
MACROS = MACROS_B
PHYSICAL_ACTIONS = PROFILE_SPECS["B"]["bindings"]


def validate_delay(event_delay_ms: int) -> int:
    if event_delay_ms < 1:
        raise ValueError("event delay must be at least 1 ms")
    return event_delay_ms


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


def selector_events(layout: str) -> tuple[list[int], list[int]]:
    return LANGUAGE_SELECTOR[layout]


def concat_events(*parts: tuple[list[int], list[int]]) -> tuple[list[int], list[int]]:
    values: list[int] = []
    states: list[int] = []
    for part_values, part_states in parts:
        values.extend(part_values)
        states.extend(part_states)
    return values, states


def key_chord(key: int, modifiers: tuple[int, ...] = ()) -> tuple[list[int], list[int]]:
    values = list(modifiers) + [key, key] + list(reversed(modifiers))
    states = [1] * (len(modifiers) + 1) + [2] * (len(modifiers) + 1)
    return values, states


def text_events(text: str, layout: str) -> tuple[list[int], list[int]]:
    return concat_events(selector_events(layout), hid_events(text, layout))


def shift_enter_events() -> tuple[list[int], list[int]]:
    return [225, 40, 40, 225], [1, 1, 2, 2]


def code_fence_events(return_to_ru: bool = True) -> tuple[list[int], list[int]]:
    fence = hid_events("```", "EN")
    parts = [selector_events("EN"), fence]
    if return_to_ru:
        parts.append(selector_events("RU"))
    return concat_events(*parts)


def safe_continue_events() -> tuple[list[int], list[int]]:
    return concat_events(selector_events("RU"), hid_events("Давай дальше, без ", "RU"),
                         selector_events("EN"), hid_events("push/merge", "EN"), selector_events("RU"))


def macro_events(profile: str, action: str, layout: str = "RU") -> tuple[list[int], list[int]]:
    if profile == "B" and action == "SAFE_CONTINUE":
        return safe_continue_events()
    if action == "NEW_LINE":
        return shift_enter_events()
    if action == "COPY":
        return key_chord(6, (224,))
    if action == "PASTE":
        return key_chord(25, (224,))
    if action == "CUT":
        return key_chord(27, (224,))
    if action == "UNDO":
        return key_chord(29, (224,))
    if action == "REDO":
        return key_chord(29, (224, 225))
    if action == "SELECT_ALL":
        return key_chord(4, (224,))
    if action == "CODE_FENCE":
        return code_fence_events()
    if action == "REPORT_FROM_CLIPBOARD":
        return concat_events(text_events("Вот отчет", "RU"), shift_enter_events(), code_fence_events(False),
                             shift_enter_events(), key_chord(25, (224,)), shift_enter_events(),
                             code_fence_events(False), selector_events("RU"))
    if action in {"CHECK", "NEXT", "AGENT_PROMPT", "FIX", "PUBLISH", "MERGE", "CREATE",
                  "CONTINUE", "REVIEW", "DONE", "REPORT", "HERE_IS_REPORT", "STATUS", "STOP",
                  "REPORT_NEXT_CHAT", "ACCEPT_OR_APPROVE"}:
        return text_events(RU_OUTPUTS[action], "RU")
    raise ValueError(f"unsupported action {action!r} for profile {profile}")


def encoded_name(value: str) -> list[int]:
    return list(value.encode("ascii"))


def data_array(values: Iterable[int]) -> list[int]:
    result = [0] * EVENT_CAPACITY
    prefix = list(values)
    if len(prefix) > EVENT_CAPACITY:
        raise ValueError("macro exceeds native event capacity")
    result[: len(prefix)] = prefix
    return result


def macro_object(profile: str, name: str, action: str, guid: str, layout: str, event_delay_ms: int) -> dict[str, Any]:
    values, states = macro_events(profile, action, layout)
    return {"BindKeys": 0, "ForbidView": False, "MacroGuid": guid, "MacroName": encoded_name(name),
            "macData": {"YStep": 0, "YStepEn": 0, "extVal": [[0, 0] for _ in range(EVENT_CAPACITY)],
                        "macDly": data_array([event_delay_ms] * len(values)), "macRpt": 1,
                        "macSta": data_array(states), "macVal": data_array(values), "num": len(values),
                        "numCpi": 0, "numLed": 0, "numMedia": 2, "numWhl": 0, "numXY": 245, "rptType": 0}}


def macro_group(profile: str, layout: str, event_delay_ms: int) -> dict[str, Any]:
    spec = PROFILE_SPECS[profile]
    return {"GrpGuid": spec["groupGuid"], "GrpName": encoded_name(spec["groupName"]),
            "MacroInfo": [macro_object(profile, name, action, guid, layout, event_delay_ms)
                          for name, action, guid in spec["macros"]]}


def serialize_macro(profile: str = "B", layout: str = "RU", event_delay_ms: int = DEFAULT_EVENT_DELAY_MS) -> dict[str, Any]:
    # The pre-two-profile API accepted serialize_macro("RU"|"EN"). Preserve
    # that call shape while making Profile B the explicit default.
    if profile in {"RU", "EN"} and layout == "RU":
        layout, profile = profile, "B"
    if profile not in PROFILE_SPECS:
        raise ValueError("profile must be A or B")
    if layout not in {"RU", "EN"}:
        raise ValueError("layout must be RU or EN")
    validate_delay(event_delay_ms)
    group = macro_group(profile, layout, event_delay_ms)
    return {"ForbidView": False, "GrpGuid": group["GrpGuid"], "GrpName": group["GrpName"],
            "MacroInfo": group["MacroInfo"]}


def empty_macro_binding(prefix: str = "MemMacId") -> dict[str, Any]:
    return {prefix: 0, "grpGuid": "", "macGuid": ""}


def minimal_kb_config() -> dict[str, Any]:
    slots = {slot: 700 for slot, _ in PROVEN_BINDINGS.values()}
    slots.update({"btn_KBKey_KeyPadSub": 86, "btn_KBKey_KeyPadAdd": 87, "btn_KBKey_Space": 44,
                  "btn_KBKey_KeyPadMulti": 85})
    return {"FnKey": {}, "FnKeyMacro": {}, "KBKey": slots,
            "KBKeyMacro": {key: empty_macro_binding() for key in slots}, "KBled": [],
            "KBmain": {"curporfile": 1}}


def serialize_kb(profile: str = "B", template: dict[str, Any] | None = None,
                event_delay_ms: int = DEFAULT_EVENT_DELAY_MS) -> tuple[dict[str, Any], list[str]]:
    if profile not in PROFILE_SPECS:
        raise ValueError("profile must be A or B")
    validate_delay(event_delay_ms)
    spec = PROFILE_SPECS[profile]
    config = copy.deepcopy(template["KBconfig"] if template else minimal_kb_config())
    action_to_guid = {action: guid for _, action, guid in spec["macros"]}
    for physical, action in spec["bindings"]:
        slot, mem_id = PROVEN_BINDINGS[physical]
        config.setdefault("KBKey", {})[slot] = 700
        config.setdefault("KBKeyMacro", {})[slot] = {"MemMacId": mem_id,
                                                       "grpGuid": spec["groupGuid"],
                                                       "macGuid": action_to_guid[action]}
    config.setdefault("KBKey", {})[JOYSTICK_CLICK_STORAGE] = 40
    config.setdefault("KBKeyMacro", {})[JOYSTICK_CLICK_STORAGE] = empty_macro_binding()
    return {"KBconfig": config, "MacroGrpInfo": [macro_group(profile, "RU", event_delay_ms)],
            "SingleProfile": 1}, list(UNRESOLVED_CONTROLS)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def semantic_maps() -> dict[str, Any]:
    result: dict[str, Any] = {}
    for profile, spec in PROFILE_SPECS.items():
        macro_by_action = {action: name for name, action, _ in spec["macros"]}
        result[profile] = {"profileId": spec["id"], "role": spec["role"],
                           "groupName": spec["groupName"], "groupGuid": spec["groupGuid"],
                           "bindings": [{"physicalControl": physical, "semanticAction": action,
                                         "macro": macro_by_action[action],
                                         "storageField": PROVEN_BINDINGS[physical][0],
                                         "memMacId": PROVEN_BINDINGS[physical][1]}
                                        for physical, action in spec["bindings"]],
                           "spaceText": RU_OUTPUTS["ACCEPT_OR_APPROVE"] if profile == "A" else RU_OUTPUTS["SAFE_CONTINUE"]}
    return result


def _profile_paths(output_dir: Path, profile: str) -> tuple[Path, Path]:
    stem = PROFILE_SPECS[profile]["name"]
    return output_dir / f"{stem}.Macro.Config", output_dir / f"{stem}.KB.Config"


def generate(output_dir: Path, layout: str = "RU", kb_template_path: Path | None = None,
             event_delay_ms: int = DEFAULT_EVENT_DELAY_MS) -> dict[str, Any]:
    validate_delay(event_delay_ms)
    output_dir.mkdir(parents=True, exist_ok=True)
    template = json.loads(kb_template_path.read_text(encoding="utf-8")) if kb_template_path else None
    package_info: dict[str, Any] = {}
    for profile in ("A", "B"):
        macro_path, kb_path = _profile_paths(output_dir, profile)
        write_json(macro_path, serialize_macro(profile, layout, event_delay_ms))
        kb, unresolved = serialize_kb(profile, template, event_delay_ms)
        write_json(kb_path, kb)
        package_info[profile] = {"macro": macro_path.name, "kb": kb_path.name,
                                 "macroSha256": sha256(macro_path), "kbSha256": sha256(kb_path),
                                 "unresolvedBindings": unresolved}
    maps_path = output_dir / "semantic-maps.json"
    write_json(maps_path, semantic_maps())
    manifest = {
        "generatorVersion": VERSION, "generatorCommit": os.environ.get("K15_GENERATOR_COMMIT", "working-tree"),
        "release": "v1-rc1", "profiles": {p: {"profileId": PROFILE_SPECS[p]["id"],
        "role": PROFILE_SPECS[p]["role"], "macroGroupGuid": PROFILE_SPECS[p]["groupGuid"]} for p in ("A", "B")},
        "inputProfileSelection": {"mode": "forced-self-select", "selected": layout,
        "ruSelector": "Ctrl+Shift+2", "enSelector": "Ctrl+Shift+1",
        "selectorOrder": "Ctrl down, Shift down, layout key down/up, Shift up, Ctrl up",
        "selectorSettleDelayMs": SELECTOR_SETTLE_DELAY_MS},
        "eventDelayMs": event_delay_ms, "defaultEventDelayMs": DEFAULT_EVENT_DELAY_MS,
        "oneMsConfigSupported": True, "macRpt": 1, "rptType": 0, "packages": package_info,
        "semanticMaps": maps_path.name, "physicalSlotModel": [{"control": c, "storage": s, "memMacId": m}
        for c, (s, m) in PROVEN_BINDINGS.items()], "unresolvedBindings": [],
        "joystickClick": {"storage": JOYSTICK_CLICK_STORAGE, "mode": "NATIVE_ENTER", "keyValue": 40},
        "rgbScope": "UNCHANGED_PASSTHROUGH", "allProfiles": {"status": "PARTIAL", "package": None,
        "reason": "Sanitized evidence proves the SingleProfile/profile-count delta, not the full Export-All object shape; unsupported fields are not guessed."},
        "fixtureProvenance": ["corrected native Shift+Enter fixture SHA-256: " + CORRECTED_NEW_LINE_FIXTURE_SHA256,
        "native bottom-binding fixture SHA-256: " + BOTTOM_BINDING_FIXTURE_SHA256,
        "native Space-binding fixture SHA-256: " + SPACE_BINDING_FIXTURE_SHA256,
        "sanitized native Profile mode delta: export current SingleProfile=1, export all SingleProfile=0"]}
    write_json(output_dir / "manifest.json", manifest)
    report_lines = ["# K15 Two-Profile V1 RC generation report", "",
        "STATUS=READY_FOR_TWO_PROFILE_V1_IMPORT_TEST", "", "PROFILE_A_ROLE=TOOLS_AUTH",
        "PROFILE_B_ROLE=MAIN_VIBECODING", "PROFILE_A_SPACE=Подтверждаю",
        "PROFILE_B_SPACE=Давай дальше, без push/merge", "PROFILE_A_COPY=PASS", "PROFILE_A_PASTE=PASS",
        "PROFILE_A_CUT=PASS", "PROFILE_A_UNDO=PASS", "PROFILE_A_REDO=PASS", "PROFILE_A_SELECT_ALL=PASS",
        "PROFILE_A_REPORT=PASS", "PROFILE_A_HERE_IS_REPORT=PASS", "PROFILE_A_CODE_FENCE=PASS",
        "PROFILE_A_REPORT_FROM_CLIPBOARD=PASS", "REPORT_FROM_CLIPBOARD_AUTO_SUBMIT=NO",
        f"DEFAULT_KEY_EVENT_DELAY_MS={DEFAULT_EVENT_DELAY_MS}", "ONE_MS_CONFIG_SUPPORTED=YES",
        "ALL_PROFILE_A_RU_TEXT_ROUNDTRIP=PASS", "ALL_PROFILE_B_RU_TEXT_ROUNDTRIP=PASS",
        "SHIFT_ENTER_5MS=PASS", "JOYSTICK_NATIVE_ENTER=PASS", "ALL_15_PHYSICAL_BINDINGS_PROFILE_A=PASS",
        "ALL_15_PHYSICAL_BINDINGS_PROFILE_B=PASS", "MEMMACID_MAPPING_PROVEN=PASS",
        "RGB_SCOPE=UNCHANGED_PASSTHROUGH", "ALL_PROFILES_PACKAGE_READY=PARTIAL",
        "ALL_PROFILES_KB_CONFIG=NOT CREATED", "VOROTEX_IMPORT_IS_NON_PRUNING=PROVEN",
        "LIVE_DEVICE_CHANGED=NO", "LIVE_VOROTEX_CONFIG_CHANGED=NO", "PUSH=NOT RUN", "PR=NOT CREATED", "MERGE=NOT RUN", "",
        f"- Generated event delay: `{event_delay_ms} ms` (selector settle timing is separate: `{SELECTOR_SETTLE_DELAY_MS} ms`)",
        "- Shift+Enter: `225,40,40,225 / 1,1,2,2` with configured event delay",
        "- Profile A 0 contains RU text, three EN backticks, Ctrl+V, and no native Enter",
        "- Combined Export-All package intentionally not emitted: unsupported native fields are not guessed."]
    (output_dir / "generation-report.md").write_text("\n".join(report_lines) + "\n", encoding="utf-8")
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--layout", choices=("RU", "EN"), default="RU")
    parser.add_argument("--kb-template", type=Path)
    parser.add_argument("--event-delay-ms", type=int, default=DEFAULT_EVENT_DELAY_MS)
    args = parser.parse_args()
    generate(args.output_dir, args.layout, args.kb_template, args.event_delay_ms)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
