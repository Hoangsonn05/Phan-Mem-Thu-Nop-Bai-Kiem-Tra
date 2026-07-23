# Device command signature canonical format

The Edge Function and C# Agent sign the same UTF-8 string with HMAC-SHA256.
Fields are joined by a single LF (`\n`) in this order: command ID, session ID,
device ID, command type, canonical payload JSON, created UTC, expiry UTC, and
issuer ID.

- UUIDs use lowercase `D` form.
- Timestamps use invariant ISO-8601 UTC with exactly seven fractional digits
  and `+00:00` (the Edge clock supplies milliseconds and pads four zeroes).
- Device/command text plus JSON property names and string values are normalized
  to Unicode NFC.
- JSON object properties are sorted ordinally after NFC normalization; arrays
  retain order; output has no insignificant whitespace.
- The lowercase signature is 64 hexadecimal characters.

`device-command-signature.json` is the shared golden fixture. Both the Deno and
.NET tests must pass whenever this format changes.
