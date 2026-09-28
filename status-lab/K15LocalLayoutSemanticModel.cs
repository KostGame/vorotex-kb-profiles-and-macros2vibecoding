using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal sealed record K15LocalSemanticAction(
    string ControlId,
    string StorageField,
    int StorageValue,
    byte[] ExpectedBindingRaw,
    byte? MacroMemorySlot = null,
    string? GroupGuid = null,
    string? MacroGuid = null,
    byte[]? MacroPayload = null);

internal sealed record K15LocalSemanticSnapshot(
    byte Slot,
    IReadOnlyDictionary<string, K15LocalSemanticAction> Actions);

internal static class K15LocalLayoutSemanticModel
{
    private const int MacroStorageValue = 700;
    private const int ProfileLoopStorageValue = 312;

    internal static K15LocalSemanticSnapshot Parse(
        string profileJson,
        string macroConfigJson,
        byte slot)
    {
        if (slot > 1) throw new ArgumentOutOfRangeException(nameof(slot));

        using var profile = JsonDocument.Parse(profileJson);
        using var macros = JsonDocument.Parse(macroConfigJson);

        var kbConfig = profile.RootElement.GetProperty("KBconfig");
        var keyValues = kbConfig.GetProperty("KBKey");
        var macroBindings = kbConfig.GetProperty("KBKeyMacro");
        var actions = new Dictionary<string, K15LocalSemanticAction>(StringComparer.Ordinal);

        foreach (var pair in K15LayoutAuthorityModel.StorageFields)
        {
            var controlId = pair.Key;
            var field = pair.Value;
            var storageValue = keyValues.GetProperty(field).GetInt32();

            actions[controlId] = storageValue switch
            {
                MacroStorageValue => ParseMacroAction(
                    controlId, field, macroBindings.GetProperty(field), macros.RootElement),
                ProfileLoopStorageValue => new K15LocalSemanticAction(
                    controlId, field, storageValue, new byte[] { 0x09, 0x03, 0x00, 0x00 }),
                >= 0 and < 255 => new K15LocalSemanticAction(
                    controlId, field, storageValue,
                    new byte[] { 0x02, checked((byte)storageValue), 0x00, 0x00 }),
                _ => throw new InvalidDataException(
                    $"Unsupported K15 storage value {storageValue} for {controlId}/{field}.")
            };
        }

        return new K15LocalSemanticSnapshot(slot, actions);
    }

    private static K15LocalSemanticAction ParseMacroAction(
        string controlId,
        string field,
        JsonElement binding,
        JsonElement macroRoot)
    {
        var memorySlot = binding.GetProperty("MemMacId").GetInt32();
        if (memorySlot is < 0 or > 255)
            throw new InvalidDataException($"Macro memory slot {memorySlot} is invalid for {controlId}.");

        var groupGuid = binding.GetProperty("grpGuid").GetString();
        var macroGuid = binding.GetProperty("macGuid").GetString();
        if (string.IsNullOrWhiteSpace(groupGuid) || string.IsNullOrWhiteSpace(macroGuid))
            throw new InvalidDataException($"Macro GUID binding is incomplete for {controlId}/{field}.");

        var payload = ResolveMacroPayload(macroRoot, groupGuid, macroGuid);
        return new K15LocalSemanticAction(
            controlId,
            field,
            MacroStorageValue,
            new byte[] { 0x0A, 0x00, checked((byte)memorySlot), 0x00 },
            checked((byte)memorySlot),
            groupGuid,
            macroGuid,
            payload);
    }

    private static byte[] ResolveMacroPayload(
        JsonElement root,
        string groupGuid,
        string macroGuid)
    {
        JsonElement? match = null;
        foreach (var group in root.GetProperty("MacroGrpInfo").EnumerateArray())
        {
            if (!GuidEquals(group.GetProperty("GrpGuid").GetString(), groupGuid))
                continue;

            foreach (var macro in group.GetProperty("MacroInfo").EnumerateArray())
            {
                if (!GuidEquals(macro.GetProperty("MacroGuid").GetString(), macroGuid))
                    continue;
                if (match is not null)
                    throw new InvalidDataException($"Duplicate macro GUID binding {groupGuid}/{macroGuid}.");
                match = macro;
            }
        }

        if (match is null)
            throw new InvalidDataException($"Macro GUID binding {groupGuid}/{macroGuid} was not found.");

        return EncodeMacroData(match.Value.GetProperty("macData"));
    }

    private static bool GuidEquals(string? left, string right) =>
        Guid.TryParse(left, out var l) &&
        Guid.TryParse(right, out var r) &&
        l == r;

    private static byte[] EncodeMacroData(JsonElement data)
    {
        var count = data.GetProperty("num").GetInt32();
        var states = data.GetProperty("macSta");
        var values = data.GetProperty("macVal");
        var delays = data.GetProperty("macDly");

        if (count < 0 ||
            states.GetArrayLength() < count ||
            values.GetArrayLength() < count ||
            delays.GetArrayLength() < count)
            throw new InvalidDataException("Macro active prefix exceeds its event arrays.");

        var payload = new byte[checked(count * 2)];
        for (var index = 0; index < count; index++)
        {
            var state = states[index].GetInt32();
            var value = values[index].GetInt32();
            var delay = delays[index].GetInt32();

            if (state is not (1 or 2))
                throw new InvalidDataException($"Unsupported macro state {state} at event {index}.");
            if (value is < 0 or > 255 || delay is < 0 or > 0x7F)
                throw new InvalidDataException($"Macro event {index} exceeds wire encoding bounds.");

            payload[index * 2] = checked((byte)value);
            payload[index * 2 + 1] = (byte)(delay | (state == 2 ? 0x80 : 0));
        }

        return payload;
    }
}
