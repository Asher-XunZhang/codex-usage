using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Media;

namespace CodexUsage;

/// <summary>Only the collapsed quota arc uses this preference. Null colors follow the theme.</summary>
internal sealed record CapsuleArcStyle(string Mode = "gradient", string? SolidHex = null, string? LowHex = null, string? HighHex = null, int Version = 1)
{
    internal bool IsValid => Version == 1 && Mode is "solid" or "gradient" && Valid(SolidHex) && Valid(LowHex) && Valid(HighHex) && (LowHex is null) == (HighHex is null);
    internal bool IsBuiltinGradient => LowHex is null && HighHex is null;
    private static bool Valid(string? value) => value is null || Parse(value) is not null;
    internal static Color? Parse(string value) => value.Length == 7 && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)
        ? System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : null;
    internal static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    internal Color Endpoint(bool high, bool light) => Parse((high ? HighHex : LowHex) ?? "") ?? CapsuleColors.Color(high ? 1 : 0, light);
    internal Color SolidColor(bool light) => Parse(SolidHex ?? "") ?? CapsuleColors.Color(1, light);
    internal Color Color(double fraction, bool light)
    {
        if (!IsValid) return CapsuleColors.Color(fraction, light);
        if (Mode == "solid") return SolidColor(light);
        if (IsBuiltinGradient) return CapsuleColors.Color(fraction, light);
        var low = Endpoint(false, light); var high = Endpoint(true, light);
        double t = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return System.Windows.Media.Color.FromRgb(Mix(low.R, high.R), Mix(low.G, high.G), Mix(low.B, high.B));
    }
    internal CapsuleArcStyle SetColor(Color color, bool high, bool light) => Mode == "solid" ? this with { SolidHex = Hex(color) }
        : this with { LowHex = high ? Hex(Endpoint(false, light)) : Hex(color), HighHex = high ? Hex(color) : Hex(Endpoint(true, light)) };
    internal CapsuleArcStyle ResetColors() => Mode == "solid" ? this with { SolidHex = null } : this with { LowHex = null, HighHex = null };
    internal JsonObject ToJson() => new() { ["version"] = Version, ["mode"] = Mode, ["solidHex"] = SolidHex, ["lowHex"] = LowHex, ["highHex"] = HighHex };
    internal static CapsuleArcStyle Load(JsonObject floating, out string? warning)
    {
        warning = null;
        if (!floating.ContainsKey("arcStyle")) return new();
        if (floating["arcStyle"] is JsonObject value)
        {
            string? Read(string key) => value[key] is null ? null : value.S(key, "invalid");
            var result = new CapsuleArcStyle(value.S("mode"), Read("solidHex"), Read("lowHex"), Read("highHex"), value.I("version"));
            if (result.IsValid) return result;
        }
        warning = "已保存的配色无法读取，暂用默认颜色；应用后才会替换原配置。";
        return new();
    }
}
