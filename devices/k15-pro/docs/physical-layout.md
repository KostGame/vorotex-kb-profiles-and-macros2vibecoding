# K15 Pro physical layout

## Confirmed standard keys

| Physical ID | VOROTEX internal slot | Native meaning | Generator support |
|---|---|---|---|
| `TOP_1` | `btn_KBKey_KeyPad1` | keypad 1 slot | yes |
| `TOP_2` | `btn_KBKey_KeyPad2` | keypad 2 slot | yes |
| `TOP_3` | `btn_KBKey_KeyPad3` | keypad 3 slot | yes |
| `TOP_4` | `btn_KBKey_KeyPad4` | keypad 4 slot | yes |
| `TOP_5` | `btn_KBKey_KeyPad5` | keypad 5 slot | yes |
| `TOP_6` | `btn_KBKey_KeyPad6` | keypad 6 slot | yes |
| `MID_7` | `btn_KBKey_KeyPad7` | keypad 7 slot | yes |
| `MID_8` | `btn_KBKey_KeyPad8` | keypad 8 slot | yes |
| `MID_9` | `btn_KBKey_KeyPad9` | keypad 9 slot | yes |
| `MID_0` | `btn_KBKey_KeyPad0` | keypad 0 slot | yes |
| `MID_DOT` | `btn_KBKey_KeyPadPoint` | keypad decimal point slot | not yet |
| `MID_ENTER` | `btn_KBKey_KeyPadEnter` | keypad Enter slot | not yet |
| `BOTTOM_MINUS` | `btn_KBKey_KeyPadSub` | keypad minus slot | not yet |
| `BOTTOM_PLUS` | `btn_KBKey_KeyPadAdd` | keypad plus slot | not yet |
| `BOTTOM_SPACE` | `btn_KBKey_Space` | Space slot | not yet |

`Generator support = not yet` means the physical storage slot is confirmed, but the complete native macro-binding label/schema needed by the generator has not yet been proven.

## Important slot semantics

The `btn_KBKey_*` field name identifies a VOROTEX storage slot, not necessarily the current output of the physical key. A profile can assign an ordinary HID key or a macro to that slot.

Generic full-keyboard fields such as `btn_KBKey_1` are separate from the K15 physical keypad-backed slots and must not be substituted for them.

## Special controls

Known user-visible behavior:

- rotary encoder left/right: system volume down/up;
- rotary encoder click: hardware profile switch;
- joystick up/down/left/right: cursor navigation.

The exact physical storage fields for the encoder and joystick remain unresolved. They must not be generated from guessed mappings.

Joystick click availability/storage is also unresolved.
