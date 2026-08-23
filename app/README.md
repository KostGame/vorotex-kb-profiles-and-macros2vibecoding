# VOROTEX Vibecoding Configurator

Static local-first prototype for editing official VOROTEX export/import files for the K15 Pro.

## Run locally

No build step and no local server are required.

1. Clone or pull this repository.
2. Open `app/index.html` in a modern browser.
3. Choose a VOROTEX export:
   - `.Macro.Config` for a macro group;
   - `.KB.Config` for a keyboard profile.
4. Inspect groups, macros, bindings and raw JSON.
5. Use **Export as...** to save an edited copy.
6. Import the resulting file back through the official VOROTEX application.

All parsing and editing happens locally in the browser. The application does not upload configuration files and does not write directly to the keyboard.

## v0.1 alpha scope

Implemented:

- detect and open `.Macro.Config`;
- detect and open `.KB.Config`;
- decode VOROTEX UTF-16 integer-array names;
- show macro groups and macros;
- show active `KBKeyMacro` bindings for profiles;
- show macro event arrays (`macVal`, `macSta`, `macDly`, `extVal`);
- rename an existing macro while preserving its GUID;
- apply the experimentally confirmed GUI `Cycle = 1` representation: `macRpt=1`, `rptType=0`;
- expert raw JSON editing;
- validate the supported structural subset;
- export an edited `.Macro.Config` or `.KB.Config` file.

Not implemented yet:

- automatic text-to-HID compilation;
- automatic insertion of EN `Ctrl+Shift+1` / RU `Ctrl+Shift+2` language-selector events;
- creation of new macro groups from scratch;
- visual rebinding of the 15 physical K15 keys;
- joystick/encoder editing;
- direct VOROTEX installation-file mutation;
- direct HID/device writes.

## Safety model

The configurator treats the official VOROTEX export as the source document and preserves unknown fields instead of trying to reconstruct them from assumptions. The user-facing path is:

```text
VOROTEX Export -> local configurator -> edited export -> VOROTEX Import -> K15
```

Raw forensic captures and personal live exports must not be committed to this public repository.
