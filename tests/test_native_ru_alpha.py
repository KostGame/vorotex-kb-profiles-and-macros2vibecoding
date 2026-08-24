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


class SerializerTests(unittest.TestCase):
    def native_template(self):
        """Sanitized stand-in for the full native KB template sections."""
        config = gen.minimal_kb_config()
        config["FnKey"] = {"btn_KBKey_native_sentinel": 0}
        config["FnKeyMacro"] = {"btn_KBKey_native_sentinel": gen.empty_macro_binding()}
        config["KBKey"]["btn_KBKey_native_sentinel"] = 91
        config["KBKeyMacro"]["btn_KBKey_native_sentinel"] = gen.empty_macro_binding()
        config["KBled"] = [{"nativeLedSentinel": 0}]
        return {"KBconfig": config}

    def macro(self, profile, action):
        spec = gen.PROFILE_SPECS[profile]
        name, _, _ = next(item for item in spec["macros"] if item[1] == action)
        package = gen.serialize_macro(profile)
        return next(item for item in package["MacroInfo"] if bytes(item["MacroName"]).decode() == name)

    def test_all_fifteen_macros_use_native_cycle_and_5ms_timing(self):
        for profile in ("A", "B"):
            package = gen.serialize_macro(profile)
            self.assertEqual(len(package["MacroInfo"]), 15)
            self.assertEqual(package["GrpGuid"], gen.PROFILE_SPECS[profile]["groupGuid"])
            for macro in package["MacroInfo"]:
                data = macro["macData"]
                self.assertEqual(data["macRpt"], 1)
                self.assertEqual(data["rptType"], 0)
                self.assertTrue(all(delay == 5 for delay in data["macDly"][: data["num"]]))

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
            ("0", "REPORT_FROM_CLIPBOARD"), (".", "STATUS"),
            ("Enter", "NEW_LINE"), ("-", "STOP"), ("+", "REPORT_NEXT_CHAT"),
            ("Space", "ACCEPT_OR_APPROVE")])

    def test_profile_b_exact_map_and_safe_space(self):
        self.assertEqual(gen.PROFILE_SPECS["B"]["bindings"][-1], ("Space", "SAFE_CONTINUE"))
        self.assertNotIn("ACCEPT_OR_APPROVE", [a for _, a in gen.PROFILE_SPECS["B"]["bindings"]])
        self.assertEqual(gen.RU_OUTPUTS["SAFE_CONTINUE"], "Давай дальше, без push/merge")

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
        expected = {"COPY": [224, 6, 6, 224], "PASTE": [224, 25, 25, 224],
                    "CUT": [224, 27, 27, 224], "UNDO": [224, 29, 29, 224],
                    "REDO": [224, 225, 29, 29, 225, 224], "SELECT_ALL": [224, 4, 4, 224]}
        for action, values in expected.items():
            data = self.macro("A", action)["macData"]
            self.assertEqual(data["macVal"][:len(values)], values)

    def test_code_fence_is_exactly_three_ascii_backticks_and_returns_ru(self):
        data = self.macro("A", "CODE_FENCE")["macData"]
        self.assertEqual(data["macVal"][:6], [224, 225, 30, 30, 225, 224])
        self.assertEqual(data["macVal"][6:12], [53, 53, 53, 53, 53, 53])
        self.assertEqual(data["macVal"][0:data["num"]][-6:], [224, 225, 31, 31, 225, 224])
        self.assertEqual(data["num"], 18)

    def test_report_from_clipboard_structure_contains_ctrl_v_and_no_submit(self):
        data = self.macro("A", "REPORT_FROM_CLIPBOARD")["macData"]
        values = data["macVal"][:data["num"]]
        self.assertIn([224, 25, 25, 224], [values[i:i + 4] for i in range(len(values) - 3)])
        self.assertEqual(values.count(40), 6)
        self.assertEqual(values.count(53), 12)
        self.assertEqual(values[-6:], [224, 225, 31, 31, 225, 224])
        self.assertNotEqual(values[6 + len(gen.hid_events("Вот отчет", "RU")[0]) - 2:]
                            [0:2], [44, 44])

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

    def test_configurable_timing_uses_5ms_release_minimum(self):
        self.assertEqual(gen.DEFAULT_EVENT_DELAY_MS, 5)
        self.assertEqual(gen.OFFICIAL_RELEASE_MIN_DELAY_MS, 5)
        self.assertEqual(gen.validate_official_import_delay(5), 5)
        package = gen.serialize_macro("A", event_delay_ms=1)
        self.assertTrue(all(d == 1 for m in package["MacroInfo"] for d in m["macData"]["macDly"][:m["macData"]["num"]]))
        with self.assertRaises(ValueError):
            gen.serialize_macro("A", event_delay_ms=0)
        with self.assertRaisesRegex(ValueError, "research-unsafe override"):
            gen.validate_official_import_delay(1)
        self.assertEqual(gen.validate_official_import_delay(1, True), 1)


if __name__ == "__main__":
    unittest.main()
