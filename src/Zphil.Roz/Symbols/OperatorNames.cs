using System.Collections.Frozen;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Operator metadata-name helpers: recognition of <c>op_*</c> names and conversion
///     from metadata names (e.g. <c>op_Addition</c>) to source display tokens (e.g. <c>+</c>).
/// </summary>
internal static class OperatorNames
{
    private static readonly FrozenDictionary<string, string> OperatorTokenMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op_Addition"] = "+",
            ["op_Subtraction"] = "-",
            ["op_Multiply"] = "*",
            ["op_Division"] = "/",
            ["op_Modulus"] = "%",
            ["op_Equality"] = "==",
            ["op_Inequality"] = "!=",
            ["op_LessThan"] = "<",
            ["op_GreaterThan"] = ">",
            ["op_LessThanOrEqual"] = "<=",
            ["op_GreaterThanOrEqual"] = ">=",
            ["op_UnaryNegation"] = "-",
            ["op_UnaryPlus"] = "+",
            ["op_LogicalNot"] = "!",
            ["op_BitwiseAnd"] = "&",
            ["op_BitwiseOr"] = "|",
            ["op_ExclusiveOr"] = "^",
            ["op_LeftShift"] = "<<",
            ["op_RightShift"] = ">>",
            ["op_Increment"] = "++",
            ["op_Decrement"] = "--",
            ["op_True"] = "true",
            ["op_False"] = "false",
            ["op_UnsignedRightShift"] = ">>>",
            ["op_CheckedAddition"] = "checked +",
            ["op_CheckedSubtraction"] = "checked -",
            ["op_CheckedMultiply"] = "checked *",
            ["op_CheckedDivision"] = "checked /",
            ["op_CheckedUnaryNegation"] = "checked -",
            ["op_CheckedIncrement"] = "checked ++",
            ["op_CheckedDecrement"] = "checked --"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    ///     Returns <c>true</c> if <paramref name="name" /> is a known operator metadata name
    ///     (e.g. <c>op_Addition</c>, <c>op_Implicit</c>).
    /// </summary>
    public static bool IsOperatorMetadataName(string name) =>
        name.StartsWith("op_", StringComparison.Ordinal) &&
        (OperatorTokenMap.ContainsKey(name) || name is "op_Implicit" or "op_Explicit" or "op_CheckedExplicit");

    /// <summary>
    ///     Returns the source display token for an operator metadata name (e.g. <c>op_Addition</c>
    ///     → <c>+</c>), or the metadata name itself when no mapping exists.
    /// </summary>
    public static string GetDisplayToken(string metadataName) =>
        OperatorTokenMap.GetValueOrDefault(metadataName, metadataName);
}
