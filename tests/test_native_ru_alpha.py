import json
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
        self.assertEqual(fixture["after"]["grpGuid"], gen.GROUP_GUID)

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


class SerializerTests(unittest.TestCase):
    def test_all_fifteen_macros_use_native_cycle_and_timing(self):
        package = gen.serialize_macro("RU")
        self.assertEqual(len(package["MacroInfo"]), 15)
        self.assertEqual(package["GrpGuid"], gen.GROUP_GUID)
        for macro in package["MacroInfo"]:
            data = macro["macData"]
            self.assertEqual(data["macRpt"], 1)
            self.assertEqual(data["rptType"], 0)
            self.assertTrue(all(delay == 10 for delay in data["macDly"][: data["num"]]))

    def test_new_line_is_not_plain_enter_or_submit(self):
        package = gen.serialize_macro("RU")
        macro = package["MacroInfo"][11]["macData"]
        self.assertEqual(macro["num"], 4)
        self.assertEqual(macro["macVal"][:4], [225, 40, 40, 225])
        self.assertEqual(macro["macSta"][:4], [1, 1, 2, 2])
        self.assertEqual(macro["macDly"][:4], [10, 10, 10, 10])

    def test_ru_hid_mapping_matches_native_phrase_fixture(self):
        values, states = gen.hid_events("Проверь", "RU")
        self.assertEqual(values, [10, 10, 11, 11, 13, 13, 7, 7, 23, 23, 11, 11, 16, 16])
        self.assertEqual(states, [1, 2] * 7)

    def test_kb_proven_bindings_and_unresolved_controls(self):
        package, unresolved = gen.serialize_kb()
        macros = package["KBconfig"]["KBKeyMacro"]
        self.assertEqual(macros["btn_KBKey_KeyPad1"]["MemMacId"], 2)
        self.assertEqual(macros["btn_KBKey_KeyPad2"]["MemMacId"], 1)
        self.assertEqual(macros["btn_KBKey_KeyPadEnter"]["MemMacId"], 11)
        self.assertEqual(macros["btn_KBKey_KeyPadSub"]["MemMacId"], 13)
        self.assertEqual(macros["btn_KBKey_KeyPadAdd"]["MemMacId"], 14)
        self.assertEqual(unresolved, ["Space"])
        self.assertEqual(macros["btn_KBKey_Space"]["MemMacId"], 0)
        self.assertEqual(macros["btn_KBKey_Space"]["grpGuid"], "")
        self.assertEqual(package["KBconfig"]["KBKey"]["btn_KBKey_Space"], 44)
        self.assertEqual(package["KBconfig"]["KBKey"]["btn_KBKey_Enter"], 40)
        self.assertEqual(package["KBconfig"]["KBKeyMacro"]["btn_KBKey_Enter"]["MemMacId"], 0)
        self.assertEqual(package["SingleProfile"], 1)
        self.assertEqual(len(package["MacroGrpInfo"][0]["MacroInfo"]), 15)

    def test_forced_english_profile_is_supported(self):
        package = gen.serialize_macro("EN")
        self.assertEqual(package["MacroInfo"][0]["macData"]["macVal"][:16], [224, 225, 30, 30, 225, 224, 6, 6, 11, 11, 8, 8, 6, 6, 14, 14])

    def test_russian_text_self_selects_russian_layout(self):
        package = gen.serialize_macro("RU")
        values = package["MacroInfo"][0]["macData"]["macVal"]
        states = package["MacroInfo"][0]["macData"]["macSta"]
        self.assertEqual(values[:6], [224, 225, 31, 31, 225, 224])
        self.assertEqual(states[:6], [1, 1, 1, 2, 2, 2])


if __name__ == "__main__":
    unittest.main()
