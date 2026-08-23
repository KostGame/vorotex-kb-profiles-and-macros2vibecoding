# W909 / SXS-W909 hardware-family compatibility note

## Status

**Working classification:** VOROTEX K15 Pro is treated by this project as a **VOROTEX-branded hardware analogue of the W909 / SXS-W909 family**.

This classification is useful for finding manuals, listings, teardown clues, control behavior, and future compatibility leads. It is not a claim of proven binary compatibility.

## Why the family match is plausible

The independently documented W909-family design has the same distinctive combination used by K15 Pro research in this repository:

- 15 programmable mechanical keys;
- hot-swappable switches;
- RGB lighting;
- USB-C wired mode;
- Bluetooth mode;
- 2.4 GHz wireless mode;
- rotary knob/encoder;
- 5-way joystick.

Public references describing W909/SXS-W909 variants:

- Adventurers W909 manual mirror: https://manuals.plus/asin/B0F4D4SVD9
- JOMAA W909 manual mirror: https://manuals.plus/ae/1005008749908942
- JOMAA SXS-W909 manual mirror: https://manuals.plus/ae/1005009731536663
- W909 retail specification example: https://www.neweggbusiness.com/p/9B-0GA-09A5-00008

The K15-side control/storage facts remain those proven and documented locally in this repository.

## What "analogue" means here

Allowed inference:

```text
same/closely related physical platform is plausible
-> W909-family documentation is useful research input
```

Not allowed without direct evidence:

```text
same-looking hardware
-> same firmware
-> same driver
-> same VID/PID
-> same Bluetooth identity
-> same JSON schema
-> same RGB protocol
-> same device-write protocol
```

## Compatibility matrix

| Area | Current status |
|---|---|
| Overall 15-key form factor | strong visual/functional family match |
| Tri-mode connectivity | family match |
| Rotary control | family match |
| 5-way joystick | family match |
| Hot-swappable mechanical switches | family match |
| Native K15 storage fields | proven only for K15 research |
| Vendor configuration JSON compatibility | unproven |
| Driver interchangeability | unproven |
| Firmware interchangeability | unproven; do not attempt casually |
| USB/Bluetooth identifiers | unproven |
| RGB protocol | unproven |
| Low-level device-write protocol | unproven |

## Research rule

W909-family information may be used to generate **hypotheses**, names to search for, and read-only verification steps.

It must not be used as the sole justification for flashing firmware, installing an unknown driver, overwriting a K15 configuration, or sending guessed device commands.

Any newly proven equivalence should be recorded separately with reproducible evidence and an explicit proof status.
