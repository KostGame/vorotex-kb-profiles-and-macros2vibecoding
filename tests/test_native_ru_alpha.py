import json
import re
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

import generate_native_ru_alpha as gen


class NativeFixtureTests(unittest.TestCase):
    def read_fixture(self, name):
        return json.loads((ROOT / "tests" / "fixtures" / name).read_text(encoding="utf-8"))

    def read_text_macro_fixture(self, name):
        return json.loads((ROOT / "devices" / "k15-pro" / "fixtures" / "text-macros" / name).read_text(encoding="utf-8"))

    def test_cycle_fixture_targets_only_rpt_type(self):
        fixture = self.read_fixture("native-cycle.example.json")
        self.assertEqual(fixture["before"]["macRpt"], fixture["after"]["macRpt"])
        self.assertEqual(fixture["before"]["rptType"], 1)
        self.assertEqual(fixture["after"]["rptType"], 0)

    def test_corrected_new_line_fixture(self):
        fixture = self.read_fixture("native-new-line.example.json")
        self.assertEqual(fixture["macVal"], [225, 40, 40, 225])
        self.assertEqual(fixture["macSta"], [1, 1, 2, 2])
        self.assertEqual(fixture["macDly"], [10, 10, 10, 10])
        self.assertEqual(fixture["macRpt"], 1)
        self.assertEqual(fixture["rptType"], 0)

    def test_enter_binding_fixture(self):
        fixture = self.read_fixture("native-enter-binding.example.json")
        self.assertEqual(fixture["before"]["MemMacId"], 0)
        self.assertEqual(fixture["after"]["MemMacId"], 11)
        self.assertEqual(fixture["after"]["grpGuid"], "55492475-3604-4D74-996C-50B165062B5E")

    def test_space_binding_fixture(self):
        fixture = self.read_fixture("native-space-binding.example.json")
        self.assertEqual(fixture["KBKey"], 700)
        self.assertEqual(fixture["MemMacId"], 12)
        self.assertEqual(fixture["grpGuid"], gen.GROUP_GUID)
        self.assertEqual(fixture["macro"], "VIBE_15_ACCEPT_RU")

    def test_bottom_binding_fixture_is_observed_not_universal(self):
        fixture = self.read_fixture("native-bottom-binding.example.json")
        self.assertEqual(fixture["minus"]["MemMacId"], 13)
        self.assertEqual(fixture["plus"]["MemMacId"], 14)
        self.assertIsNone(fixture["space"]["MemMacId"])

    def test_language_selector_fixture(self):
        fixture = self.read_fixture("native-language-selector.example.json")
        self.assertEqual(fixture["RU"]["values"], [224, 225, 31, 31, 225, 224])
        self.assertEqual(fixture["EN"]["values"], [224, 225, 30, 30, 225, 224])

    def test_joystick_click_fixture(self):
        fixture = self.read_fixture("native-joystick-click.example.json")
        self.assertEqual(fixture["storageField"], "btn_KBKey_Enter")
        self.assertEqual(fixture["KBKey"], 40)
        self.assertFalse(fixture["macroBinding"])

    def test_export_current_and_export_all_mode_fixture(self):
        fixture = self.read_fixture("native-profile-mode.example.json")
        self.assertEqual(fixture["exportCurrent"]["SingleProfile"], 1)
        self.assertEqual(fixture["exportAll"]["SingleProfile"], 0)
        self.assertEqual(fixture["exportCurrent"]["profileCount"], 1)
        self.assertEqual(fixture["exportAll"]["profileCount"], 2)

    def test_two_profile_semantics_fixture(self):
        fixture = self.read_fixture("native-two-profile-space.example.json")
        self.assertEqual(fixture["profileA"]["role"], "TOOLS_AUTH")
        self.assertEqual(fixture["profileA"]["space"], "Подтверждаю")
        self.assertEqual(fixture["profileB"]["role"], "MAIN_VIBECODING")
        self.assertEqual(fixture["profileB"]["space"], "Давай дальше, без push/merge")

    def test_public_tree_has_no_raw_exports_or_machine_paths(self):
        tracked = subprocess.check_output(["git", "ls-files"], cwd=ROOT, text=True).splitlines()
        forbidden_names = {"Profile0.json", "Profile1.json", "macroConfig.json", "DeviceFeature.ini"}
        self.assertTrue(forbidden_names.isdisjoint({Path(item).name for item in tracked}))
        for item in tracked:
            content = (ROOT / item).read_text(encoding="utf-8", errors="ignore")
            self.assertIsNone(re.search(r"[A-Za-z]:\\\\Users\\\\|[A-Za-z]:\\\\AI_AGENT_PROJECTS", content))

    def test_generated_standalone_public_fixture_is_sanitized_and_exact(self):
        fixture = self.read_text_macro_fixture("generated-standalone-canary.example.json")
        manifest = self.read_text_macro_fixture("manifest.example.json")
        self.assertEqual(fixture["fixtureId"], "tmac-generated-standalone-canary")
        self.assertEqual(fixture["source"], "repository-serializer-generated")
        self.assertEqual(fixture["transport"], ".Macro.Config")
        self.assertEqual(fixture["verification"], "official-vorotex-imported")
        self.assertEqual(fixture["groupName"], "TMAC_CANARY_GENERATED")
        self.assertEqual(fixture["macroCount"], 1)
        macro = fixture["macros"][0]
        self.assertEqual(macro["name"], "TMAC_GEN_TEXT")
        self.assertEqual(macro["visibleText"], "K15TEST")
        self.assertEqual(macro["activeEventCount"], 14)
        self.assertEqual(macro["activeHidValues"], [14, 14, 30, 30, 34, 34, 23, 23, 8, 8, 22, 22, 23, 23])
        self.assertEqual(macro["activeStates"], [1, 2] * 7)
        self.assertEqual(macro["activeDelaysMs"], [1] * 14)
        self.assertEqual(macro["arrayCapacity"], 500)
        self.assertEqual((macro["macRpt"], macro["rptType"]), (1, 0))
        self.assertEqual(manifest["officialStatus"]["GENERATED_MACRO_CONFIG_IMPORT"], "PASS")
        self.assertEqual(manifest["parentAcceptanceMatrix"]["TMAC001_PARENT_READY_TO_CLOSE"], "NO")
        public_text = "\n".join((ROOT / "devices" / "k15-pro" / "fixtures" / "text-macros" / name).read_text(encoding="utf-8")
                                  for name in ("README.md", "generated-standalone-canary.example.json", "manifest.example.json"))
        self.assertNotRegex(public_text, r"[A-Za-z]:[\\/]")
        self.assertTrue(fixture["sanitization"]["realGuidsReplaced"])
        self.assertTrue(fixture["sanitization"]["machinePathsOmitted"])
        self.assertTrue(fixture["sanitization"]["rawPackageExcluded"])


class SerializerTests(unittest.TestCase):
    def native_template(self):
        """Sanitized stand-in for the full native KB template sections."""
        config = gen.minimal_kb_config()
        config["FnKey"] = {"btn_KBKey_native_sentinel": 0}
        config["FnKeyMacro"] = {"btn_KBKey_native_sentinel": gen.empty_macro_binding()}
        config["KBKey"]["btn_KBKey_native_sentinel"] = 91
        config["KBKeyMacro"]["btn_KBKey_native_sentinel"] = gen.empty_macro_binding()
        config["KBKey"]["btn_KB_Scr_Up0"] = 234
        config["KBKey"]["btn_KB_Scr_Dn0"] = 233
        config["FnKey"]["btn_KB_Scr_Up0"] = 234
        config["FnKey"]["btn_KB_Scr_Dn0"] = 233
        config["KBled"] = [{"nativeLedSentinel": 0}]
        return {"KBconfig": config}

    def native_lighting_fixture(self):
        def record(index, color, brightness=4):
            return {"brightnessvalue": brightness, "ctrl_KBLed_Col0": color,
                    "ctrl_KBLed_Col1": [0, 4278190337], "ctrl_KBLed_Col2": [0, 4278190335],
                    "ctrl_KBLed_Col3": [0, 4294967040], "ctrl_KBLed_Col4": [0, 4286578816],
                    "ctrl_KBLed_Col5": [0, 4278255615], "ctrl_KBLed_Col6": [0, 4294967295],
                    "curselmode": index, "directionsel": 0, "frequencyvalue": 4}
        bank_a = [record(i, [1, 4278255360] if i == 0 else [0, 122914128]) for i in range(14)]
        bank_b = [record(i, [0, 122914128]) for i in range(14)]
        bank_b[0]["ctrl_KBLed_Col6"] = [1, 4294967295]
        return {"KBconfig": {"KBled": [bank_a, bank_b, []]}}

    def macro(self, profile, action):
        spec = gen.PROFILE_SPECS[profile]
        name, _, _ = next(item for item in spec["macros"] if item[1] == action)
        package = gen.serialize_macro(profile)
        return next(item for item in package["MacroInfo"] if bytes(item["MacroName"]).decode() == name)

    def test_all_fifteen_macros_use_native_cycle_and_mixed_timing(self):
        for profile in ("A", "B"):
            package = gen.serialize_macro(profile)
            self.assertEqual(len(package["MacroInfo"]), 15)
            self.assertEqual(package["GrpGuid"], gen.PROFILE_SPECS[profile]["groupGuid"])
            for macro in package["MacroInfo"]:
                data = macro["macData"]
                self.assertEqual(data["macRpt"], 1)
                self.assertEqual(data["rptType"], 0)
                self.assertEqual(len(data["macDly"][: data["num"]]), data["num"])
                if data["num"] and any(d == 1 for d in data["macDly"][: data["num"]]):
                    self.assertIn(1, data["macDly"][: data["num"]])

    def test_new_line_is_shift_enter_at_5ms(self):
        for profile in ("A", "B"):
            macro = self.macro(profile, "NEW_LINE")["macData"]
            self.assertEqual(macro["num"], 4)
            self.assertEqual(macro["macVal"][:4], [225, 40, 40, 225])
            self.assertEqual(macro["macSta"][:4], [1, 1, 2, 2])
            self.assertEqual(macro["macDly"][:4], [5, 5, 5, 5])

    def test_profile_a_exact_map(self):
        self.assertEqual(gen.PROFILE_SPECS["A"]["bindings"], [
            ("1", "COPY"), ("2", "PASTE"), ("3", "CUT"), ("4", "UNDO"),
            ("5", "REDO"), ("6", "SELECT_ALL"), ("7", "REPORT"),
            ("8", "HERE_IS_REPORT"), ("9", "CODE_FENCE"),
            ("0", "REPORT_FROM_CLIPBOARD"), (".", "STATUS_FULL"),
            ("Enter", "NEW_LINE"), ("-", "STOP"), ("+", "REPORT_NEXT_CHAT"),
            ("Space", "ACCEPT_OR_APPROVE")])

    def test_profile_b_exact_map_and_safe_space(self):
        self.assertEqual(gen.PROFILE_SPECS["B"]["bindings"][-1], ("Space", "SAFE_CONTINUE"))
        self.assertEqual(gen.PROFILE_SPECS["B"]["bindings"][-2], ("+", "ACCEPTED"))
        self.assertNotIn("ACCEPT_OR_APPROVE", [a for _, a in gen.PROFILE_SPECS["B"]["bindings"]])
        self.assertEqual(gen.RU_OUTPUTS["SAFE_CONTINUE"], "Давай дальше, без push/merge")
        self.assertEqual(gen.RU_OUTPUTS["ACCEPTED"], "Принимается")

    def test_profile_b_plus_accepted_preserves_binding_slot_and_guid(self):
        package, _ = gen.serialize_kb("B", self.native_template(), event_delay_ms=5)
        binding = package["KBconfig"]["KBKeyMacro"]["btn_KBKey_KeyPadAdd"]
        self.assertEqual(binding["MemMacId"], 14)
        self.assertEqual(binding["macGuid"], "B4A6DEAB-4CCD-4761-8E19-E4D984005A76")
        macro = next(item for item in package["MacroGrpInfo"][0]["MacroInfo"]
                     if bytes(item["MacroName"]).decode("ascii") == "VIBE_14_ACCEPTED_RU")
        self.assertEqual(macro["MacroGuid"], binding["macGuid"])
        values = macro["macData"]["macVal"][:macro["macData"]["num"]]
        states = macro["macData"]["macSta"][:macro["macData"]["num"]]
        expected_values, expected_states = gen.concat_events(
            gen.selector_events("RU"), gen.hid_events("Принимается ", "RU"))
        self.assertEqual(values, expected_values)
        self.assertEqual(states, expected_states)
        self.assertEqual(values[-2:], [44, 44])
        self.assertNotIn(40, values)

    def test_profile_b_other_bindings_and_profile_a_plus_are_isolated(self):
        expected_b = [("1", "CHECK"), ("2", "NEXT"), ("3", "AGENT_PROMPT"), ("4", "FIX"),
                      ("5", "PUBLISH"), ("6", "MERGE"), ("7", "CREATE"), ("8", "CONTINUE"),
                      ("9", "REVIEW"), ("0", "DONE"), (".", "STATUS_SHORT"), ("Enter", "NEW_LINE"),
                      ("-", "STOP"), ("+", "ACCEPTED"), ("Space", "SAFE_CONTINUE")]
        self.assertEqual(gen.PROFILE_SPECS["B"]["bindings"], expected_b)
        self.assertIn(("+", "REPORT_NEXT_CHAT"), gen.PROFILE_SPECS["A"]["bindings"])
        self.assertEqual(gen.MACROS_A[13][0], "TOOLS_14_REPORT_NEXT_CHAT_RU")

    def test_ordinary_text_commands_append_exactly_one_ascii_space(self):
        self.assertEqual(gen.TEXT_COMMAND_SUFFIX, " ")
        for profile, action, visible in (("B", "CHECK", "Проверь"),
                                         ("B", "NEXT", "Следующий шаг"),
                                         ("A", "ACCEPT_OR_APPROVE", "Подтверждаю")):
            data = self.macro(profile, action)["macData"]
            expected_values, expected_states = gen.hid_events(visible + gen.TEXT_COMMAND_SUFFIX, "RU")
            self.assertEqual(data["macVal"][6:6 + len(expected_values)], expected_values)
            self.assertEqual(data["macSta"][6:6 + len(expected_states)], expected_states)
            self.assertEqual(data["macVal"][6 + len(expected_values) - 2:6 + len(expected_values)], [44, 44])
        check = self.macro("B", "CHECK")["macData"]
        self.assertEqual(check["num"], 6 + len(gen.hid_events("Проверь ", "RU")[0]))
        self.assertEqual(check["num"], 2 + 6 + len(gen.hid_events("Проверь", "RU")[0]))
        self.assertNotEqual(gen.TEXT_COMMAND_SUFFIX, ". ")

    def test_shortcut_macros(self):
        expected = {"COPY": [224, 6, 6, 224],
                    "CUT": [224, 27, 27, 224], "UNDO": [224, 29, 29, 224],
                    "REDO": [224, 225, 29, 29, 225, 224], "SELECT_ALL": [224, 4, 4, 224]}
        for action, values in expected.items():
            data = self.macro("A", action)["macData"]
            self.assertEqual(data["macVal"][:len(values)], values)

    def test_profile_a_paste_uses_ctrl_v_then_safe_new_line(self):
        data = self.macro("A", "PASTE")["macData"]
        values = data["macVal"][:data["num"]]
        states = data["macSta"][:data["num"]]
        expected_values, expected_states = gen.concat_events(gen.key_chord(25, (224,)), gen.shift_enter_events())
        self.assertEqual(values, expected_values)
        self.assertEqual(states, expected_states)

    def test_code_fence_is_exactly_three_ascii_backticks_and_returns_ru(self):
        data = self.macro("A", "CODE_FENCE")["macData"]
        self.assertEqual(data["macVal"][:6], [224, 225, 30, 30, 225, 224])
        self.assertEqual(data["macVal"][6:12], [53, 53, 53, 53, 53, 53])
        self.assertEqual(data["macVal"][0:data["num"]][-6:], [224, 225, 31, 31, 225, 224])
        self.assertEqual(data["num"], 18)

    def test_report_from_clipboard_has_exact_chatgpt_composer_sequence(self):
        data = self.macro("A", "REPORT_FROM_CLIPBOARD")["macData"]
        values = data["macVal"][:data["num"]]
        states = data["macSta"][:data["num"]]
        expected_values, expected_states = gen.concat_events(
            gen.selector_events("RU"),
            gen.hid_events("Вот отчет", "RU"),
            gen.shift_enter_events(),
            gen.selector_events("EN"),
            gen.hid_events("```", "EN"),
            gen.shift_enter_events(),
            gen.key_chord(25, (224,)),
            gen.shift_enter_events(),
            gen.selector_events("RU"),
        )
        self.assertEqual(values, expected_values)
        self.assertEqual(states, expected_states)

        ctrl_v = [224, 25, 25, 224]
        ctrl_v_index = next(index for index in range(len(values) - 3) if values[index:index + 4] == ctrl_v)
        post_paste = values[ctrl_v_index + len(ctrl_v):]
        expected_tail, _ = gen.concat_events(gen.shift_enter_events(), gen.selector_events("RU"))
        self.assertEqual(post_paste, expected_tail, "only Shift+Enter and the final RU selector may follow Ctrl+V")
        self.assertNotIn(53, post_paste, "closing fence must not follow Ctrl+V")
        self.assertEqual(values.count(40), 6, "the final post-paste Shift+Enter is required")
        self.assertEqual(values.count(53), 6, "only the opening three-backtick fence is allowed")
        self.assertEqual(values[-6:], [224, 225, 31, 31, 225, 224])
        report_start = len(gen.selector_events("RU")[0])
        report_end = report_start + len(gen.hid_events("Вот отчет", "RU")[0])
        self.assertNotEqual(values[report_end - 2:report_end], [44, 44], "structural text must not receive suffix")

    def test_ru_hid_mapping_and_cyrillic_ge(self):
        values, states = gen.hid_events("Проверь", "RU")
        self.assertEqual(values, [10, 10, 11, 11, 13, 13, 7, 7, 23, 23, 11, 11, 16, 16])
        self.assertEqual(states, [1, 2] * 7)
        self.assertEqual(gen.hid_events("г", "RU"), ([24, 24], [1, 2]))

    def test_safe_continue_uses_ru_and_en_segments(self):
        data = self.macro("B", "SAFE_CONTINUE")["macData"]
        values = data["macVal"][:data["num"]]
        self.assertIn([224, 225, 31, 31, 225, 224], [values[i:i + 6] for i in range(len(values) - 5)])
        self.assertIn([224, 225, 30, 30, 225, 224], [values[i:i + 6] for i in range(len(values) - 5)])
        self.assertNotIn(40, values)

    def test_kb_proven_bindings_and_joystick_for_both_profiles(self):
        for profile in ("A", "B"):
            package, unresolved = gen.serialize_kb(profile, self.native_template())
            macros = package["KBconfig"]["KBKeyMacro"]
            self.assertEqual(unresolved, [])
            self.assertEqual(len(package["MacroGrpInfo"]), 1)
            self.assertEqual(package["MacroGrpInfo"][0]["GrpGuid"], gen.PROFILE_SPECS[profile]["groupGuid"])
            self.assertEqual(len(package["MacroGrpInfo"][0]["MacroInfo"]), 15)
            self.assertEqual(package["KBconfig"]["KBKey"]["btn_KBKey_Enter"], 40)
            self.assertEqual(macros["btn_KBKey_Enter"]["MemMacId"], 0)
            self.assertEqual(package["SingleProfile"], 1)
            self.assertEqual(len([x for x in macros if macros[x]["grpGuid"]]), 15)

    def test_minimal_kb_shape_and_template_remain_supported(self):
        minimal, _ = gen.serialize_kb("B")
        self.assertEqual(minimal["KBconfig"]["FnKey"], {})
        self.assertEqual(minimal["KBconfig"]["FnKeyMacro"], {})
        self.assertEqual(minimal["KBconfig"]["KBled"], [])
        template = self.native_template()
        package, _ = gen.serialize_kb("B", template)
        self.assertEqual(package["KBconfig"]["FnKey"], template["KBconfig"]["FnKey"])
        self.assertEqual(package["KBconfig"]["FnKeyMacro"], template["KBconfig"]["FnKeyMacro"])
        self.assertEqual(package["KBconfig"]["KBled"], template["KBconfig"]["KBled"])
        self.assertEqual(package["KBconfig"]["KBKey"]["btn_KBKey_native_sentinel"], 91)

    def test_profile_lighting_banks_are_copied_exactly(self):
        lighting = self.native_lighting_fixture()
        for profile, index in (("A", 0), ("B", 1)):
            package, _ = gen.serialize_kb(profile, self.native_template(), profile_lighting_template=lighting)
            self.assertEqual(package["KBconfig"]["KBled"], lighting["KBconfig"]["KBled"][index])
            self.assertEqual(len(package["KBconfig"]["KBled"]), 14)
        package, _ = gen.serialize_kb("A", self.native_template(), profile_lighting_template=lighting)
        self.assertEqual(package["KBconfig"]["KBled"][0]["brightnessvalue"], 4)
        self.assertEqual(package["KBconfig"]["KBled"][0]["ctrl_KBLed_Col0"], [1, 4278255360])

    def test_profile_lighting_fixture_fails_closed_on_missing_bank(self):
        with self.assertRaises(ValueError):
            gen.serialize_kb("A", self.native_template(), profile_lighting_template={"KBconfig": {"KBled": [[]]}})

    def test_profile_a_encoder_is_vertical_scroll_and_profile_b_is_unchanged(self):
        template = self.native_template()
        profile_a, _ = gen.serialize_kb("A", template, profile_lighting_template=self.native_lighting_fixture())
        self.assertEqual(profile_a["KBconfig"]["KBKey"]["btn_KB_Scr_Up0"], 304)
        self.assertEqual(profile_a["KBconfig"]["KBKey"]["btn_KB_Scr_Dn0"], 305)
        self.assertEqual(profile_a["KBconfig"]["FnKey"]["btn_KB_Scr_Up0"], 234)
        self.assertEqual(profile_a["KBconfig"]["FnKey"]["btn_KB_Scr_Dn0"], 233)
        profile_b, _ = gen.serialize_kb("B", template, profile_lighting_template=self.native_lighting_fixture())
        self.assertEqual(profile_b["KBconfig"]["KBKey"]["btn_KB_Scr_Up0"], 234)
        self.assertEqual(profile_b["KBconfig"]["KBKey"]["btn_KB_Scr_Dn0"], 233)
        self.assertEqual(profile_b["KBconfig"]["FnKey"]["btn_KB_Scr_Up0"], 234)
        self.assertEqual(profile_b["KBconfig"]["FnKey"]["btn_KB_Scr_Dn0"], 233)

    def test_configurable_timing_uses_1ms_text_and_5ms_structural_policy(self):
        self.assertEqual(gen.DEFAULT_EVENT_DELAY_MS, 1)
        self.assertEqual(gen.OFFICIAL_RELEASE_MIN_DELAY_MS, 1)
        self.assertEqual(gen.TEXT_KEY_EVENT_DELAY_MS, 1)
        self.assertEqual(gen.LAYOUT_SELECTOR_EVENT_DELAY_MS, 5)
        self.assertEqual(gen.STRUCTURAL_EVENT_DELAY_MS, 5)
        self.assertEqual(gen.validate_official_import_delay(1), 1)
        package = gen.serialize_macro("A", event_delay_ms=1)
        self.assertTrue(any(d == 5 for m in package["MacroInfo"] for d in m["macData"]["macDly"][:m["macData"]["num"]]))
        with self.assertRaises(ValueError):
            gen.serialize_macro("A", event_delay_ms=0)
        with self.assertRaises(ValueError):
            gen.validate_official_import_delay(0)
        self.assertEqual(gen.validate_official_import_delay(1, True), 1)

    def test_standalone_k15test_macro_uses_disposable_identity_and_native_shape(self):
        package = gen.serialize_standalone_text_macro(
            "TMAC_CANARY_GENERATED", "11111111-1111-5111-8111-111111111111",
            "TMAC_GEN_TEXT", "22222222-2222-5222-8222-222222222222",
            "K15TEST", layout="EN", event_delay_ms=1)
        self.assertEqual(package["GrpName"], list(b"TMAC_CANARY_GENERATED"))
        self.assertEqual(package["GrpGuid"], "11111111-1111-5111-8111-111111111111")
        self.assertEqual(len(package["MacroInfo"]), 1)
        macro = package["MacroInfo"][0]
        self.assertEqual(bytes(macro["MacroName"]).decode("ascii"), "TMAC_GEN_TEXT")
        self.assertEqual(macro["MacroGuid"], "22222222-2222-5222-8222-222222222222")
        data = macro["macData"]
        self.assertEqual(data["num"], 14)
        self.assertEqual(data["macVal"][:14], [14, 14, 30, 30, 34, 34, 23, 23, 8, 8, 22, 22, 23, 23])
        self.assertEqual(data["macSta"][:14], [1, 2] * 7)
        self.assertEqual(data["macDly"][:14], [1] * 14)
        self.assertEqual(data["macRpt"], 1)
        self.assertEqual(data["rptType"], 0)
        self.assertEqual(len(data["macVal"]), gen.EVENT_CAPACITY)
        self.assertEqual(len(data["macSta"]), gen.EVENT_CAPACITY)
        self.assertEqual(len(data["macDly"]), gen.EVENT_CAPACITY)
        self.assertEqual(len(data["extVal"]), gen.EVENT_CAPACITY)
        self.assertTrue(all(value == 0 for value in data["macVal"][14:]))
        self.assertTrue(all(value == 0 for value in data["macSta"][14:]))
        self.assertTrue(all(value == 0 for value in data["macDly"][14:]))

    def test_shifted_ru_punctuation_is_encoded_as_chord(self):
        self.assertEqual(gen.hid_events(",", "RU"), ([225, 56, 56, 225], [1, 1, 2, 2]))
        self.assertEqual(gen.hid_events(":", "RU"), ([225, 35, 35, 225], [1, 1, 2, 2]))
        self.assertEqual(gen.hid_events(".", "RU"), ([225, 55, 55, 225], [1, 1, 2, 2]))

    def test_status_phrase_and_safe_continue_delay_segments(self):
        self.assertEqual(gen.RU_OUTPUTS["STATUS_FULL"], "Дай статус: что сделано, что осталось, блокеры и следующий шаг")
        self.assertEqual(gen.RU_OUTPUTS["STATUS_SHORT"], "Дай статус")
        for profile, action in (("A", "STATUS_FULL"), ("B", "STATUS_SHORT")):
            data = self.macro(profile, action)["macData"]
            self.assertEqual(data["macDly"][:6], [5] * 6)
            self.assertIn(1, data["macDly"][:data["num"]])
        values, states, delays = gen.safe_continue_event_stream()
        start = next(i for i in range(len(values)-3) if values[i:i+4] == [225, 56, 56, 225])
        self.assertEqual(values[start:start+4], [225, 56, 56, 225])
        self.assertEqual(states[start:start+4], [1, 1, 2, 2])
        self.assertNotIn(40, values)
        self.assertIn(5, delays); self.assertIn(1, delays)


if __name__ == "__main__":
    unittest.main()
